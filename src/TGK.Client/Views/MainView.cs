using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Client.Terminal;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Core.Ssh;

namespace TGK.Client.Views;

/// <summary>
/// The signed-in window: tab strip, host sidebar, the active tab's content (or its split view, see MainView.Split.cs)
/// and a status bar. Owns the tab list; session tabs come from <see cref="TgkApplication.SessionTabFactory"/>.
/// </summary>
public sealed partial class MainView : TgkView
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
    private TabContent? _tunnelMenuTab; // the tab whose tunnels the open tunnel menu lists
    private IReadOnlyList<TunnelStatus>? _tunnelMenuTunnels;
    private SKRect _tunnelMenuChip;

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
        _content = new ContentHost(this);
        _status = new StatusBar();
        _status.TunnelsClicked += ShowTunnelMenu;
        _root.AddChild(_content);
        _root.AddChild(_sidebar);
        _root.AddChild(_strip);
        _root.AddChild(_status);
        AddFullWindow(_root);

        _sidebarVisible = !Services.Prefs.SidebarCollapsed;
        Services.Vault.Changed += OnVaultChanged;
        RegisterShortcuts();
        Events.OnMouseDown += OnViewMouseDown;
        UiClock.Tick += OnUiTick;
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

    /// <summary>Adds <paramref name="tab"/> after the active tab or its split view (or at the end) and, by default, activates it.</summary>
    public void OpenTab(TabContent tab, bool activate = true)
    {
        int index = _active < 0 ? _tabs.Count : Block(_active).Last + 1;
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
        TabContent tab = _tabs[index];
        // Moving to another pane of a split view with a maximized pane shows the whole split view again.
        if (tab.Split is { Zoomed: { } zoomed } layout && zoomed != tab)
            layout.Zoomed = null;
        _active = index;
        previous?.OnDeactivated();
        UpdatePanes();
        tab.ClearAttention();
        // A click in a pane has already focused what it hit there (or opened a menu, which has the keyboard until it
        // closes); otherwise the tab's default focus takes the keyboard.
        if (!Menu.IsOpen && (ActiveKeyboardElement is not { } focused || !tab.ContainsElement(focused)))
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
        List<TabContent> otherPanes = tab.Split?.Items.Where(t => t != tab).ToList() ?? [];
        Detach(tab);
        if (_tabs.Count == 0)
        {
            NewTab(); // like a browser window that stays open: always keep one tab
            return;
        }
        if (wasActive)
        {
            // A closed pane hands over to the pane after it in the strip (else the one before), so the split view stays.
            int next = Math.Min(index, _tabs.Count - 1);
            if (otherPanes.Count > 0)
            {
                List<int> panes = otherPanes.Select(t => _tabs.IndexOf(t)).Where(i => i >= 0).ToList();
                next = panes.Where(i => i >= index).DefaultIfEmpty(panes.Max()).Min();
            }
            ActivateTab(next);
        }
        else
        {
            UpdatePanes(); // it may have been a pane on screen
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
        PaneLayout<TabContent>? layout = old.Split;
        if (layout is not null)
        {
            layout.Replace(old, replacement); // the replacement takes over the pane
            old.Split = null;
        }
        Detach(old);
        Attach(replacement, index);
        replacement.Split = layout;
        if (wasActive || _active < 0)
        {
            ActivateTab(index);
        }
        else
        {
            UpdatePanes(); // a pane on screen shows (and starts) its replacement
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
        if (_active >= index)
            _active++;
        _content.AddChild(tab);
        _content.InvalidateLayout();
        tab.OnAttached();
    }

    /// <summary>Removes <paramref name="tab"/> (and its pane); <c>_active</c> keeps pointing at the active tab, or is -1 when that was this one.</summary>
    private void Detach(TabContent tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index == _active)
            tab.OnDeactivated();
        tab.OnClosing();
        tab.Changed -= OnTabChanged;
        LeaveSplit(tab);
        _tabs.RemoveAt(index);
        if (index == _active)
            _active = -1;
        else if (index < _active)
            _active--;
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

    private void OnTabChanged(TabContent tab)
    {
        SyncChrome();
        // An open tunnel menu follows its tunnels (Starting → Active/Failed) and closes when the session ends.
        if (tab == _tunnelMenuTab && !ReferenceEquals(tab.Tunnels, _tunnelMenuTunnels))
            ShowTunnelMenu(_tunnelMenuChip);
    }

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
        foreach (PaneHeader header in _paneHeaders)
        {
            if (header.Visible)
                header.InvalidatePaint();
        }
        TabContent? tab = ActiveTab;
        string sync = HostFormat.Sync(Services.Vault, DateTimeOffset.UtcNow);
        _status.Set(tab?.StatusText ?? (tab is HomeTabContent ? $"{Services.Vault.Current.Hosts.Count} saved hosts" : ""),
            tab?.Status ?? TabStatus.None, tab?.SizeText, sync, tab?.Tunnels ?? []);
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
        foreach (SessionTabContent session in _tabs.OfType<SessionTabContent>())
            session.OnVaultChanged();
        SyncChrome();
    }

    public void SyncNow()
    {
        if (RunVault(() => Services.Vault.SyncAsync()))
            SyncChrome();
    }

    // ---- hosts ----

    /// <summary>Opens the host editor for <paramref name="host"/> (or for a new host when null) on <paramref name="tab"/>.</summary>
    public void EditHost(HostEntry? host, int tab = HostEditorDialog.GeneralTab)
    {
        var dialog = new HostEditorDialog(this, host);
        dialog.Open();
        if (tab != HostEditorDialog.GeneralTab)
            dialog.ShowTab(tab);
    }

    public void DuplicateHost(HostEntry host)
    {
        HostEntry copy = host.Clone();
        copy.Name = string.IsNullOrWhiteSpace(host.Name) ? "" : host.Name + " (copy)";
        new HostEditorDialog(this, null, copy).Open();
    }

    public async void DeleteHost(HostEntry host)
    {
        if (await HostEditorDialog.ConfirmDelete(this, host))
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

    // ---- groups ----

    /// <summary>Opens the settings of <paramref name="group"/>, or creates a new group when null.</summary>
    public void EditGroup(HostGroup? group, int tab = GroupSettingsDialog.GeneralTab)
    {
        var dialog = new GroupSettingsDialog(this, group);
        dialog.Open();
        if (tab != GroupSettingsDialog.GeneralTab)
            dialog.ShowTab(tab);
    }

    public async void DeleteGroup(HostGroup group)
    {
        if (await GroupSettingsDialog.ConfirmDelete(this, group))
            RunVault(() => Services.Vault.DeleteGroupAsync(group.Id));
    }

    /// <summary>Context menu for a group header (null: the "Ungrouped" section or the empty list).</summary>
    public void ShowGroupMenu(HostGroup? group, float x, float y)
    {
        var items = new List<MenuItem>();
        if (group is not null)
        {
            items.Add(new MenuItem { Text = "Group settings…", Icon = "settings", Action = () => EditGroup(group) });
            items.Add(new MenuItem { Text = "New host in group…", Icon = "server", Action = () => new HostEditorDialog(this, null, new HostEntry { GroupId = group.Id }).Open() });
            items.Add(MenuItem.Separator);
        }
        items.Add(new MenuItem { Text = "New group…", Icon = "folder", Action = () => EditGroup(null) });
        if (group is not null)
        {
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem { Text = "Delete group…", Icon = "trash", IsDanger = true, Action = () => DeleteGroup(group) });
        }
        Menu.Show(items, x, y);
    }

    // ---- tunnels ----

    /// <summary>
    /// The active session's tunnels above the status bar chip (<paramref name="chip"/>); a click copies the listening
    /// address. While open it is rebuilt when they change (<see cref="OnTabChanged"/>).
    /// </summary>
    private void ShowTunnelMenu(SKRect chip)
    {
        if (ActiveTab is not { Tunnels.Count: > 0 } tab)
        {
            if (_tunnelMenuTab is not null)
                Menu.Close();
            return;
        }
        var items = new List<MenuItem> { new() { Text = "Tunnels of this session · click to copy the address", IsHeader = true } };
        foreach (TunnelStatus tunnel in tab.Tunnels)
        {
            PortForward forward = tunnel.Forward;
            string address = $"{forward.BindAddress}:{forward.BindPort}";
            bool failed = tunnel.State == TunnelState.Failed;
            items.Add(new MenuItem
            {
                Text = forward.Description is { Length: > 0 } description ? $"{forward}  ·  {description}" : forward.ToString(),
                Icon = failed ? "alert" : "tunnel",
                Hint = tunnel.State switch
                {
                    TunnelState.Active => "Active",
                    TunnelState.Starting => "Starting…",
                    TunnelState.Failed => "Failed",
                    _ => "Stopped",
                },
                IsDanger = failed,
                Action = () =>
                {
                    Browser.SetClipboardText(address);
                    ShowToast($"Copied {address}", ToastKind.Success);
                },
            });
            if (failed && tunnel.Error is { } error)
                items.AddRange(Gfx.Wrap(error, Gfx.Font(Theme.FontSm), 420, 3).Select(line => new MenuItem { Text = line, IsHeader = true }));
        }
        Menu.Show(items, chip.Right - 320, chip.Bottom, 320, chip.Height, onClosed: () => _tunnelMenuTab = null);
        (_tunnelMenuTab, _tunnelMenuTunnels, _tunnelMenuChip) = (tab, tab.Tunnels, chip);
    }

    // ---- account ----

    public void ShowAccountMenu()
    {
        var at = _strip.Avatar.Transform.Computed;
        bool local = Services.Vault.Mode == VaultMode.Local;
        var items = new List<MenuItem>
        {
            new() { Text = local ? "Local vault · this device only" : $"Signed in as {Services.Vault.CurrentUser}", IsHeader = true },
            MenuItem.Separator,
            new() { Text = "Keys & identities", Icon = "key", Action = ShowIdentities },
            new() { Text = "Settings", Icon = "settings", Hint = "Ctrl+,", Action = ShowSettings },
        };
        if (local)
        {
            items.Add(new MenuItem { Text = "Change master password…", Icon = "shield", Action = ShowChangePassword });
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem { Text = "Upload to a server account…", Icon = "upload", Action = () => new UploadToServerDialog(this).Open() });
        }
        else
        {
            items.Add(new MenuItem { Text = "Sync now", Icon = "refresh", Action = SyncNow });
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem { Text = "Devices & sessions…", Icon = "shield", Action = ShowDevices });
            items.Add(new MenuItem { Text = "Change password…", Icon = "lock", Action = ShowChangePassword });
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem { Text = "Save an offline copy…", Icon = "monitor", Action = () => new OfflineCopyDialog(this).Open() });
        }
        items.Add(new MenuItem { Text = "Export backup…", Icon = "download", Action = () => new BackupExportDialog(this).Open() });
        items.Add(new MenuItem { Text = "Import backup…", Icon = "folder", Action = () => new BackupImportDialog(this).Open() });
        items.Add(MenuItem.Separator);
        items.Add(local
            ? new MenuItem { Text = "Lock", Icon = "lock", Action = () => SignOut() }
            : new MenuItem { Text = "Sign out", Icon = "logout", Action = () => SignOut() });
        Menu.Show(items, at.X + at.Width, at.Y + at.Height + 6, 260, at.Height);
    }

    /// <summary>Drops down from the sync chip (window rect <paramref name="x"/>, <paramref name="y"/>, <paramref name="h"/>): the sync state in words and "Sync now".</summary>
    public void ShowSyncMenu(float x, float y, float h)
    {
        IVaultService vault = Services.Vault;
        if (vault.Mode == VaultMode.Local)
        {
            string where = vault.Status == SyncState.Error
                ? "Save error. " + vault.LastError
                : "Your vault is stored only on this computer, encrypted with your master password. Nothing is synced.";
            Menu.Show(Gfx.Wrap(where, Gfx.Font(Theme.FontSm), 340, 5).Select(line => new MenuItem { Text = line, IsHeader = true }).ToList(), x, y + h + 6, 220, h);
            return;
        }
        string last = $"Last synced {HostFormat.Ago(vault.LastSync, DateTimeOffset.UtcNow)}";
        string status = vault.Status switch
        {
            SyncState.Syncing => "Syncing…",
            SyncState.Offline => "Offline. " + (vault.LastError ?? "The server can't be reached."),
            SyncState.Error => "Sync error. " + (vault.LastError ?? "The last sync failed."),
            _ => last,
        };
        // Headers are single lines: wrap long errors over several.
        var items = Gfx.Wrap(status, Gfx.Font(Theme.FontSm), 340, 5).Select(line => new MenuItem { Text = line, IsHeader = true }).ToList();
        if (vault.Status is SyncState.Offline or SyncState.Error)
            items.Add(new MenuItem { Text = last, IsHeader = true });
        items.Add(MenuItem.Separator);
        items.Add(new MenuItem { Text = "Sync now", Icon = "refresh", Action = SyncNow });
        Menu.Show(items, x, y + h + 6, 220, h);
    }

    public void ShowIdentities() => new IdentitiesDialog(this).Open();

    public void ShowSettings() => new SettingsDialog(this).Open();

    public void ShowDevices() => new DevicesDialog(this).Open();

    public void ShowChangePassword() => new ChangePasswordDialog(this).Open();

    /// <summary>After the vault switched between the local vault and a server account (upload to a server).</summary>
    public void OnVaultModeChanged()
    {
        _strip.Avatar.InvalidatePaint();
        RefreshFromVault();
    }

    /// <summary>
    /// Signs this device out (locks the local vault) and returns to the login screen. Asks first when sessions are
    /// open (unless <paramref name="confirmed"/>) and, always, when changes could not be saved: signing out discards them.
    /// </summary>
    public async void SignOut(bool confirmed = false)
    {
        bool local = Services.Vault.Mode == VaultMode.Local;
        string action = local ? "Lock" : "Sign out";
        int sessions = _tabs.Count(t => t is not HomeTabContent);
        if (!confirmed && sessions > 0 && !await ConfirmDialog.ShowAsync(this, local ? "Lock the vault?" : "Sign out?",
                $"{sessions} open session{(sessions == 1 ? "" : "s")} will be closed.", action))
            return;
        if (Services.Vault.PendingChanges > 0)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Services.Vault.SyncAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            if (!Services.Vault.IsLoggedIn)
                return; // the server ended the session meanwhile; the login screen is already on its way
            int unsent = Services.Vault.PendingChanges;
            string them = unsent == 1 ? "it" : "them";
            if (unsent > 0 && !await ConfirmDialog.ShowAsync(this, local ? "Discard unsaved changes?" : "Discard unsynced changes?",
                    local
                        ? $"{unsent} change{(unsent == 1 ? "" : "s")} could not be saved to the local vault ({Services.Vault.LastError ?? "the file can't be written"}). Locking now discards {them}."
                        : $"{unsent} change{(unsent == 1 ? " has" : "s have")} not reached the server yet ({Services.Vault.LastError ?? "it is not reachable"}). Signing out now discards {them}.",
                    $"{action} anyway", danger: true))
                return;
        }
        Teardown();
        await Services.Vault.LogoutAsync();
        App.ShowLogin();
    }

    /// <summary>Closes all tabs and detaches from services and shortcuts; the view is discarded afterwards.</summary>
    internal void Teardown()
    {
        if (_torndown)
            return;
        _torndown = true;
        Services.Vault.Changed -= OnVaultChanged;
        UiClock.Tick -= OnUiTick;
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
        // Split view (Terminator's keys): split right / down, maximize the pane, Alt+arrows move between panes.
        bool SplitReady() => Ready() && ActiveTab?.Split is not null;
        Add(ctrlShift, Key.E, () => SplitPane(SplitOrientation.Horizontal));
        Add(ctrlShift, Key.O, () => SplitPane(SplitOrientation.Vertical));
        Add(ctrlShift, Key.X, () => ToggleZoom(), SplitReady);
        foreach ((Key key, PaneDirection direction) in new[] { (Key.Left, PaneDirection.Left), (Key.Right, PaneDirection.Right), (Key.Up, PaneDirection.Up), (Key.Down, PaneDirection.Down) })
            Add(KeyModifiers.Alt, key, () => FocusPane(direction), () => SplitReady() && ActiveTab?.Split?.Zoomed is null);
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
            case "split":
                // The new-tab page and two sessions: one large pane on the left, two stacked on the right.
                foreach (string name in new[] { "staging-web", "web-01" })
                {
                    if (vault.Hosts.Find(h => h.Name == name) is { } host)
                        OpenTab(App.SessionTabFactory(host), activate: false);
                }
                ApplyLayout(LayoutPreset.MainLeft);
                break;
            case "tabs-overflow":
                // More tabs than fit in the strip.
                for (int i = 0; i < 16 && vault.Hosts.Count > 0; i++)
                    OpenTab(App.SessionTabFactory(vault.Hosts[i % vault.Hosts.Count]), activate: false);
                ActivateTab(_tabs.Count - 1);
                break;
            case "host-editor" when web is not null:
                EditHost(web);
                break;
            case "host-editor-connection" when web is not null:
                EditHost(web, HostEditorDialog.ConnectionTab);
                break;
            case "host-editor-tunnels" when (vault.Hosts.Find(h => h.Tunnels.Count > 0) ?? web) is { } tunneled:
                EditHost(tunneled, HostEditorDialog.TunnelsTab);
                break;
            case "host-editor-appearance" when (vault.Hosts.Find(h => h.Options.ColorScheme is not null) ?? web) is { } styled:
                EditHost(styled, HostEditorDialog.AppearanceTab);
                break;
            case "group-settings" when vault.Groups.OrderBy(g => g.SortOrder).FirstOrDefault() is { } group:
                EditGroup(group, GroupSettingsDialog.ConnectionTab);
                break;
            case "connection-defaults":
                var settings = new SettingsDialog(this);
                settings.Open();
                settings.ShowTab(SettingsDialog.ConnectionTab);
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
            case "devices":
                ShowDevices();
                break;
            case "change-password":
                ShowChangePassword();
                break;
            case "local-main":
                ShowAccountMenu();
                break;
            case "upload-to-server":
                new UploadToServerDialog(this).Open();
                break;
            case "offline-copy":
                new OfflineCopyDialog(this).Open();
                break;
            case "backup-export":
                new BackupExportDialog(this).Open();
                break;
            case "backup-import":
                new BackupImportDialog(this).Open();
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

    /// <summary>
    /// Holds every tab's content (only the active tab, or its split view, is visible) and the panes' title bars and
    /// dividers; draws the ring around the focused pane.
    /// </summary>
    private sealed class ContentHost : VisualElement
    {
        private readonly MainView _view;
        private SKRect? _focus;

        public ContentHost(MainView view)
        {
            _view = view;
            Style = new ElementStyle { BackColor = Theme.Surface };
        }

        /// <summary>The focused pane of the split view on screen (title bar included), or null.</summary>
        public void SetFocusRect(SKRect? rect)
        {
            if (rect == _focus)
                return;
            _focus = rect;
            InvalidatePaint();
        }

        protected override void LayoutChildren() => _view.LayoutContent(Transform.Computed.Width, Transform.Computed.Height);

        protected override void OnAfterStyleDraw(List<DrawCommand> cmds) => cmds.Add(new DrawCallbackCommand(c =>
        {
            if (_focus is { } focus)
                Gfx.StrokeRound(c, SKRect.Inflate(focus, 1.5f, 1.5f), 3, Theme.Accent.WithAlpha(170));
        }));
    }
}
