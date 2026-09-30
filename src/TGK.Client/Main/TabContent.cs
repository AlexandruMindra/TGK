using System;
using System.Collections.Generic;
using Blossom.Core.Visual;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Main;

/// <summary>Drives the tab's status dot: amber connecting, green connected, grey closed, red failed.</summary>
public enum TabStatus
{
    /// <summary>No dot (e.g. the new-tab page).</summary>
    None,
    Connecting,
    Connected,
    Closed,
    Failed,
}

/// <summary>
/// What a tab shows: the new-tab page (<see cref="HomeTabContent"/>) or a session. <see cref="MainView"/> sizes it to
/// the content area (or to its pane, when the tab is part of a split view) and toggles <c>Visible</c> as tabs switch.
/// All members are UI-thread only; raise
/// <see cref="Changed"/> (via the <c>Set*</c> helpers) after a background event has been marshalled with
/// <c>UiThread.Post</c>.
/// </summary>
public abstract class TabContent : VisualElement
{
    private string _title = "New tab";
    private TabStatus _status;
    private string? _statusText;
    private string? _sizeText;
    private bool _needsAttention;
    private IReadOnlyList<TunnelStatus> _tunnels = [];

    protected TabContent()
    {
        Style = new ElementStyle { BackColor = Theme.Surface };
        IsClipping = true;
    }

    /// <summary>The main view hosting this tab; set before <see cref="OnAttached"/> runs.</summary>
    public MainView Host { get; internal set; } = null!;

    /// <summary>The split view this tab is a pane of (shared by all its panes), or null for a tab of its own.</summary>
    public PaneLayout<TabContent>? Split { get; internal set; }

    /// <summary>On screen: the active tab, or another pane of the active tab's split view.</summary>
    public bool IsShown => Visible;

    public string Title => _title;
    public TabStatus Status => _status;

    /// <summary>Left side of the status bar (e.g. <c>deploy@web-01:22 · Connected</c>); null shows nothing.</summary>
    public string? StatusText => _statusText;

    /// <summary>Right side of the status bar, e.g. the terminal size <c>120×32</c>; null hides it.</summary>
    public string? SizeText => _sizeText;

    /// <summary>The live session's tunnels and how they are doing (the status bar's tunnel chip); empty when there are none.</summary>
    public IReadOnlyList<TunnelStatus> Tunnels => _tunnels;

    /// <summary>Something happened in this background tab (e.g. a terminal bell); cleared when it is shown.</summary>
    public bool NeedsAttention => _needsAttention;

    /// <summary>Raised on the UI thread when the title, status, attention flag, tunnels or status-bar texts change.</summary>
    public event Action<TabContent>? Changed;

    /// <summary>Element to focus when the tab is activated or when keys arrive while nothing has focus.</summary>
    public virtual VisualElement? DefaultFocus => null;

    /// <summary>
    /// True for terminals: plain Ctrl+T / Ctrl+W / Ctrl+B then go to the tab (the shell needs them) and only their
    /// Ctrl+Shift variants act as app shortcuts.
    /// </summary>
    public virtual bool WantsControlKeys => false;

    /// <summary>
    /// What "reopen my tabs" saves of this tab (<see cref="Workspace"/>); null when it can't be reopened and is left
    /// out. Never includes secrets.
    /// </summary>
    public virtual WorkspaceTab? SaveState() => null;

    /// <summary>Called once after the tab was added to <see cref="Host"/> (services and dialogs are reachable from here on).</summary>
    public virtual void OnAttached() { }

    /// <summary>The tab came on screen, as the active tab or as a pane of a split view (it is already laid out).</summary>
    public virtual void OnShown() { }

    /// <summary>The tab became the active one: it has the keyboard and drives the status bar (it is already shown).</summary>
    public virtual void OnActivated() { }

    /// <summary>Another tab became active; this one may still be shown as a pane of the same split view.</summary>
    public virtual void OnDeactivated() { }

    /// <summary>The tab is being closed or replaced: release sessions and timers. The element is disposed afterwards.</summary>
    public virtual void OnClosing() { }

    protected void SetTitle(string title)
    {
        if (_title == title)
            return;
        _title = title;
        Changed?.Invoke(this);
    }

    protected void SetStatus(TabStatus status, string? statusText)
    {
        if (_status == status && _statusText == statusText)
            return;
        _status = status;
        _statusText = statusText;
        Changed?.Invoke(this);
    }

    /// <summary>Flags the tab in the tab strip unless it is on screen.</summary>
    protected void RequestAttention()
    {
        if (_needsAttention || IsShown)
            return;
        _needsAttention = true;
        Changed?.Invoke(this);
    }

    internal void ClearAttention() => _needsAttention = false;

    protected void SetTunnels(IReadOnlyList<TunnelStatus> tunnels)
    {
        if (_tunnels.Count == 0 && tunnels.Count == 0)
            return;
        _tunnels = tunnels;
        Changed?.Invoke(this);
    }

    protected void SetSizeText(string? sizeText)
    {
        if (_sizeText == sizeText)
            return;
        _sizeText = sizeText;
        Changed?.Invoke(this);
    }
}
