using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Client.Terminal;
using TGK.Core.Models;

namespace TGK.Client.Views;

/// <summary>
/// The signed-in window: tab strip, host sidebar, the active tab's content and a status bar.
/// Owns the tab list; session tabs come from <see cref="TgkApplication.SessionTabFactory"/>.
/// </summary>
public sealed class MainView : TgkView
{
    private readonly List<TabContent> _tabs = [];
    private readonly List<IDisposable> _shortcuts = [];
    private MainRoot _root = null!;
    private TabStrip _strip = null!;
    private Sidebar _sidebar = null!;
    private ContentHost _content = null!;
    private StatusBar _status = null!;
    private int _active = -1;
    private bool _sidebarVisible;
    private bool _vaultRefreshQueued;
    private bool _torndown;

    public MainView(ClientServices services) : base("Main", services)
    {
    }

    public IReadOnlyList<TabContent> Tabs => _tabs;

    public TabContent? ActiveTab => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    public bool SidebarVisible => _sidebarVisible;

    protected override VisualElement? FocusScope => _root;

    protected override VisualElement? DefaultFocus => ActiveTab?.DefaultFocus;

    protected override void Build()
    {
        _root = new MainRoot(this);
        _strip = new TabStrip(this);
        _sidebar = new Sidebar(this);
        _content = new ContentHost();
        _status = new StatusBar();
        _root.AddChild(_content);
        _root.AddChild(_sidebar);
        _root.AddChild(_strip);
        _root.AddChild(_status);
        AddFullWindow(_root);

        _sidebarVisible = !Services.Prefs.SidebarCollapsed;
        Services.Vault.Changed += OnVaultChanged;
        RegisterShortcuts();
        _sidebar.Refresh();
        NewTab();
    }

    protected override void OnShown()
    {
        base.OnShown();
        App.OnMainViewShown(this);
    }

    // ---- layout ----

    private void Layout(float w, float h)
    {
        float sidebarW = _sidebarVisible ? Math.Min(Services.Prefs.SidebarWidth, Math.Max(200, w * 0.4f)) : 0;
        float bodyTop = Theme.TabStripHeight, bodyH = Math.Max(0, h - Theme.TabStripHeight - Theme.StatusBarHeight);
        _strip.ContentLeft = sidebarW;
        _strip.Transform.SetLocalFrame(0, 0, w, Theme.TabStripHeight);
        _sidebar.Visible = _sidebarVisible;
        if (_sidebarVisible)
            _sidebar.Transform.SetLocalFrame(0, bodyTop, sidebarW, bodyH);
        _content.Transform.SetLocalFrame(sidebarW, bodyTop, w - sidebarW, bodyH);
        _status.Transform.SetLocalFrame(0, h - Theme.StatusBarHeight, w, Theme.StatusBarHeight);
    }

    public void ToggleSidebar()
    {
        _sidebarVisible = !_sidebarVisible;
        Services.UpdatePrefs(p => p.SidebarCollapsed = !_sidebarVisible);
        _root.InvalidateLayout();
    }

    // ---- tabs ----

    /// <summary>Opens a new-tab page and activates it.</summary>
    public void NewTab()
    {
        var home = new HomeTabContent(this);
        OpenTab(home);
        home.FocusQuickConnect();
    }

    /// <summary>Adds <paramref name="tab"/> after the active tab (or at the end) and, by default, activates it.</summary>
    public void OpenTab(TabContent tab, bool activate = true)
    {
        int index = _active < 0 ? _tabs.Count : _active + 1;
        Attach(tab, index);
        if (activate)
            ActivateTab(index);
        else
            SyncChrome();
    }

