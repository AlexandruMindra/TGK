using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Views;
using TGK.Core.Models;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Main;

/// <summary>Left column: live host search, collapsible groups of saved hosts, and "New host" / "Keys" buttons.</summary>
public sealed class Sidebar : Control, IKeyInput
{
    private const float SearchTop = 12, SearchH = 32, FooterH = 52;
    private readonly TextField _search;
    private readonly ScrollContainer _scroll;
    private readonly HostList _list;
    private readonly Button _newHost;
    private readonly Button _keys;

    public Sidebar(MainView main)
    {
        _list = new HostList(main);
        _list.TypeAhead += TypeAhead;
        _search = new TextField("Search hosts") { LeadingIcon = "search", ShowClearButton = true };
        _search.Changed += text => _list.Filter = text;
        _search.Submitted += _list.ConnectFirstMatch;
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
        _scroll.AddChild(_list);
        _list.HeightChanged += () => InvalidateLayout();
        _newHost = new Button("New host", ButtonVariant.Ghost, "plus");
        _newHost.Clicked += () => main.EditHost(null);
        _keys = new Button("Keys", ButtonVariant.Ghost, "key");
        _keys.Clicked += main.ShowIdentities;
        AddChild(_search);
        AddChild(_scroll);
        AddChild(_newHost);
        AddChild(_keys);
    }

    public TextField Search => _search;

    public HostList List => _list;

    /// <summary>Rebuilds the rows from the current vault snapshot.</summary>
    public void Refresh() => _list.Rebuild();

    protected override void LayoutChildren()
    {
        _search.Transform.SetLocalFrame(12, SearchTop, W - 24, SearchH);
        float listTop = SearchTop + SearchH + 10;
        float listH = Math.Max(0, H - listTop - FooterH);
        _scroll.Transform.SetLocalFrame(0, listTop, W, listH);
        _list.Transform.SetLocalFrame(0, 0, W, Math.Max(listH, _list.ContentHeight));
        _scroll.InvalidateLayout();
        float bw = (W - 24 - 8) / 2f;
        _newHost.Transform.SetLocalFrame(12, H - FooterH + 10, bw, 32);
        _keys.Transform.SetLocalFrame(12 + bw + 8, H - FooterH + 10, bw, 32);
    }

    // Keys the search box does not use: Down moves into the list.
    public bool OnKey(KeyStroke k)
    {
        if (k.Is(Key.Down) && _search.IsFocused)
        {
            _list.FocusFirst();
            return true;
        }
        return false;
    }

    public void OnText(string text) { }

    /// <summary>Typing while the list has focus continues in the search box.</summary>
    private void TypeAhead(string text)
    {
        _search.Focus();
        _search.OnText(text);
    }

    /// <summary>Scrolls so the vertical range [<paramref name="top"/>, <paramref name="bottom"/>] of the list is visible.</summary>
    internal void ScrollIntoView(float top, float bottom)
    {
        float viewH = _scroll.Transform.Computed.Height;
        if (top < _scroll.ScrollY)
            _scroll.ScrollY = top;
        else if (bottom > _scroll.ScrollY + viewH)
            _scroll.ScrollY = bottom - viewH;
    }

    protected override void Paint(SKCanvas c)
    {
        Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.Sidebar);
        Gfx.Line(c, W - 0.5f, 0, W - 0.5f, H, Theme.Border);
        Gfx.Line(c, 0, H - FooterH + 0.5f, W - 1, H - FooterH + 0.5f, Theme.Border);
    }
}

/// <summary>
/// The grouped host list, custom-drawn as one element inside the sidebar's scroll container. Click selects,
/// double-click / Enter connects in a new tab, right-click opens the host menu, arrows move the selection.
/// </summary>
public sealed class HostList : Control, IKeyInput
{
    private const float GroupH = 30, HostH = 46, TopPad = 2;
    private const int DoubleClickMs = 400;
    private readonly MainView _main;
    private readonly List<Row> _rows = [];
    private string _filter = "";
    private int _hover = -1;
    private Guid? _selected;
    private long _lastClickMs;
    private int _lastClickRow = -1;

    public HostList(MainView main)
    {
        _main = main;
        ReceivesKeyboard = true;
        Events.OnMouseMove += (_, e) => SetHover(RowAt(e.Relative.Y));
        Events.OnMouseDown += OnDown;
        OnFocused += _ => InvalidatePaint();
        OnFocusLost += _ => InvalidatePaint();
    }

    /// <summary>Raised when the number of rows (and so the content height) changed.</summary>
    public event Action? HeightChanged;

