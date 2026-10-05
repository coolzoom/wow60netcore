using FrameXml;
using FrameXml.Lua;
using MoonSharp.Interpreter;
using Net;
using static FrameXml.Lua.LuaBindings;

namespace Client.Online;

/// <summary>
/// Connects the GlueXML screens to a <see cref="GameSession"/>: the login, realm list, character select and
/// character create functions the scripts call, and the events they wait for.
/// </summary>
public sealed class GlueNetwork : IDisposable
{
    private readonly GlueApi _api;
    private readonly GameSession _session;
    private readonly CharacterCustomizer _customizer;
    private readonly ClientData _data;
    private UiScreen _ui = null!;
    private bool _autoConnect;
    private bool _freshList;
    private bool _realmChanged;
    private int _selected = 1;

    public GlueNetwork(GlueApi api, GameSession session, ClientData data)
    {
        _api = api;
        _session = session;
        _data = data;
        _customizer = new CharacterCustomizer(data);
        api.Registered = Register;

        session.Progress += OnProgress;
        session.Failed += OnFailed;
        session.RealmsReceived += OnRealms;
        session.CharactersReceived += OnCharacters;
        session.CharacterCreated += OnCreated;
        session.CharacterDeleted += OnDeleted;
        session.Disconnected += OnDisconnected;
    }

    /// <summary>Enter the world with the first character as soon as the list arrives (scripted test runs).</summary>
    public bool AutoEnterWorld { get; set; }

    /// <summary>The character being created, as the create screen's scene shows it.</summary>
    public Appearance CreateLook => _customizer.Look;

    /// <summary>The character highlighted on the select screen, with what it wears.</summary>
    public Appearance? SelectedLook => _session.Characters.ElementAtOrDefault(_selected - 1) is { } c
        ? new Appearance(c.Race, c.Gender, c.Skin, c.Face, c.HairStyle, c.HairColor, c.FacialHair, null,
            [.. c.Equipment.Take(19).Select(e => (int)e.DisplayId)])
        : null;

    /// <summary>The selected character entered the world.</summary>
    public event Action<WorldEntry>? EnteredWorld
    {
        add => _session.EnteredWorld += value;
        remove => _session.EnteredWorld -= value;
    }

    public void Dispose()
    {
        _session.Progress -= OnProgress;
        _session.Failed -= OnFailed;
        _session.RealmsReceived -= OnRealms;
        _session.CharactersReceived -= OnCharacters;
        _session.CharacterCreated -= OnCreated;
        _session.CharacterDeleted -= OnDeleted;
        _session.Disconnected -= OnDisconnected;
    }

    /// <summary>Logon server from the realmList CVar ("host" or "host:port").</summary>
    private (string Host, int Port) LogonServer()
    {
        var realmList = (_api.GetCVar("realmList") ?? "").Trim();
        var colon = realmList.LastIndexOf(':');
        return colon > 0 && int.TryParse(realmList[(colon + 1)..], out var port)
            ? (realmList[..colon], port)
            : (realmList.Length > 0 ? realmList : "127.0.0.1", AuthClient.DefaultPort);
    }

