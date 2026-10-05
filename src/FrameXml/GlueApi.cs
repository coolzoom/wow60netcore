using System.Text.RegularExpressions;
using FrameXml.Lua;
using MoonSharp.Interpreter;
using static FrameXml.Lua.LuaBindings;

namespace FrameXml;

/// <summary>
/// C functions the glue screens call (registered by FUN_0046abb0 and the glue sub-screens). Without a realm
/// connection most return "nothing yet"; the host hooks the callbacks to connect, play sound, quit, and so on.
/// </summary>
public sealed partial class GlueApi
{
    /// <summary>Called functions that only matter with a server connection or 3D scene.</summary>
    private static readonly string[] NoOps =
    [
        "AcceptContest", "CancelRealmListQuery", "ChangeRealm", "CloseMenus", "CreateCharacter", "CycleCharCustomization",
        "DeleteCharacter", "EnterWorld", "GetCharacterListUpdate", "HideCursor", "PatchDownloadApply", "PatchDownloadCancel",
        "PINEntered", "RandomizeCharCustomization", "RealmListDialogCancelled", "RenameCharacter", "RequestRealmList",
        "ResetCharCustomize", "ResetRaceSelect", "SelectCharacter",
        "SetPreferredInfo", "SetRaceSelectFrame", "SetScriptMemory", "SetSelectedClass", "SetSelectedRace", "SetSelectedSex",
        "ShowCursor", "SortRealms", "SurveyNotificationDone", "UpdateCustomizationScene", "UpdateRaceHighlight",
        "UpdateSelectionCustomizationScene", "SetAddonVersionCheck",
    ];

