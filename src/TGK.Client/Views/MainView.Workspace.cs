using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using TGK.Client.Controls;
using TGK.Client.Main;
using TGK.Core;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Views;

// "Reopen my tabs" (Settings → Startup, off by default): the tabs and split views are saved in the vault's workspace
// item, which syncs like the rest of the vault, and reopened when the main view opens — on this device or on the next
// one the user signs in on. They are saved when this client closes, signs out or locks, and a few seconds after they
// change (so a crash loses little and another device can pick them up while this one is still open). A client only
// saves after its own tabs changed: one left open and idle never overwrites what the user did elsewhere since.
public sealed partial class MainView
{
    private const long WorkspaceSaveDelayMs = 5_000;
    private static readonly TimeSpan InitialSyncTimeout = TimeSpan.FromSeconds(5);

    private bool _workspaceReady; // the startup restore is done; saving before it would overwrite what it restores
    private long _workspaceSaveAt = -1; // UiClock time of the pending save check
    private string? _workspaceContent; // what this client last saved or reopened (see WorkspaceContent)

    /// <summary>Reopens the saved tabs when the preference is on. Runs once, after <see cref="Build"/>.</summary>
    private async void StartWorkspace()
    {
        try
        {
            // Dev scenes and --dev-connect set up their own tabs.
            if (Services.Dev.Scene is not null || Services.Dev.Connect is not null)
                return;
            // A kept session opens on the vault cached on this device: the preference and the tabs may have changed on
            // another device since, so wait (briefly) for the first sync.
            if (Services.Vault.Mode == VaultMode.Server)
            {
                using var timeout = new CancellationTokenSource(InitialSyncTimeout);
                try
                {
                    await Services.Vault.SyncAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or VaultException or InvalidOperationException)
                {
                    Log.Warning($"Reopening tabs without a fresh sync: {ex.Message}");
                }
            }
            if (_torndown || !Services.Vault.IsLoggedIn)
                return;
            Workspace saved = Services.Vault.Current.Workspace;
            if (saved.RestoreTabs && saved.Tabs.Count > 0)
                RestoreWorkspace(saved);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not reopen the saved tabs: {ex}");
        }
        finally
        {
            _workspaceReady = true;
            if (!_torndown)
                _workspaceContent = WorkspaceContent(CaptureWorkspace());
        }
    }

    private void RestoreWorkspace(Workspace saved)
    {
        // The new-tab page the window opened with gives way, unless the user already typed into it meanwhile.
        HomeTabContent? blank = _tabs.Count == 1 && _tabs[0] is HomeTabContent { IsBlank: true } home ? home : null;
        var tabs = new TabContent?[saved.Tabs.Count];
        int at = _tabs.Count;
        for (int i = 0; i < saved.Tabs.Count; i++)
        {
            tabs[i] = ReopenTab(saved.Tabs[i]);
            if (tabs[i] is { } tab)
                Attach(tab, at++);
        }
        int reopened = tabs.Count(t => t is not null);
        if (reopened == 0)
            return;
        foreach (WorkspaceSplit split in saved.Splits)
        {
            if (WorkspaceLayouts.Import<TabContent>(split, i => i >= 0 && i < tabs.Length ? tabs[i] : null) is not { } layout)
                continue;
            foreach (TabContent pane in layout.Items)
                pane.Split = layout;
            GatherPanes(layout);
        }
        if (blank is not null)
            Detach(blank);
        TabContent? active = saved.ActiveTab >= 0 && saved.ActiveTab < tabs.Length ? tabs[saved.ActiveTab] : null;
        ActivateTab(active ?? tabs.First(t => t is not null)!);
        UpdatePanes(); // ActivateTab returns early when the tab was already active
        SyncChrome();

        string where = saved.SavedOn is { Length: > 0 } device && device != DeviceName ? $" from {device}" : "";
        ShowToast($"Reopened {reopened} tab{(reopened == 1 ? "" : "s")}{where}.", ToastKind.Success);
    }

