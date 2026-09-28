using System;
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

    public TgkApplication(ClientServices services)
    {
        Title = "TGK";
        Services = services;
        _login = new LoginView(services);
        AddView(_login);
        SetActiveView(_login);
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

    /// <summary>Returns to the login screen and discards the main view (after sign-out).</summary>
    public void ShowLogin() => UiThread.Post(() =>
    {
        SetActiveView(_login);
        _login.Reset();
        if (_main is not null)
        {
            MainView old = _main;
            _main = null;
            UiThread.Post(() => RemoveView(old));
        }
    });

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