    private readonly Dictionary<string, string> _cvars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["realmList"] = "",
        ["accountName"] = "",
        ["gxResolution"] = "1024x768",
        ["locale"] = "zhCN",
        ["readTOS"] = "0",
        ["readEULA"] = "0",
        ["readScanning"] = "0",
        ["readContest"] = "0",
    };

    private UiScreen? _ui;
    private List<AddOnInfo> _addOns = [];

    public IReadOnlyDictionary<string, string> CVars => _cvars;

    /// <summary>The model frame showing the character select scene (SetCharSelectModelFrame).</summary>
    public Objects.Model? CharSelectFrame { get; private set; }
    /// <summary>The model frame showing the character create scene (SetCharCustomizeFrame).</summary>
    public Objects.Model? CharCustomizeFrame { get; private set; }
    /// <summary>Character facing in degrees on the select and create screens.</summary>
    public float CharacterSelectFacing { get; set; }
    public float CharacterCreateFacing { get; set; }

    public Action<string, string>? LoginRequested { get; set; }
    public Action? QuitRequested { get; set; }
    public Action<string>? UrlRequested { get; set; }
    public Action<string>? SoundRequested { get; set; }
    public Action<string?>? MusicRequested { get; set; }
    public Action<string, string>? CVarChanged { get; set; }
    /// <summary>Runs after the built-in functions are registered and before GlueXML loads, so the host can replace them.</summary>
    public Action<UiScreen>? Registered { get; set; }

    public string? GetCVar(string name) => _cvars.GetValueOrDefault(name);

    public void SetCVar(string name, string value)
    {
        _cvars[name] = value;
        CVarChanged?.Invoke(name, value);
    }

    /// <summary>Marks the EULA, terms of use and scanning notice as read, as an accepted install has in Config.wtf.</summary>
    public void AcceptAgreements()
    {
        foreach (var name in new[] { "readTOS", "readEULA", "readScanning", "readContest" })
            _cvars[name] = "1";
    }

    /// <summary>Reads realmlist.wtf and WTF\Config.wtf ("SET name "value"" lines) from a game directory.</summary>
    public void LoadConfig(string gameDirectory)
    {
        foreach (var file in new[] { Path.Combine(gameDirectory, "realmlist.wtf"), Path.Combine(gameDirectory, "WTF", "Config.wtf") })
            if (File.Exists(file))
                LoadConfigText(File.ReadAllText(file));
    }

    public void LoadConfigText(string text)
    {
        foreach (Match match in ConfigLine().Matches(text))
            _cvars[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
    }

    [GeneratedRegex("""^\s*set\s+(\S+)\s+(?:"([^"]*)"|(\S+))""", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ConfigLine();

    public void Register(UiScreen ui)
    {
        _ui = ui;
        _addOns = AddOns.Discover(ui.Files);
        var g = ui.Lua.Globals;

        Fn("GetCVar", a => S(GetCVar(a.Str(0) ?? "")));
        Fn("SetCVar", a => { SetCVar(a.Str(0) ?? "", a.Str(1) ?? ""); return DynValue.Nil; });
        Fn("RegisterCVar", a => { _cvars.TryAdd(a.Str(0) ?? "", a.Str(1) ?? ""); return DynValue.Nil; });
        Fn("GetBuildInfo", _ => Tuple(S("WoW"), S("Release"), S("1.12.1"), S("5875"), S("Sep 19 2006")));
        Fn("GetScriptMemory", _ => N(0));
        Fn("SetCurrentScreen", a => { ui.CurrentScreen = a.Str(0); return DynValue.Nil; });

        Fn("PlaySound", a => { SoundRequested?.Invoke(a.Str(0) ?? ""); return DynValue.Nil; });
        Fn("PlayGlueMusic", a => { MusicRequested?.Invoke(a.Str(0)); return DynValue.Nil; });
        Fn("PlayCreditsMusic", _ => { MusicRequested?.Invoke(@"Sound\Music\GlueScreenMusic\wow_main_theme.mp3"); return DynValue.Nil; });
        Fn("StopGlueMusic", _ => { MusicRequested?.Invoke(null); return DynValue.Nil; });
        Fn("QuitGame", _ => { QuitRequested?.Invoke(); return DynValue.Nil; });
        Fn("LaunchURL", a => { UrlRequested?.Invoke(a.Str(0) ?? ""); return DynValue.Nil; });
        Fn("LaunchAddOnURL", a => { UrlRequested?.Invoke(_addOns.ElementAtOrDefault(a.Int(0) - 1)?.Toc["URL"] ?? ""); return DynValue.Nil; });
        Fn("Screenshot", _ => { ui.FireEvent("GLUE_SCREENSHOT_FAILED"); return DynValue.Nil; });

        Fn("GetSavedAccountName", _ => S(GetCVar("accountName") ?? ""));
        Fn("SetSavedAccountName", a => { SetCVar("accountName", a.Str(0) ?? ""); return DynValue.Nil; });
        Fn("GetServerName", _ => DynValue.Nil);
        Fn("IsConnectedToServer", _ => DynValue.Nil);
        Fn("DefaultServerLogin", a =>
        {
            var account = a.Str(0) ?? "";
            var password = a.Str(1) ?? "";
            if (LoginRequested is { } login)
                login(account, password);
            else
                ui.FireEvent("OPEN_STATUS_DIALOG", "CANCEL", ui.LocalizedText("LOGIN_STATE_CONNECTING"));
            return DynValue.Nil;
        });
        Fn("DisconnectFromServer", _ => DynValue.Nil);
        Fn("StatusDialogClick", _ => { ui.FireEvent("CLOSE_STATUS_DIALOG"); return DynValue.Nil; });

        Agreement("TOS", "readTOS");
        Agreement("EULA", "readEULA");
        Agreement("Scanning", "readScanning");
        Agreement("Contest", "readContest");

        Fn("GetNumAddOns", _ => N(_addOns.Count));
        Fn("GetAddOnInfo", a =>
        {
            var addOn = AddOnArg(a);
            if (addOn is null)
                return DynValue.Nil;
            var outOfDate = addOn.Toc.InterfaceVersion != AddOns.InterfaceVersion;
            return Tuple(S(addOn.Name), S(addOn.Title), S(addOn.Notes), S(addOn.Toc["URL"]),
                B(addOn.Enabled && !outOfDate), S(outOfDate ? "INTERFACE_VERSION" : addOn.Enabled ? null : "DISABLED"),
                S(addOn.Secure ? "SECURE" : "INSECURE"), DynValue.Nil);
        });
        Fn("GetAddOnDependencies", a => AddOnArg(a) is { } addOn ? Tuple(addOn.Toc.Dependencies.Select(S).ToArray()) : DynValue.Nil);
        Fn("GetAddOnEnableState", a => AddOnArg(a, 1) is { Enabled: true } ? N(2) : N(0));
        Fn("EnableAddOn", a => { if (AddOnArg(a) is { } addOn) addOn.Enabled = true; return DynValue.Nil; });
        Fn("DisableAddOn", a => { if (AddOnArg(a) is { } addOn) addOn.Enabled = false; return DynValue.Nil; });
        Fn("EnableAllAddOns", _ => { _addOns.ForEach(x => x.Enabled = true); return DynValue.Nil; });
        Fn("DisableAllAddOns", _ => { _addOns.ForEach(x => x.Enabled = false); return DynValue.Nil; });
        Fn("SaveAddOns", _ => DynValue.Nil);
        Fn("ResetAddOns", _ => DynValue.Nil);
        Fn("IsAddonVersionCheckEnabled", _ => B(true));

        Fn("GetNumCharacters", _ => N(0));
        Fn("GetCharacterInfo", _ => DynValue.Nil);
        Fn("GetNumRealms", _ => N(0));
        Fn("GetRealmInfo", _ => DynValue.Nil);
        Fn("GetRealmCategories", _ => DynValue.Nil);
        Fn("GetSelectedCategory", _ => N(1));
        Fn("GetBillingPlan", _ => N(0));
        Fn("GetBillingTimeRemaining", _ => N(0));
        Fn("GetMovieResolution", _ => N(800));
        Fn("GetMovieSubtitles", _ => DynValue.Nil);
        Fn("PatchDownloadProgress", _ => Tuple(N(0), N(0)));
        Fn("SetCharSelectModelFrame", a => { CharSelectFrame = ui.FindFrame(a.Str(0) ?? "") as Objects.Model; return DynValue.Nil; });
        Fn("SetCharCustomizeFrame", a => { CharCustomizeFrame = ui.FindFrame(a.Str(0) ?? "") as Objects.Model; return DynValue.Nil; });
        Fn("SetCharSelectBackground", a => { if (CharSelectFrame is { } frame) frame.ModelFile = a.Str(0); return DynValue.Nil; });
        Fn("SetCharCustomizeBackground", a => { if (CharCustomizeFrame is { } frame) frame.ModelFile = a.Str(0); return DynValue.Nil; });
        Fn("GetCharacterSelectFacing", _ => N(CharacterSelectFacing));
        Fn("SetCharacterSelectFacing", a => { CharacterSelectFacing = Facing(a.F(0)); return DynValue.Nil; });
        Fn("GetCharacterCreateFacing", _ => N(CharacterCreateFacing));
        Fn("SetCharacterCreateFacing", a => { CharacterCreateFacing = Facing(a.F(0)); return DynValue.Nil; });
        Fn("GetRandomName", _ => S(""));
        Fn("GetAvailableRaces", _ => Tuple(S("Human"), S("HUMAN"), S("Dwarf"), S("DWARF"), S("Night Elf"), S("NIGHTELF"),
            S("Gnome"), S("GNOME"), S("Orc"), S("ORC"), S("Undead"), S("SCOURGE"), S("Tauren"), S("TAUREN"), S("Troll"), S("TROLL")));
        Fn("GetClassesForRace", _ => Tuple(S("Warrior"), S("WARRIOR")));
        Fn("GetSelectedRace", _ => N(1));
        Fn("GetSelectedSex", _ => N(2));
        Fn("GetSelectedClass", _ => Tuple(S("Warrior"), S("WARRIOR"), N(1)));
        Fn("GetFactionForRace", _ => S("Alliance"));
        Fn("GetNameForRace", _ => Tuple(S("Human"), S("HUMAN")));
        Fn("GetFacialHairCustomization", _ => S("NORMAL"));
        Fn("ShowUIPanel", a => { ui.Bindings.ObjectOf<Objects.Frame>(a[0])?.Show(); return DynValue.Nil; });
        Fn("HideUIPanel", a => { ui.Bindings.ObjectOf<Objects.Frame>(a[0])?.Hide(); return DynValue.Nil; });

        foreach (var name in NoOps)
            if (g.Get(name).IsNil())
                Fn(name, _ => DynValue.Nil);
        Registered?.Invoke(ui);
    }

    /// <summary>Degrees wrapped to [0, 360), as the client keeps them.</summary>
    private static float Facing(float degrees) => (degrees % 360 + 360) % 360;

    private void Agreement(string name, string cvar)
    {
        Fn($"{name}Accepted", _ => B(GetCVar(cvar) == "1"));
        Fn($"Accept{name}", _ => { SetCVar(cvar, "1"); return DynValue.Nil; });
        Fn($"Show{name}Notice", _ => B(GetCVar(cvar) != "1"));
    }

    private AddOnInfo? AddOnArg(LuaArgs a, int index = 0) => a.IsNumber(index)
        ? _addOns.ElementAtOrDefault(a.Int(index) - 1)
        : _addOns.FirstOrDefault(x => x.Name.Equals(a.Str(index), StringComparison.OrdinalIgnoreCase));

    private void Fn(string name, Func<LuaArgs, DynValue> body) =>
        _ui!.Lua.Globals[name] = DynValue.NewCallback((_, args) => body(new LuaArgs(args, 0)), name);
}