    // A saved host that was deleted since is not reopened.
    private TabContent? ReopenTab(WorkspaceTab saved)
    {
        if (saved.HostId is { } id)
            return Services.Vault.Current.FindHost(id) is { } host ? App.SessionTabFactory(host) : null;
        if (saved.Host is { Length: > 0 } address)
            return App.SessionTabFactory(new HostEntry { Host = address, Port = saved.Port, Username = saved.Username ?? "" });
        return new HomeTabContent(this);
    }

    /// <summary>The tabs and split views as "reopen my tabs" saves them (the preference on).</summary>
    private Workspace CaptureWorkspace()
    {
        var workspace = new Workspace { RestoreTabs = true, SavedAt = DateTimeOffset.UtcNow, SavedOn = DeviceName };
        var index = new Dictionary<TabContent, int>();
        foreach (TabContent tab in _tabs)
        {
            if (workspace.Tabs.Count >= Workspace.MaxTabs || tab.SaveState() is not { } state
                || state.Host?.Length > Workspace.MaxAddressLength || state.Username?.Length > Workspace.MaxAddressLength)
                continue;
            index[tab] = workspace.Tabs.Count;
            workspace.Tabs.Add(state);
        }
        workspace.ActiveTab = ActiveTab is { } active && index.TryGetValue(active, out int a) ? a : -1;
        foreach (PaneLayout<TabContent> layout in _tabs.Select(t => t.Split).OfType<PaneLayout<TabContent>>().Distinct())
        {
            if (WorkspaceLayouts.Export(layout, t => index.TryGetValue(t, out int i) ? i : null) is { } split)
                workspace.Splits.Add(split);
        }
        return workspace;
    }

    // What is compared to decide whether the tabs changed: everything but when and where they were saved.
    private static string WorkspaceContent(Workspace workspace)
    {
        Workspace copy = workspace.Clone();
        copy.SavedAt = null;
        copy.SavedOn = null;
        return JsonSerializer.Serialize(copy, TgkJson.Options);
    }

    private static string DeviceName => Environment.MachineName;

    /// <summary>Something that may be saved changed (tabs, their order, split views, the active tab): save soon.</summary>
    private void WorkspaceChanged()
    {
        if (_workspaceReady && _workspaceSaveAt < 0 && Services.Vault.Current.Workspace.RestoreTabs)
            _workspaceSaveAt = UiClock.NowMs + WorkspaceSaveDelayMs;
    }

    private void TickWorkspace()
    {
        if (_workspaceSaveAt >= 0 && UiClock.NowMs >= _workspaceSaveAt)
            SaveWorkspace();
    }

    /// <summary>
    /// Saves the tabs when the preference is on and they changed since this client last saved or reopened them.
    /// Applied to the vault at once and stored or pushed in the background (the returned task, null when nothing was
    /// saved); failures are only logged.
    /// </summary>
    internal Task? SaveWorkspace()
    {
        _workspaceSaveAt = -1;
        if (!_workspaceReady || _torndown || !Services.Vault.IsLoggedIn || !Services.Vault.Current.Workspace.RestoreTabs)
            return null;
        Workspace workspace = CaptureWorkspace();
        string content = WorkspaceContent(workspace);
        if (content == _workspaceContent)
            return null;
        Task? task = Store(workspace);
        if (task is not null)
            _workspaceContent = content;
        return task;
    }

    /// <summary>
    /// Turns "reopen my tabs" on (saving the current tabs right away, so another device can pick them up) or off
    /// (which also discards the saved tabs).
    /// </summary>
    public void SetRestoreTabs(bool on)
    {
        if (on == Services.Vault.Current.Workspace.RestoreTabs)
            return;
        if (!on)
        {
            _workspaceSaveAt = -1;
            RunVault(() => Services.Vault.SaveWorkspaceAsync(new Workspace()));
            return;
        }
        Workspace workspace = CaptureWorkspace();
        if (RunVault(() => Services.Vault.SaveWorkspaceAsync(workspace)))
            _workspaceContent = WorkspaceContent(workspace);
    }

    private Task? Store(Workspace workspace)
    {
        Task task;
        try
        {
            task = Services.Vault.SaveWorkspaceAsync(workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Log.Warning($"Could not save the open tabs: {ex.Message}");
            return null;
        }
        task.ContinueWith(t => Log.Warning($"Could not save the open tabs: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }
}