    /// <summary>Raised with text typed while the list has focus.</summary>
    public event Action<string>? TypeAhead;

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value.Trim();
            Rebuild();
        }
    }

    public float ContentHeight => TopPad + _rows.Sum(r => r.Height) + 8;

    public HostEntry? SelectedHost => _selected is { } id ? _main.Services.Vault.Current.FindHost(id) : null;

    private enum RowKind { Group, Host, Empty }

    private sealed record Row(RowKind Kind, string Label, HostEntry? Host = null, HostGroup? Group = null, int Count = 0, bool Collapsed = false)
    {
        public float Height => Kind == RowKind.Host ? HostH : GroupH;
    }

    public void Rebuild()
    {
        float oldHeight = ContentHeight;
        VaultData vault = _main.Services.Vault.Current;
        _rows.Clear();
        bool filtering = _filter.Length > 0;

        var sections = vault.Groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Group: (HostGroup?)g, Name: g.Name))
            .Append((Group: null, Name: "Ungrouped"));
        foreach ((HostGroup? group, string name) in sections)
        {
            List<HostEntry> hosts = vault.Hosts
                .Where(h => group is null ? vault.FindGroup(h.GroupId) is null : h.GroupId == group.Id)
                .Where(h => !filtering || Matches(h, vault, name))
                .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hosts.Count == 0)
                continue;
            bool collapsed = !filtering && group is { Collapsed: true };
            _rows.Add(new Row(RowKind.Group, name, Group: group, Count: hosts.Count, Collapsed: collapsed));
            if (!collapsed)
                _rows.AddRange(hosts.Select(h => new Row(RowKind.Host, h.DisplayName, Host: h)));
        }
        if (_rows.Count == 0)
            _rows.Add(new Row(RowKind.Empty, filtering ? $"No hosts match “{_filter}”" : "No saved hosts yet"));

        if (_selected is { } sel && !_rows.Any(r => r.Host?.Id == sel))
            _selected = null;
        _hover = -1;
        InvalidatePaint();
        if (Math.Abs(oldHeight - ContentHeight) > 0.5f)
            HeightChanged?.Invoke();
    }

    private bool Matches(HostEntry h, VaultData vault, string groupName)
    {
        bool Has(string? s) => s is not null && s.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        return Has(h.Name) || Has(h.Host) || Has(HostFormat.UserOf(h, vault)) || Has(groupName);
    }

    /// <summary>Enter in the search box: connect to the selected (or first matching) host in a new tab.</summary>
    public void ConnectFirstMatch()
    {
        HostEntry? host = SelectedHost ?? _rows.FirstOrDefault(r => r.Kind == RowKind.Host)?.Host;
        if (host is not null)
            _main.ConnectInNewTab(host);
    }

    /// <summary>Focuses the list and selects the first host (keeps an existing selection).</summary>
    public void FocusFirst()
    {
        ParentView?.SetActiveKeyboardElement(this);
        if (SelectedHost is null && _rows.FirstOrDefault(r => r.Kind == RowKind.Host)?.Host is { } first)
            Select(first.Id);
    }

    private float RowTop(int index)
    {
        float y = TopPad;
        for (int i = 0; i < index; i++)
            y += _rows[i].Height;
        return y;
    }

    private int RowAt(float y)
    {
        float top = TopPad;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (y >= top && y < top + _rows[i].Height)
                return _rows[i].Kind == RowKind.Empty ? -1 : i;
            top += _rows[i].Height;
        }
        return -1;
    }

    private void SetHover(int index) => SetAndPaint(ref _hover, index);

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _hover = -1;
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        int index = RowAt(e.Relative.Y);
        if (index < 0)
        {
            if (e.Button == 1)
                _main.ShowGroupMenu(null, e.Global.X, e.Global.Y);
            return;
        }
        Row row = _rows[index];
        if (row.Kind == RowKind.Group)
        {
            if (e.Button == 0)
                ToggleGroup(row);
            else if (e.Button == 1)
                _main.ShowGroupMenu(row.Group, e.Global.X, e.Global.Y);
            return;
        }
        if (row.Host is not { } host)
            return;
        Select(host.Id);
        if (e.Button == 1)
        {
            _main.ShowHostMenu(host, e.Global.X, e.Global.Y);
            return;
        }
        if (e.Button != 0)
            return;
        long now = UiClock.NowMs;
        if (index == _lastClickRow && now - _lastClickMs < DoubleClickMs)
        {
            _lastClickRow = -1;
            _main.ConnectInNewTab(host);
            return;
        }
        _lastClickRow = index;
        _lastClickMs = now;
    }

    private void ToggleGroup(Row row)
    {
        if (row.Group is not { } group || _filter.Length > 0)
            return;
        HostGroup copy = group.Clone();
        copy.Collapsed = !copy.Collapsed;
        _main.RunVault(() => _main.Services.Vault.SaveGroupAsync(copy));
    }

    private void Select(Guid id)
    {
        if (_selected == id)
            return;
        _selected = id;
        InvalidatePaint();
        int index = _rows.FindIndex(r => r.Host?.Id == id);
        if (index >= 0 && Parent?.Parent is Sidebar sidebar)
            sidebar.ScrollIntoView(RowTop(index), RowTop(index) + HostH);
    }

    public bool OnKey(KeyStroke k)
    {
        List<int> hostRows = Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Kind == RowKind.Host).ToList();
        int current = _selected is { } id ? hostRows.FindIndex(i => _rows[i].Host!.Id == id) : -1;
        switch (k.Key)
        {
            case Key.Down when k.Modifiers == KeyModifiers.None && hostRows.Count > 0:
                Select(_rows[hostRows[Math.Min(current + 1, hostRows.Count - 1)]].Host!.Id);
                return true;
            case Key.Up when k.Modifiers == KeyModifiers.None && hostRows.Count > 0:
                Select(_rows[hostRows[Math.Max(current - 1, 0)]].Host!.Id);
                return true;
            case Key.Enter or Key.KeypadEnter when !k.IsRepeat && SelectedHost is { } host:
                _main.ConnectInNewTab(host);
                return true;
            case Key.Delete when !k.IsRepeat && SelectedHost is { } host:
                _main.DeleteHost(host);
                return true;
            case Key.F2 when SelectedHost is { } host:
                _main.EditHost(host);
                return true;
            default:
                return false;
        }
    }

    public void OnText(string text) => TypeAhead?.Invoke(text);

    protected override void Paint(SKCanvas c)
    {
        VaultData vault = _main.Services.Vault.Current;
        SKRect clip = c.LocalClipBounds;
        float y = TopPad;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            float h = row.Height;
            if (y + h >= clip.Top && y <= clip.Bottom)
                PaintRow(c, row, i, y, h, vault);
            y += h;
        }
    }

    private void PaintRow(SKCanvas c, Row row, int index, float y, float h, VaultData vault)
    {
        float cy = y + h / 2f;
        switch (row.Kind)
        {
            case RowKind.Empty:
                Gfx.Text(c, row.Label, W / 2f, y + 28, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center, W - 24);
                break;
            case RowKind.Group:
                if (index == _hover)
                    Gfx.FillRound(c, new SKRect(6, y + 2, W - 6, y + h - 2), Theme.RadiusSm, Theme.SurfaceRaised);
                Icons.Draw(c, row.Collapsed ? "chevron-right" : "chevron-down", 20, cy, 14, Theme.TextMuted);
                Gfx.Text(c, row.Label.ToUpperInvariant(), 32, cy, Theme.FontXs, Theme.WeightSemibold, Theme.TextSecondary, TextAlignment.Left, W - 70);
                Gfx.Text(c, row.Count.ToString(), W - 16, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
                break;
            case RowKind.Host when row.Host is { } host:
                bool selected = host.Id == _selected;
                var r = new SKRect(6, y + 1, W - 6, y + h - 1);
                if (selected)
                    Gfx.FillRound(c, r, Theme.Radius, HasFocus ? Theme.Accent.WithAlpha(56) : Theme.SurfaceHover);
                else if (index == _hover)
                    Gfx.FillRound(c, r, Theme.Radius, Theme.SurfaceRaised);
                SKColor tag = Theme.ParseHex(host.TagColor, Theme.Idle);
                Gfx.Circle(c, 22, y + 16, 4, tag);
                float nameRight = HostHints.Draw(c, host, vault, W - 14, y + 16, selected ? Theme.TextSecondary : Theme.TextMuted);
                Gfx.Text(c, host.DisplayName, 34, y + 16, Theme.FontBase, Theme.WeightRegular, Theme.TextPrimary, TextAlignment.Left, nameRight - 38);
                Gfx.Text(c, HostFormat.Address(host, vault), 34, y + 33, Theme.FontSm, Theme.WeightRegular,
                    selected ? Theme.TextSecondary : Theme.TextMuted, TextAlignment.Left, W - 50);
                break;
        }
    }
}
