using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using SkiaSharp;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Input;
using Button = TGK.Client.Controls.Button;
using TGK.Client.Views;
using TGK.Core.Models;

namespace TGK.Client.Main;

/// <summary>
/// The new-tab page: a quick-connect field (<c>user@host[:port]</c>), then Recent and All hosts as cards. Typing in the
/// field also searches the saved hosts by name, user and/or host (<see cref="QuickConnect.Search"/>): the cards show the
/// matches, ↓/↑ pick one and Enter connects to it. Connecting from here replaces this page with the session, like
/// navigating in a browser tab.
/// </summary>
public sealed class HomeTabContent : TabContent
{
    private const float MaxColumnW = 900, SidePad = 40, QuickH = 44, ConnectW = 120;
    private readonly ScrollContainer _scroll;
    private readonly VisualElement _page;
    private readonly Label _heading, _subheading, _hint;
    private readonly TextField _quick;
    private readonly Button _connect;
    private readonly Label _recentCaption, _allCaption, _emptyText;
    private readonly HostCardGrid _recent, _all;
    private readonly Button _newHost;
    private string _query = "";

    public HomeTabContent(MainView main)
    {
        SetTitle("New tab");
        _scroll = new ScrollContainer
        {
            OverflowX = OverflowMode.Clip,
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden,
            ScrollbarThickness = 6,
            ScrollbarRadius = 3,
            ScrollbarThumbColor = Theme.BorderStrong,
            ScrollbarTrackColor = SKColors.Transparent,
            Style = new ElementStyle(),
        };
        _page = new VisualElement { Style = new ElementStyle() };
        _scroll.AddChild(_page);
        AddChild(_scroll);

        _heading = Add(new Label("Connect to a server", Theme.FontXl, Theme.TextPrimary, Theme.WeightSemibold));
        _subheading = Add(new Label("Type an address, or search your saved hosts by name, user or host.", Theme.FontBase, Theme.TextMuted));
        _quick = Add(new TextField("user@host[:port], or search saved hosts") { LeadingIcon = "terminal", FontSize = Theme.FontLg, Mono = true, ShowClearButton = true });
        _quick.Submitted += Connect;
        _quick.Changed += text =>
        {
            _query = text.Trim();
            ApplyFilter();
            ShowHint(null);
        };
        _quick.KeyPreview = OnQuickKey;
        _connect = Add(new Button("Connect", ButtonVariant.Primary, "bolt") { FontSize = Theme.FontMd });
        _connect.Clicked += Connect;
        _hint = Add(new Label("", Theme.FontSm, Theme.TextMuted));
        _recentCaption = Add(new Label("RECENT", Theme.FontXs, Theme.TextSecondary, Theme.WeightSemibold));
        _recent = Add(new HostCardGrid(main, showLastUsed: true));
        _allCaption = Add(new Label("ALL HOSTS", Theme.FontXs, Theme.TextSecondary, Theme.WeightSemibold));
        _all = Add(new HostCardGrid(main, showLastUsed: false));
        _emptyText = Add(new Label(EmptyText(main.Services.IsLocal), Theme.FontBase, Theme.TextMuted) { MaxLines = 2 });
        _newHost = Add(new Button("New host", ButtonVariant.Secondary, "plus"));
        _newHost.Clicked += () => Host.EditHost(null);
        _recent.HostClicked += host => Host.ConnectInTab(this, host);
        _all.HostClicked += host => Host.ConnectInTab(this, host);
        ShowHint(null);
    }

    public override VisualElement? DefaultFocus => _quick;

    /// <summary>The quick-connect field (Ctrl+L focuses it).</summary>
    public TextField QuickConnectField => _quick;

    public override void OnAttached() => Refresh();

    /// <summary>Nothing typed into the quick-connect field yet.</summary>
    public bool IsBlank => _quick.Text.Length == 0;

    public override WorkspaceTab SaveState() => new();

    /// <summary>Re-reads hosts from the vault (called by <see cref="MainView"/> on every vault change).</summary>
    public void Refresh()
    {
        _emptyText.Text = EmptyText(Host.Services.IsLocal); // the mode can change (upload to a server account)
        ApplyFilter();
    }

    private bool Searching => _query.Length > 0;

    // All saved hosts, or those matching the text typed so far (the keyboard choice stays on the same host).
    private void ApplyFilter()
    {
        VaultData vault = Host.Services.Vault.Current;
        HostEntry? chosen = _all.Selected >= 0 && _all.Selected < _all.Hosts.Count ? _all.Hosts[_all.Selected] : null;
        _all.Hosts = Searching ? QuickConnect.Search(_query, vault) : vault.Hosts.OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (chosen is not null && Searching)
            _all.Selected = _all.Hosts.ToList().FindIndex(h => h.Id == chosen.Id);
        _allCaption.Text = Searching ? $"MATCHING SAVED HOSTS · {_all.Hosts.Count}" : "ALL HOSTS";
        InvalidateLayout();
    }

