using System;
using System.IO;
using System.Threading.Tasks;
using Blossom;
using TGK.Client.Input;
using TGK.Client.Platform;
using TGK.Core;
using TGK.Core.Services;

namespace TGK.Client;

internal static class Program
{
    private const int MinWidth = 900, MinHeight = 560;

    private static void Main(string[] args)
    {
        Log.Initialize(Path.Combine(AppPaths.ConfigDirectory, "logs", "tgk.log"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal($"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        CoreLog.Warning = Log.Warning;

        DevOptions dev = DevOptions.Parse(args);
        var remote = new RemoteVaultService(new RemoteVaultOptions { DeviceName = $"{Environment.MachineName} · {OsName()}" });
        var services = new ClientServices(new RoutingVaultService(remote, new MockVaultService()), new PrefsStore(), dev);
        Browser.MaxFps = 120;
        Browser.ShowDebugOverlay = dev.DebugOverlay;
        KeyboardHub.AllowDebugOverlay = dev.DebugOverlay;

        UiThread.Install();
        // Runs on the UI thread once the window exists (the post queue is drained by the main loop).
        Browser.Post(() => ConfigureWindow(dev.WindowSize));
        Browser.Initialize(new TgkApplication(services)); // blocks until the window closes
        remote.Dispose(); // writes edits that were not saved or pushed yet
    }

    private static string OsName() => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    private static void ConfigureWindow((int Width, int Height)? size)
    {
        try
        {
            AppWindow.SetMinimumSize(MinWidth, MinHeight);
            if (size is { } s)
                AppWindow.SetSize(Math.Max(MinWidth, s.Width), Math.Max(MinHeight, s.Height));
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not configure the window: {ex.Message}");
        }
    }
}
