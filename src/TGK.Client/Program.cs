using System;
using System.IO;
using System.Threading.Tasks;
using Blossom;
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
        var local = new LocalVaultService();
        var services = new ClientServices(new RoutingVaultService(remote, new MockVaultService(), local), new PrefsStore(), dev);
        Shell.MaxFps = 120;
        Shell.ShowDebugOverlay = dev.DebugOverlay;

        UiThread.Install();
        var app = new TgkApplication(services);
        app.EnableStatsOverlay = dev.DebugOverlay; // F12 toggles the frame-time overlay only with --fps
        app.Window.MinWidth = MinWidth;
        app.Window.MinHeight = MinHeight;
        if (dev.WindowSize is { } size)
        {
            app.Window.Width = Math.Max(MinWidth, size.Width);
            app.Window.Height = Math.Max(MinHeight, size.Height);
        }
        Shell.Initialize(app); // blocks until the window closes
        app.OnExit(TimeSpan.FromSeconds(3)); // saves the open tabs ("reopen my tabs") and pushes pending changes
        app.InstallPendingUpdate(); // an update downloaded in this run ("Update now")
        remote.Dispose(); // writes edits that were not saved or pushed yet
        local.Dispose();
        if (app.RelaunchRequested)
            SelfUpdate.Relaunch(); // after everything is written, so the new process reads it
    }

    private static string OsName() => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
}
