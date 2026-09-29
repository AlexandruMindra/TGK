using System;
using Blossom;
using Blossom.Core;
using TGK.Client.Main;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Models;

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
    }

    public ClientServices Services { get; }

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
        ShowLogin(reason);
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
