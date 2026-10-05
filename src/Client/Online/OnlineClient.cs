using Formats.Mpq;
using Net;

namespace Client.Online;

/// <summary>
/// One connection to a server across screens: the glue screens hand over to the world on entering it, and the
/// world hands back to character select on logout (or to login when the connection drops).
/// </summary>
public sealed class OnlineClient : IDisposable
{
    private readonly string _dataDirectory;
    private readonly bool _looseFiles;
    private readonly bool _acceptAgreements;
    private ClientData? _data;
    private GameText? _text;

    public OnlineClient(string dataDirectory, bool looseFiles, bool acceptAgreements, string? realmList)
    {
        _dataDirectory = dataDirectory;
        _looseFiles = looseFiles;
        _acceptAgreements = acceptAgreements;
        RealmList = realmList;
        Gameplay = new Gameplay(Session);
    }

    public GameSession Session { get; } = new();
    /// <summary>Spells, combat, items and loot; listens from login on so nothing sent before the world screen is lost.</summary>
    public Gameplay Gameplay { get; }
    /// <summary>Overrides the realmList from realmlist.wtf when set.</summary>
    public string? RealmList { get; }

    /// <summary>"account:password" to log in with right away (scripted test runs).</summary>
    public string? AutoLogin { get; init; }
    public bool AutoEnterWorld { get; init; }
    /// <summary>Scripted combat test once in the world (see <see cref="OnlineWorld.AutoFight"/>).</summary>
    public bool AutoFight { get; init; }
    /// <summary>Lua run in the in-game interface once the player is in the world (UI test).</summary>
    public string? UiScript { get; init; }

    /// <summary>DBC tables, read once from whichever screen gets there first.</summary>
    public ClientData Data(MpqFileSystem files) => _data ??= new ClientData(files);

    /// <summary>Spell, item and faction tables plus the in-game UI strings.</summary>
    public GameText Text(MpqFileSystem files) => _text ??= new GameText(files);

    /// <param name="screen">"login", "charselect", or "disconnected" (login with the disconnected notice).</param>
    public GlueGame Glue(string screen = "login") => new(_dataDirectory, _looseFiles, _acceptAgreements) { Online = this, StartScreen = screen };

    public WorldGame World(string mapDirectory) => new(_dataDirectory, mapDirectory, Session.Location?.Position) { Online = this };

    public void Dispose() => Session.Dispose();
}
