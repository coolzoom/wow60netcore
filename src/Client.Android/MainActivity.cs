using System.Numerics;
using Android.App;
using Android.Content.PM;
using Engine;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl.Android;

namespace Client;

/// <summary>
/// Android entry point: SDL hosts an OpenGL ES 3.0 surface and runs the same games as the desktop client.
/// Game data goes in the app's external files directory: /sdcard/Android/data/org.netcoreclient.wow/files/Data/*.MPQ
/// (android.sh 4 pushes it). Intent extras: mode = glue (default: the full client, login to world) | world | procedural,
/// map = map directory for world mode, realmlist = logon server host[:port] for glue mode (otherwise realmlist.wtf),
/// login = "account:password" to log in with right away in glue mode.
/// </summary>
[Activity(Name = "org.netcoreclient.wow.MainActivity", Label = "WoW NetCore", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public sealed class MainActivity : SilkActivity
{
    private static readonly Vector3 Goldshire = new(-9464f, 62f, 56f);

    protected override void OnRun()
    {
        try
        {
            Run();
        }
        catch (Exception e)
        {
            // The runtime's own crash report only names the outer exception (e.g. TargetInvocationException).
            Console.WriteLine($"Fatal: {e}");
            throw;
        }
    }

    private void Run()
    {
        var root = GetExternalFilesDir(null)!.AbsolutePath;
        // Only the app's own directory: walking up (as on desktop) reaches other apps' storage, which is off limits.
        string? data = Path.Combine(root, "Data");
        try
        {
            if (!Directory.Exists(data) || !Directory.EnumerateFiles(data, "*.MPQ").Any())
                data = null;
        }
        catch (UnauthorizedAccessException e)
        {
            Console.WriteLine($"{data} is not readable by the app ({e.Message}); files pushed as root need chown to the app's uid.");
            data = null;
        }
        var mode = Intent?.GetStringExtra("mode") ?? "glue";
        var map = Intent?.GetStringExtra("map") ?? "Azeroth";
        Console.WriteLine(data is null
            ? $"No Data/*.MPQ under {root}; starting the procedural scene."
            : $"Game data: {data}, mode {mode}");

        var options = new GameOptions("WoW NetCore", UiScale: Resources?.DisplayMetrics?.Density ?? 1f, TouchControls: true);
        var view = Silk.NET.Windowing.Window.GetView(ViewOptions.Default with { API = Game.MobileApi, VSync = true });
        using var online = data is not null && mode == "glue"
            ? new Online.OnlineClient(data, looseFiles: false, acceptAgreements: true, Intent?.GetStringExtra("realmlist"))
            {
                AutoLogin = Intent?.GetStringExtra("login"),
            }
            : null;
        using Game game = data is null || mode == "procedural" ? new ClientGame(1121)
            : online is not null ? online.Glue()
            : new WorldGame(data, map, map.Equals("Azeroth", StringComparison.OrdinalIgnoreCase) ? Goldshire : null);
        game.Run(view, options);
    }
}
