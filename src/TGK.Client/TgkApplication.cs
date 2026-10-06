using System;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core;
using TGK.Client.Agents;
using TGK.Client.Main;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client;

/// <summary>The Blossom application: the login screen, then one <see cref="MainView"/> per signed-in session.</summary>
public sealed class TgkApplication : Application
{
    private readonly LoginView _login;
    private MainView? _main;
    private bool _devStartupDone;
    private bool _devAutoLogin;

    /// <summary>Restores a kept session ("keep me signed in") and then starts in the main view, else on the login screen.</summary>
    public TgkApplication(ClientServices services)
    {
        Title = "TGK";
        Services = services;
        Agents = new AgentService(this);
        Platform.AppWindow.CloseGuard = () => _main?.InterceptClose() == true; // files with unsaved changes ask first
        // Every start asks GitHub about updates, in the background: nothing waits for the answer (the main view takes it).
        if (services.Prefs.CheckForUpdates && services.Dev.Scene is null)
        {
            UpdateChannel channel = services.Prefs.UpdateChannel ?? UpdateChecker.DefaultChannel(AppInfo.Version);
            // On the thread pool as a whole: the UI loop does not run yet.
            _startupUpdateCheck = Task.Run(() => MainView.AskGitHubAsync(channel));
        }
        // Subscribed first: the background sync of a restored session may already find it revoked.
        services.Vault.SessionEnded += reason => UiThread.Post(() => OnSessionEnded(reason));
        if (!services.Dev.SkipRestore)
            RestoreSession();
        _login = new LoginView(services);
        AddView(_login);
        if (services.Vault.IsLoggedIn)
        {
            _main = new MainView(services);
            AddView(_main);
            SetActiveView(_main);
        }
        else
        {
            _devAutoLogin = services.Dev.AutoLogin;
            SetActiveView(_login);
        }
        AppWindow.Resized += () => (ActiveView as TgkView)?.FitToWindow();
        if (services.Prefs.AgentsEnabled && services.Dev.Scene is null)
            UiThread.Post(() => _ = Agents.ApplyAsync(true));
    }

    public ClientServices Services { get; }

    private Task<(bool Ok, UpdateInfo? Update)>? _startupUpdateCheck;

    /// <summary>The update check sent as TGK started, for the first main view to take (null afterwards).</summary>
    internal Task<(bool Ok, UpdateInfo? Update)>? TakeStartupUpdateCheck()
    {
        Task<(bool Ok, UpdateInfo? Update)>? check = _startupUpdateCheck;
        _startupUpdateCheck = null;
        return check;
    }

    /// <summary>Agents (MCP): the local endpoint, approvals and activity.</summary>
    public AgentService Agents { get; }

    /// <summary>
    /// The seam for session tabs: builds the tab content that connects to <c>host</c> (a saved host, or an unsaved
    /// entry from quick connect). The default is the SSH terminal tab.
    /// </summary>
    public Func<HostEntry, TabContent> SessionTabFactory { get; set; } = host => new SessionTabContent(host);

    /// <summary>The signed-in view, or null while the login screen is shown.</summary>
    public MainView? Main => _main;

    /// <summary>Switches to a fresh main view (after a successful sign-in).</summary>
    public void ShowMain() => UiThread.Post(() =>
    {
        if (_main is not null)
            RemoveView(_main);
        Agents.OnSignedIn();
        _main = new MainView(Services);
        AddView(_main);
        SetActiveView(_main);
    });

    /// <summary>Returns to the login screen and discards the main view (after sign-out), showing <paramref name="notice"/> on the card.</summary>
    public void ShowLogin(string? notice = null) => UiThread.Post(() =>
    {
        SetActiveView(_login);
        _login.Reset(notice);
        if (_main is not null)
        {
            MainView old = _main;
            _main = null;
            UiThread.Post(() => RemoveView(old));
        }
    });