    private void Register(UiScreen ui)
    {
        _ui = ui;

        Fn("DefaultServerLogin", a => { Login(a.Str(0) ?? "", a.Str(1) ?? ""); return DynValue.Nil; });
        Fn("StatusDialogClick", _ =>
        {
            if (_session.State is SessionState.Authenticating or SessionState.ConnectingWorld)
            {
                var wasLoggingIn = _session.State == SessionState.Authenticating;
                _session.Cancel();
                if (!wasLoggingIn)
                    ShowRealmList();
            }
            return DynValue.Nil;
        });
        Fn("DisconnectFromServer", _ => { _session.Disconnect(); return DynValue.Nil; });
        Fn("IsConnectedToServer", _ => B(_session.IsWorldConnected));
        Fn("GetServerName", _ => _session.Realm is { } realm ? Tuple(S(realm.Name), B(realm.IsPvp), B(realm.IsRp)) : DynValue.Nil);

        Fn("RequestRealmList", _ => { _session.RequestRealmList(); return DynValue.Nil; });
        Fn("GetRealmCategories", _ => S(_ui.LocalizedText("REALM_LIST")));
        Fn("GetSelectedCategory", _ => N(1));
        Fn("GetNumRealms", _ => N(_session.Realms.Count));
        Fn("GetRealmInfo", a =>
        {
            if (_session.Realms.ElementAtOrDefault(a.Int(1) - 1) is not { } realm)
                return DynValue.Nil;
            var load = realm.Population switch { >= 2f => 2.0, >= 1.5f => 1.0, >= 0.5f => 0.0, _ => -1.0 };
            return Tuple(S(realm.Name), N(realm.Characters), B(false), B(realm.IsDown), B(realm == _session.Realm),
                B(realm.IsPvp), B(realm.IsRp), N(load));
        });
        Fn("ChangeRealm", a =>
        {
            if (_session.Realms.ElementAtOrDefault(a.Int(1) - 1) is { } realm)
                Connect(realm);
            return DynValue.Nil;
        });

        Fn("GetCharacterListUpdate", _ =>
        {
            if (_freshList)
            {
                _freshList = false;
                _ui.FireEvent("CHARACTER_LIST_UPDATE");
            }
            else if (_session.IsWorldConnected)
                _session.RequestCharacters();
            return DynValue.Nil;
        });
        Fn("GetNumCharacters", _ => N(_session.Characters.Count));
        Fn("GetCharacterInfo", a =>
        {
            if (_session.Characters.ElementAtOrDefault(a.Int(0) - 1) is not { } c)
                return DynValue.Nil;
            var race = _data.Race(c.Race);
            return Tuple(S(c.Name), S(race?.Name ?? ""), S(_data.Class(c.Class)?.Name ?? ""), N(c.Level), S(_data.AreaName((int)c.Zone)),
                S(race?.FileString ?? ""), N(c.Gender), B(c.IsGhost));
        });
        Fn("SelectCharacter", a =>
        {
            _selected = a.Int(0);
            _ui.FireEvent("UPDATE_SELECTED_CHARACTER", _selected);
            return DynValue.Nil;
        });
        Fn("DeleteCharacter", a =>
        {
            if (_session.Characters.ElementAtOrDefault(a.Int(0) - 1) is { } c)
                _session.DeleteCharacter(c.Guid);
            return DynValue.Nil;
        });
        Fn("EnterWorld", _ =>
        {
            if (_session.Characters.ElementAtOrDefault(_selected - 1) is { } c)
                _session.EnterWorld(c.Guid);
            return DynValue.Nil;
        });

        Fn("ResetCharCustomize", _ => { _customizer.Reset(); return DynValue.Nil; });
        Fn("RandomizeCharCustomization", _ => { _customizer.Randomize(); return DynValue.Nil; });
        Fn("CycleCharCustomization", a => { _customizer.Cycle(a.Int(0), a.Int(1)); return DynValue.Nil; });
        Fn("GetAvailableRaces", _ => Tuple(_customizer.Races.SelectMany(r => new[] { S(r.Name), S(r.FileString) }).ToArray()));
        Fn("GetClassesForRace", _ => Tuple(_customizer.Classes.SelectMany(c => new[] { S(c.Name), S(c.FileString.ToUpperInvariant()) }).ToArray()));
        Fn("SetSelectedRace", a => { _customizer.SetRace(a.Int(0) - 1); return DynValue.Nil; });
        Fn("SetSelectedSex", a => { _customizer.SetSex(a.Int(0) - 1); return DynValue.Nil; });
        Fn("SetSelectedClass", a => { _customizer.SetClass(a.Int(0) - 1); return DynValue.Nil; });
        Fn("GetSelectedRace", _ => N(_customizer.RaceIndex + 1));
        Fn("GetSelectedSex", _ => N(_customizer.Sex + 1));
        Fn("GetSelectedClass", _ => Tuple(S(_customizer.Class.Name), S(_customizer.Class.FileString.ToUpperInvariant()), N(_customizer.ClassIndex + 1)));
        Fn("GetFactionForRace", _ =>
        {
            var faction = _customizer.Race.Faction;
            return Tuple(S(_ui.LocalizedText(faction.ToUpperInvariant())), S(faction));
        });
        Fn("GetNameForRace", _ => Tuple(S(_customizer.Race.Name), S(_customizer.Race.FileString)));
        Fn("GetFacialHairCustomization", _ => S(_customizer.FacialHairKind is { Length: > 0 } kind ? kind : "NONE"));
        Fn("GetHairCustomization", _ => S(_customizer.Race.Hair is { Length: > 0 } hair ? hair : "NORMAL"));
        Fn("CreateCharacter", a =>
        {
            var name = (a.Str(0) ?? "").Trim();
            if (name.Length == 0)
                _ui.FireEvent("OPEN_STATUS_DIALOG", "OKAY", _ui.LocalizedText("CHAR_CREATE_INVALID_NAME"));
            else
                _session.CreateCharacter(_customizer.Build(name));
            return DynValue.Nil;
        });
    }