    // Down/Up move through the matching hosts (Up from the first one returns to the typed text).
    private bool OnQuickKey(KeyStroke k)
    {
        if (!Searching || k.Modifiers != KeyModifiers.None || _all.Hosts.Count == 0)
            return false;
        int step = k.Key switch { Key.Down => 1, Key.Up => -1, _ => 0 };
        if (step == 0)
            return false;
        _all.Selected = Math.Clamp(_all.Selected + step, -1, _all.Hosts.Count - 1);
        ShowHint(null);
        if (_all.Selected >= 0)
        {
            (float top, float bottom) = _all.CardSpan(_all.Selected);
            float y = _all.Transform.Computed.Y - _page.Transform.Computed.Y;
            float viewH = _scroll.Transform.Computed.Height;
            if (y + top - 8 < _scroll.ScrollY)
                _scroll.ScrollY = y + top - 8;
            else if (y + bottom + 8 > _scroll.ScrollY + viewH)
                _scroll.ScrollY = y + bottom + 8 - viewH;
        }
        return true;
    }

    private static string EmptyText(bool local) =>
        $"You have no saved hosts yet. Add one to see it here{(local ? "" : " and on all your devices")}.";

    public void FocusQuickConnect()
    {
        _quick.Focus();
        _quick.SelectAll();
    }

    private T Add<T>(T element) where T : VisualElement
    {
        _page.AddChild(element);
        return element;
    }

    // The saved host chosen with the keyboard; else what was typed: a saved host's name or an address (a saved host
    // with that same address and user is used, with its settings).
    private void Connect()
    {
        VaultData vault = Host.Services.Vault.Current;
        if (Searching && _all.Selected >= 0 && _all.Selected < _all.Hosts.Count)
        {
            Host.ConnectInTab(this, _all.Hosts[_all.Selected]);
            return;
        }
        HostEntry? host = QuickConnect.Parse(_quick.Text, vault, out string? error);
        if (host is null)
        {
            // Just part of a name or address typed, with a single saved host matching it: that one.
            if (Searching && _all.Hosts.Count == 1)
            {
                Host.ConnectInTab(this, _all.Hosts[0]);
                return;
            }
            ShowHint(error);
            _quick.Focus();
            return;
        }
        Host.ConnectInTab(this, vault.FindHost(host.Id) ?? QuickConnect.FindSaved(host, vault) ?? host);
    }

    private void ShowHint(string? error)
    {
        _quick.HasError = error is not null;
        string idle = !Searching ? "Press Enter to connect  ·  Ctrl+L to focus  ·  Ctrl+T for a new tab"
            : _all.Selected >= 0 ? $"Enter connects to {_all.Hosts[_all.Selected].DisplayName}  ·  ↑ ↓ to choose another  ·  Esc to clear"
            : _all.Hosts.Count > 0 ? "↓ to choose a matching saved host  ·  Enter connects to the address typed"
            : "No saved host matches  ·  Enter connects to the address typed";
        _hint.Text = error ?? idle;
        _hint.Color = error is null ? Theme.TextMuted : Theme.Danger;
    }

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        _scroll.Transform.SetLocalFrame(0, 0, w, h);

        // Narrow when it is a pane of a split view: smaller margins so the field keeps a usable width.
        float sidePad = w < 560 ? 16 : SidePad;
        float colW = Math.Max(0, Math.Min(MaxColumnW, w - 2 * sidePad));
        float x = MathF.Round((w - colW) / 2f);
        float y = Math.Clamp(h * 0.08f, 20, 72);
        _heading.Transform.SetLocalFrame(x, y, colW, 30);
        y += 32;
        _subheading.Transform.SetLocalFrame(x, y, colW, 20);
        y += 36;
        _quick.Transform.SetLocalFrame(x, y, colW - ConnectW - 10, QuickH);
        _connect.Transform.SetLocalFrame(x + colW - ConnectW, y, ConnectW, QuickH);
        y += QuickH + 8;
        _hint.Transform.SetLocalFrame(x + 2, y, colW, 18);
        y += 18 + 36;

        VaultData vault = Host.Services.Vault.Current;
        int columns = HostCardGrid.ColumnsFor(colW);
        List<HostEntry> recent = Searching ? [] : vault.Hosts.Where(hh => hh.LastConnected is not null)
            .OrderByDescending(hh => hh.LastConnected).Take(columns).ToList();
        if (!recent.SequenceEqual(_recent.Hosts))
            _recent.Hosts = recent;

        bool hasRecent = recent.Count > 0, hasHosts = _all.Hosts.Count > 0 || (Searching && vault.Hosts.Count > 0);
        _recentCaption.Visible = _recent.Visible = hasRecent;
        if (hasRecent)
            y = Section(_recentCaption, _recent, x, y, colW);
        _allCaption.Visible = _all.Visible = hasHosts;
        _emptyText.Visible = _newHost.Visible = !hasHosts;
        if (hasHosts)
        {
            y = Section(_allCaption, _all, x, y, colW);
        }
        else
        {
            _emptyText.Transform.SetLocalFrame(x, y, colW, _emptyText.MeasureHeight(colW));
            y += _emptyText.MeasureHeight(colW) + 12;
            _newHost.Transform.SetLocalFrame(x, y, _newHost.PreferredWidth, 34);
            y += 34 + 32;
        }
        _page.Transform.SetLocalFrame(0, 0, w, Math.Max(h, y - 8)); // y includes a 32px section gap; keep 24px
        _scroll.InvalidateLayout();
    }

    private static float Section(Label caption, HostCardGrid grid, float x, float y, float w)
    {
        caption.Transform.SetLocalFrame(x + 2, y, w, 16);
        y += 16 + 10;
        float gh = HostCardGrid.HeightFor(grid.Hosts.Count, w);
        grid.Transform.SetLocalFrame(x, y, w, gh);
        return y + gh + 32;
    }
}