    /// <summary>True once, when the login form should sign in to the mock server by itself (dev flags).</summary>
    internal bool TakeDevAutoLogin()
    {
        bool take = _devAutoLogin;
        _devAutoLogin = false;
        return take;
    }

    // Runs before the window opens. Only local work (keyring, cache file, decryption), so the vault shows at once and
    // syncs in the background.
    private void RestoreSession()
    {
        try
        {
            Services.Vault.TryRestoreSessionAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not restore the saved session: {ex.Message}");
        }
    }

    // The server ended this device's session (revoked elsewhere, expired, account disabled): close the sessions and
    // go back to the login screen with the reason.
    private void OnSessionEnded(string reason)
    {
        _main?.Teardown();
        Agents.OnSignedOut();
        ShowLogin(reason);
    }

    /// <summary>
    /// The window closed (the UI loop has ended): saves the open tabs when "reopen my tabs" is on and pushes changes the
    /// server has not received yet, for at most <paramref name="timeout"/>, so another device sees them right away.
    /// Whatever is not pushed by then stays queued on this device and goes out with the next start.
    /// </summary>
    internal void OnExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        TimeSpan Left() => deadline - DateTime.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.Zero;
        try
        {
            // The save completes once it is stored on this device (or its push attempt finished). Waited for with a
            // timeout only: the UI loop is gone, so nothing may depend on it from here on.
            _main?.OnExit()?.Wait(Left());
            Agents.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); // stops serving; closes the agents' connections
            var vault = Services.Vault;
            if (vault.Mode != VaultMode.Server || !vault.IsLoggedIn || vault.PendingChanges == 0)
                return;
            using var cts = new CancellationTokenSource(Left());
            if (!Task.Run(() => vault.SyncAsync(cts.Token)).Wait(Left() + TimeSpan.FromSeconds(1)))
                Log.Warning("Changes not pushed at exit (the server is slow); they are sent at the next start.");
        }
        catch (AggregateException ex)
        {
            Log.Warning($"Changes not pushed at exit: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>A downloaded, unpacked update (<see cref="UpdateInstaller.DownloadAsync"/>) to install when the window closes.</summary>
    internal string? PendingUpdate { get; set; }

    /// <summary>The release <see cref="PendingUpdate"/> holds.</summary>
    internal UpdateInfo? PendingUpdateInfo { get; set; }

    /// <summary>TGK starts again once the window has closed and the pending update is installed.</summary>
    internal bool RelaunchRequested { get; private set; }

    /// <summary>"Restart now": closes the window like the user would; <see cref="InstallPendingUpdate"/> then does the rest.</summary>
    internal void RestartForUpdate()
    {
        RelaunchRequested = true;
        AppWindow.Close();
    }

    /// <summary>
    /// After <see cref="OnExit"/>: swaps the pending update in. A failure leaves the old version in place (the swap is
    /// undone) and is recorded for a toast at the next start.
    /// </summary>
    internal void InstallPendingUpdate()
    {
        if (PendingUpdate is not { } staged)
            return;
        using UpdateInstaller installer = SelfUpdate.CreateInstaller();
        try
        {
            installer.Apply(staged);
            Log.Info("Update installed.");
        }
        catch (UpdateException ex)
        {
            Log.Error($"Update not installed: {ex}");
            installer.DeleteStaging();
            string message = ex.Message;
            Services.UpdatePrefs(p => p.UpdateError = $"{message} TGK was not updated.");
        }
    }

    internal void OnMainViewShown(MainView view)
    {
        if (_devStartupDone)
            return;
        _devStartupDone = true;
        if (Services.Dev.Scene is { } scene && scene != "login")
            UiThread.Post(() => view.RunScene(scene));
        if (Services.Dev.ParseConnect() is { } connect)
            UiThread.Post(() => view.RunDevConnect(connect.Address, connect.Password, Services.Dev.Send));
    }
}