    public void Login(string account, string password)
    {
        var (host, port) = LogonServer();
        _autoConnect = true;
        _session.Login(host, port, account, password, _api.GetCVar("locale") ?? "zhCN");
    }

    private void Connect(RealmInfo realm)
    {
        _api.SetCVar("realmName", realm.Name);
        _realmChanged = true;
        _session.ConnectRealm(realm);
    }

    private void ShowRealmList()
    {
        if (_ui.CurrentScreen != "charselect")
            _ui.SetGlueScreen("charselect");
        _ui.FireEvent("OPEN_REALM_LIST");
    }

    private void OnProgress(string token) => _ui.FireEvent("OPEN_STATUS_DIALOG", "CANCEL", _ui.LocalizedText(token));

    private void OnFailed(string token)
    {
        _ui.FireEvent("OPEN_STATUS_DIALOG", "OKAY", _ui.LocalizedText(token));
        if (_session.State == SessionState.Disconnected && _ui.CurrentScreen != "login")
            _ui.SetGlueScreen("login");
    }

    private void OnRealms()
    {
        if (!_autoConnect)
        {
            _ui.FireEvent("OPEN_REALM_LIST");
            return;
        }
        _autoConnect = false;
        var realms = _session.Realms;
        var saved = _api.GetCVar("realmName");
        var realm = realms.FirstOrDefault(r => r.Name == saved && !r.IsDown) ?? (realms.Count == 1 ? realms[0] : null);
        if (realm is not null)
            Connect(realm);
        else
        {
            _ui.FireEvent("CLOSE_STATUS_DIALOG");
            ShowRealmList();
        }
    }

    private void OnCharacters()
    {
        _ui.FireEvent("CLOSE_STATUS_DIALOG");
        if (AutoEnterWorld && _session.Characters.Count > 0)
        {
            AutoEnterWorld = false;
            _session.EnterWorld(_session.Characters[0].Guid);
            return;
        }
        if (_ui.CurrentScreen != "charselect" || _realmChanged)
        {
            // Showing the screen again refreshes the realm name and connection state as well as the list.
            _realmChanged = false;
            _freshList = true;
            _ui.SetGlueScreen("charselect");
        }
        else
            _ui.FireEvent("CHARACTER_LIST_UPDATE");
    }

    private void OnCreated(ResponseCode code)
    {
        if (code != ResponseCode.CHAR_CREATE_SUCCESS)
        {
            _ui.FireEvent("OPEN_STATUS_DIALOG", "OKAY", _ui.LocalizedText(code.ToString()));
            return;
        }
        _ui.FireEvent("CLOSE_STATUS_DIALOG");
        _ui.FireEvent("SELECT_LAST_CHARACTER");
        _ui.SetGlueScreen("charselect");
    }

    private void OnDeleted(ResponseCode code)
    {
        if (code != ResponseCode.CHAR_DELETE_SUCCESS)
        {
            _ui.FireEvent("OPEN_STATUS_DIALOG", "OKAY", _ui.LocalizedText(code.ToString()));
            return;
        }
        _selected = 1;
        _session.RequestCharacters();
    }

    private void OnDisconnected(string reason)
    {
        Console.WriteLine($"Disconnected: {reason}");
        _session.Disconnect();
        _ui.FireEvent("DISCONNECTED_FROM_SERVER");
    }

    private void Fn(string name, Func<LuaArgs, DynValue> body) =>
        _ui.Lua.Globals[name] = DynValue.NewCallback((_, args) => body(new LuaArgs(args, 0)), name);
}