    public void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
            return;
        if (index == _active)
        {
            // Re-activating the current tab (e.g. clicking it) gives its content the keyboard back.
            if (ActiveKeyboardElement is null && _tabs[index].DefaultFocus is { EffectiveVisible: true } current)
                SetActiveKeyboardElement(current);
            SyncChrome();
            return;
        }
        TabContent? previous = ActiveTab;
        if (previous is not null)
        {
            previous.Visible = false;
            previous.OnDeactivated();
        }
        _active = index;
        TabContent tab = _tabs[index];
        tab.ClearAttention();
        tab.Visible = true;
        _content.ForceLayoutSubtree();
        SetActiveKeyboardElement(tab.DefaultFocus is { } focus && focus.EffectiveVisible ? focus : null);
        tab.OnActivated();
        SyncChrome();
    }

    public void ActivateTab(TabContent tab) => ActivateTab(_tabs.IndexOf(tab));

    public void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
            return;
        TabContent tab = _tabs[index];
        bool wasActive = index == _active;
        Detach(tab);
        if (_tabs.Count == 0)
        {
            _active = -1;
            NewTab(); // like a browser window that stays open: always keep one tab
            return;
        }
        if (wasActive)
        {
            _active = -1;
            ActivateTab(Math.Min(index, _tabs.Count - 1));
        }
        else
        {
            if (index < _active)
                _active--;
            SyncChrome();
        }
    }

    public void CloseTab(TabContent tab) => CloseTab(_tabs.IndexOf(tab));

    /// <summary>Replaces <paramref name="old"/> with <paramref name="replacement"/> in the same position (browser-style navigation).</summary>
    public void ReplaceTab(TabContent old, TabContent replacement)
    {
        int index = _tabs.IndexOf(old);
        if (index < 0)
        {
            OpenTab(replacement);
            return;
        }
        bool wasActive = index == _active;
        Detach(old);
        Attach(replacement, index);
        if (wasActive || _active < 0)
        {
            _active = -1;
            ActivateTab(index);
        }
        else
        {
            if (index <= _active)
                _active++;
            SyncChrome();
        }
    }

    /// <summary>Opens <paramref name="host"/> in a new session tab.</summary>
    public void ConnectInNewTab(HostEntry host) => OpenTab(App.SessionTabFactory(host));

    /// <summary>Opens <paramref name="host"/> in <paramref name="tab"/>, replacing its content (used by the new-tab page).</summary>
    public void ConnectInTab(TabContent tab, HostEntry host) => ReplaceTab(tab, App.SessionTabFactory(host));

    /// <summary>"Connect": reuse the active new-tab page if there is one, otherwise open a new tab.</summary>
    public void Connect(HostEntry host)
    {
        if (ActiveTab is HomeTabContent home)
            ConnectInTab(home, host);
        else
            ConnectInNewTab(host);
    }

    private void Attach(TabContent tab, int index)
    {
        tab.Host = this;
        tab.Visible = false;
        tab.Changed += OnTabChanged;
        _tabs.Insert(index, tab);
        _content.AddChild(tab);
        _content.InvalidateLayout();
        tab.OnAttached();
    }

    private void Detach(TabContent tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index == _active)
            tab.OnDeactivated();
        tab.OnClosing();
        tab.Changed -= OnTabChanged;
        _tabs.RemoveAt(index);
        if (ActiveKeyboardElement is { } focused && tab.ContainsElement(focused))
            SetActiveKeyboardElement(null);
        tab.Visible = false;
        // Dispose after the current input event has finished bubbling through the tab.
        UiThread.Post(() =>
        {
            _content.RemoveChild(tab);
            tab.Dispose();
        });
    }

    private void OnTabChanged(TabContent tab) => SyncChrome();

    private void CycleTab(int delta)
    {
        if (_tabs.Count > 1)
            ActivateTab(((_active + delta) % _tabs.Count + _tabs.Count) % _tabs.Count);
    }

    /// <summary>Repaints the tab strip and status bar from the tab list and the vault.</summary>
    private void SyncChrome()
    {
        _strip.SetTabs(_tabs, _active);
        _strip.RefreshSync();
        TabContent? tab = ActiveTab;
        string sync = HostFormat.Sync(Services.Vault, DateTimeOffset.UtcNow);
        _status.Set(tab?.StatusText ?? (tab is HomeTabContent ? $"{Services.Vault.Current.Hosts.Count} saved hosts" : ""),
            tab?.Status ?? TabStatus.None, tab?.SizeText, sync);
        App.Title = tab is null or HomeTabContent ? "TGK" : $"{tab.Title} — TGK";
    }

    // ---- vault ----

    // Raised on any thread; coalesce bursts into one UI refresh.
    private void OnVaultChanged()
    {
        UiThread.Post(() =>
        {
            if (_vaultRefreshQueued || _torndown)
                return;
            _vaultRefreshQueued = true;
            UiThread.Post(RefreshFromVault);
        });
    }

    private void RefreshFromVault()
    {
        _vaultRefreshQueued = false;
        if (_torndown || !Services.Vault.IsLoggedIn)
            return;
        _sidebar.Refresh();
        foreach (HomeTabContent home in _tabs.OfType<HomeTabContent>())
            home.Refresh();
        SyncChrome();
    }

    public void SyncNow()
    {
        if (RunVault(() => Services.Vault.SyncAsync()))
            SyncChrome();
    }

    // ---- hosts ----

    /// <summary>Opens the host editor for <paramref name="host"/>, or for a new host when null.</summary>
    public void EditHost(HostEntry? host) => new HostEditorDialog(this, host).Open();

    public void DuplicateHost(HostEntry host)
    {
        HostEntry copy = host.Clone();
        copy.Name = string.IsNullOrWhiteSpace(host.Name) ? "" : host.Name + " (copy)";
        new HostEditorDialog(this, null, copy).Open();
    }

    public async void DeleteHost(HostEntry host)
    {
        bool confirmed = await ConfirmDialog.ShowAsync(this, "Delete host?",
            $"“{host.DisplayName}” will be removed from your vault on all devices. Open sessions stay connected.",
            "Delete", danger: true);
        if (confirmed)
            RunVault(() => Services.Vault.DeleteHostAsync(host.Id));
    }

    /// <summary>Context menu for a saved host at window point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void ShowHostMenu(HostEntry host, float x, float y)
    {
        Menu.Show(
        [
            new MenuItem { Text = "Connect", Icon = "terminal", Action = () => Connect(host) },
            new MenuItem { Text = "Connect in new tab", Icon = "plus", Action = () => ConnectInNewTab(host) },
            MenuItem.Separator,
            new MenuItem { Text = "Edit…", Icon = "edit", Hint = "F2", Action = () => EditHost(host) },
            new MenuItem { Text = "Duplicate", Icon = "copy", Action = () => DuplicateHost(host) },
            MenuItem.Separator,
            new MenuItem { Text = "Delete…", Icon = "trash", IsDanger = true, Action = () => DeleteHost(host) },
        ], x, y);
    }

    // ---- account ----

    public void ShowAccountMenu()
    {
        var at = _strip.Avatar.Transform.Computed;
        string who = $"Signed in as {Services.Vault.CurrentUser}";
        Menu.Show(
        [
            new MenuItem { Text = who, IsHeader = true },
            MenuItem.Separator,
            new MenuItem { Text = "Keys & identities", Icon = "key", Action = ShowIdentities },
            new MenuItem { Text = "Settings", Icon = "settings", Hint = "Ctrl+,", Action = ShowSettings },
            new MenuItem { Text = "Sync now", Icon = "refresh", Action = SyncNow },
            MenuItem.Separator,
            new MenuItem { Text = "Sign out", Icon = "logout", Action = SignOut },
        ], at.X + at.Width, at.Y + at.Height + 6, 220, at.Height);
    }

    public void ShowIdentities() => new IdentitiesDialog(this).Open();

    public void ShowSettings() => new SettingsDialog(this).Open();

    public async void SignOut()
    {
        int sessions = _tabs.Count(t => t is not HomeTabContent);
        if (sessions > 0 && !await ConfirmDialog.ShowAsync(this, "Sign out?",
                $"{sessions} open session{(sessions == 1 ? "" : "s")} will be closed.", "Sign out"))
            return;
        Teardown();
        await Services.Vault.LogoutAsync();
        App.ShowLogin();
    }

    /// <summary>Closes all tabs and detaches from services and shortcuts; the view is discarded afterwards.</summary>
    private void Teardown()
    {
        if (_torndown)
            return;
        _torndown = true;
        Services.Vault.Changed -= OnVaultChanged;
        foreach (IDisposable shortcut in _shortcuts)
            shortcut.Dispose();
        _shortcuts.Clear();
        foreach (TabContent tab in _tabs.ToList())
            Detach(tab);
        _active = -1;
    }

    // ---- shortcuts ----

    private void RegisterShortcuts()
    {
        bool Ready() => IsActive && !HasModal && !Menu.IsOpen;
        // Plain Ctrl+T/W/B belong to the shell whenever keys would reach a terminal: the terminal itself is focused,
        // or nothing is and the active tab's default focus (its terminal) receives unfocused keys.
        bool PlainCtrlReady() => Ready() && !(ActiveKeyboardElement is TerminalView
            || (ActiveKeyboardElement is null && ActiveTab is { WantsControlKeys: true }));
        void Add(KeyModifiers mods, Key key, Action action, Func<bool>? when = null, bool repeats = false) =>
            _shortcuts.Add(KeyboardHub.Shortcuts.Register(mods, key, action, when ?? Ready, repeats));

        const KeyModifiers ctrl = KeyModifiers.Ctrl, ctrlShift = KeyModifiers.Ctrl | KeyModifiers.Shift;
        // Terminals need plain Ctrl+T/W/B (and Ctrl+L); the Ctrl+Shift variants always work, as in Windows Terminal.
        // Ctrl+Tab, Ctrl+PageUp/PageDown, Ctrl+1..9 and Ctrl+, stay global everywhere.
        foreach ((Key key, Action action) in new (Key, Action)[] { (Key.T, NewTab), (Key.W, () => CloseTab(_active)), (Key.B, ToggleSidebar) })
        {
            Add(ctrl, key, action, PlainCtrlReady);
            Add(ctrlShift, key, action);
        }
        Add(ctrl, Key.L, FocusQuickConnect, () => Ready() && ActiveTab is HomeTabContent);
        Add(ctrl, Key.Tab, () => CycleTab(1), repeats: true);
        Add(ctrlShift, Key.Tab, () => CycleTab(-1), repeats: true);
        Add(ctrl, Key.PageDown, () => CycleTab(1), repeats: true);
        Add(ctrl, Key.PageUp, () => CycleTab(-1), repeats: true);
        Add(ctrl, Key.Comma, ShowSettings);
        Key[] digits = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5, Key.Number6, Key.Number7, Key.Number8, Key.Number9];
        for (int i = 0; i < digits.Length; i++)
        {
            int index = i;
            // Ctrl+9 always jumps to the last tab, as in browsers.
            Add(ctrl, digits[i], () => ActivateTab(index == 8 ? _tabs.Count - 1 : index));
        }
    }

    private void FocusQuickConnect() => (ActiveTab as HomeTabContent)?.FocusQuickConnect();

    // ---- dev scenes ----

    /// <summary>Opens the UI state named by <c>--scene</c> (screenshots / manual checks).</summary>
    internal void RunScene(string scene)
    {
        _root.ForceLayoutSubtree(); // anchors for popups need real positions before the first frame
        VaultData vault = Services.Vault.Current;
        HostEntry? web = vault.Hosts.Find(h => h.Name == "web-01") ?? vault.Hosts.FirstOrDefault();
        switch (scene)
        {
            case "main":
                foreach (string name in new[] { "staging-web", "web-01" })
                {
                    if (vault.Hosts.Find(h => h.Name == name) is { } host)
                        OpenTab(App.SessionTabFactory(host), activate: false);
                }
                break;
            case "host-editor" when web is not null:
                EditHost(web);
                break;
            case "identities":
                ShowIdentities();
                break;
            case "settings":
                ShowSettings();
                break;
            case "hostkey":
                _ = HostKeyDialog.ShowAsync(this, new Core.Ssh.HostKeyInfo(web?.Host ?? "example.com", web?.Port ?? 22,
                    "ssh-ed25519", "SHA256:q2V8vQm1kqzEo9H3yT0dJ9lV3n8YQx0cC5b6bY1pZ3o"));
                break;
            case "hostkey-changed":
                _ = HostKeyDialog.ShowAsync(this, new Core.Ssh.HostKeyInfo(web?.Host ?? "example.com", web?.Port ?? 22,
                    "ssh-ed25519", "SHA256:q2V8vQm1kqzEo9H3yT0dJ9lV3n8YQx0cC5b6bY1pZ3o", "SHA256:Zp1Yb6b5Cc0xQY8n3Vl9Jd0Ty3H9oEzqk1mQv8V2q4A"));
                break;
            case "password-prompt":
                _ = PasswordPromptDialog.ShowAsync(this, web is null ? "deploy@example.com" : HostFormat.Address(web, vault));
                break;
            case "menu":
                ShowAccountMenu();
                break;
        }
    }

    /// <summary>Opens the <c>--dev-connect</c> session in a new tab (see <see cref="DevOptions.Connect"/>).</summary>
    internal void RunDevConnect(string address, string? password, string? send)
    {
        HostEntry? host = QuickConnect.Parse(address, Services.Vault.Current, out string? error);
        if (host is null)
        {
            ShowToast($"--dev-connect: {error}", ToastKind.Error);
            return;
        }
        if (ActiveTab is HomeTabContent home)
            ReplaceTab(home, new SessionTabContent(host, password, send));
        else
            OpenTab(new SessionTabContent(host, password, send));
    }

    /// <summary>Full-window root that lays out the four regions.</summary>
    private sealed class MainRoot(MainView view) : VisualElement
    {
        protected override void LayoutChildren() => view.Layout(Transform.Computed.Width, Transform.Computed.Height);
    }

    /// <summary>Holds every tab's content; each fills the area and only the active one is visible.</summary>
    private sealed class ContentHost : VisualElement
    {
        public ContentHost() => Style = new ElementStyle { BackColor = Theme.Surface };

        protected override void LayoutChildren()
        {
            float w = Transform.Computed.Width, h = Transform.Computed.Height;
            foreach (VisualElement child in Children)
                child.Transform.SetLocalFrame(0, 0, w, h);
        }
    }
}
