using System.Numerics;
using Client;
using Client.Online;
using Client.World;
using Engine;
using Formats.Mpq;

// Usage: dotnet run --project src/Client -- [--data <Data dir>] [--map <Directory>] [--pos <x,y,z>] [--browse <filter>]
//        [--fly] [--no-fog] [--radius <n|all>] [--distance <yards|inf>]
//        [--procedural] [--seed <n>] [--frames <n> [--screenshot <file.bmp>]]
//        dotnet run --project src/Client -- --glue [--no-loose] [--accept-eula] [--realmlist <host[:port]>] [--data <Data dir>]
//          --glue: the login screens from Interface\GlueXML (TOC + XML + Lua), with loose files from the game
//          directory layered over the MPQs unless --no-loose; --accept-eula skips the EULA/TOS pages.
//          Logs in to the server in realmlist.wtf (or --realmlist), then character select/create and the world.
//          [--login <account:password> [--enter-world [--auto-fight]]] logs in (and enters with the first character)
//          unattended; --auto-fight then runs to the nearest non-friendly creature, fights and loots it (combat test).
//          [--ui-script <lua>] runs Lua in the in-game interface once the player is in the world (e.g. "ToggleBackpack()").
//          [--glue-screen <charselect|charcreate|disconnected>] opens that screen first instead of the login.
var options = new Dictionary<string, string>();
var flags = new HashSet<string>();
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--"))
        continue;
    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
        options[args[i]] = args[++i];
    else
        flags.Add(args[i]);
}

int? frames = options.TryGetValue("--frames", out var f) ? int.Parse(f) : null;
var gameOptions = new GameOptions("NetCore Client Prototype", 1440, 900, frames, options.GetValueOrDefault("--screenshot"));

var dataDirectory = options.GetValueOrDefault("--data")
    ?? MpqFileSystem.FindDataDirectory(Environment.CurrentDirectory)
    ?? MpqFileSystem.FindDataDirectory(AppContext.BaseDirectory);

if (flags.Contains("--procedural") || dataDirectory is null)
{
    if (dataDirectory is null)
        Console.WriteLine("No client Data/*.MPQ found; starting the procedural scene. Pass --data <dir> to load game data.");
    using var procedural = new ClientGame(options.TryGetValue("--seed", out var seed) ? int.Parse(seed) : 1121);
    procedural.Run(gameOptions);
    return;
}

if (flags.Contains("--glue"))
{
    using var online = new OnlineClient(dataDirectory, looseFiles: !flags.Contains("--no-loose"), acceptAgreements: flags.Contains("--accept-eula"),
        options.GetValueOrDefault("--realmlist"))
    {
        AutoLogin = options.GetValueOrDefault("--login"),
        AutoEnterWorld = flags.Contains("--enter-world"),
        AutoFight = flags.Contains("--auto-fight"),
        UiScript = options.GetValueOrDefault("--ui-script"),
    };
    using var glue = online.Glue(options.GetValueOrDefault("--glue-screen", "login"));
    glue.Run(gameOptions with { Title = "NetCore Client" });
    return;
}

var map = options.GetValueOrDefault("--map", "Azeroth");
Vector3? spawn = options.TryGetValue("--pos", out var pos)
    ? ParseVector(pos)
    : map.Equals("Azeroth", StringComparison.OrdinalIgnoreCase) ? new Vector3(-9464f, 62f, 56f) : null;

using var game = new WorldGame(dataDirectory, map, spawn, options.GetValueOrDefault("--browse"));
game.FogEnabled = !flags.Contains("--no-fog");
game.Flying = flags.Contains("--fly");
if (options.TryGetValue("--radius", out var radius))
    game.Settings = game.Settings with { LoadRadius = radius == "all" ? RenderSettings.AllTiles : int.Parse(radius) };
if (options.TryGetValue("--distance", out var distance))
    game.Settings = game.Settings with { DoodadDistance = distance == "inf" ? float.PositiveInfinity : float.Parse(distance) };
game.Run(gameOptions);

static Vector3 ParseVector(string text)
{
    var parts = text.Split(',').Select(p => float.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    return new Vector3(parts[0], parts[1], parts.Length > 2 ? parts[2] : 0);
}
