using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Core.Sftp;

namespace TGK.Client.Files;

/// <summary>
/// The entries of one remote folder, custom-drawn as a table with a fixed header: Name, Size, Modified and Permissions
/// (the last columns give way when the list is narrow). Click a header to sort. Click selects, Ctrl+click toggles,
/// Shift+click selects a range; double-click or Enter opens; right-click asks for the context menu. Arrows, Page Up/Down,
/// Home/End move (with Shift they extend the selection), Ctrl+A selects all, typing jumps to a name.
/// </summary>
public sealed class FileList : Control, IKeyInput
{
    public const float HeaderH = 30, RowH = 30;
    private const float ThumbW = 6, SizeW = 84, DateW = 128, PermW = 104, IconX = 22;
    private const int DoubleClickMs = 400, TypeAheadMs = 900;

    private List<SftpEntry> _entries = []; // as listed
    private List<SftpEntry> _rows = [];    // filtered and sorted
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private int _cursor = -1, _anchor = -1, _hover = -1;
    private float _scroll;
    private bool _thumbDrag;
    private float _thumbGrab;
    private long _lastClickMs;
    private int _lastClickRow = -1;
    private string _typed = "";
    private long _typedAt;
    private string _filter = "";
    private bool _showHidden;
    private string? _message;
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _descending;
    private DateTimeOffset _now = DateTimeOffset.Now;

    public FileList()
    {
        Style = new Blossom.Core.Visual.ElementStyle { BackColor = Theme.Surface };
        ReceivesKeyboard = true;
        IsClipping = true;
        Events.OnMouseDown += OnDown;
        Events.OnMouseMove += OnMove;
        Events.OnMouseUp += (_, _) =>
        {
            if (_thumbDrag)
            {
                _thumbDrag = false;
                ReleasePointer();
            }
        };
        Events.OnScroll += (_, e) =>
        {
            e.Handled = true;
            ScrollTo(_scroll - e.Offset.Y * RowH * 3);
        };
        OnFocused += _ => InvalidatePaint();
        OnFocusLost += _ => InvalidatePaint();
    }

    /// <summary>Double-click or Enter on an entry.</summary>
    public event Action<SftpEntry>? Activated;

    /// <summary>Right-click at window point (x, y), after the selection was updated for it (empty when on no row).</summary>
    public event Action<float, float>? MenuRequested;

    /// <summary>The selection (or the rows shown) changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>The rows shown: filtered by name and the hidden setting, sorted.</summary>
    public IReadOnlyList<SftpEntry> Rows => _rows;

    /// <summary>Every entry of the folder, as listed.</summary>
    public IReadOnlyList<SftpEntry> Entries => _entries;

    /// <summary>The selected rows, in list order.</summary>
    public IReadOnlyList<SftpEntry> Selected => _rows.Where(r => _selected.Contains(r.Path)).ToList();

    /// <summary>The row with the keyboard cursor.</summary>
    public SftpEntry? Focused => _cursor >= 0 && _cursor < _rows.Count ? _rows[_cursor] : null;

    public FileSortColumn SortColumn => _sortColumn;
    public bool SortDescending => _descending;

    /// <summary>Only names containing this text (any case) are shown.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
                return;
            _filter = value;
            Rebuild();
        }
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (_showHidden == value)
                return;
            _showHidden = value;
            Rebuild();
        }
    }

    /// <summary>
    /// Text shown in place of the rows (e.g. "Loading…" or why the folder can't be listed); null for the rows. An
    /// empty folder says so by itself.
    /// </summary>
    public string? Message
    {
        get => _message;
        set => SetAndPaint(ref _message, value);
    }

    /// <summary>
    /// Shows a folder's entries. <paramref name="select"/> selects (and scrolls to) that path; otherwise the selection
    /// is kept for the paths still there when <paramref name="keepSelection"/>, else the list starts at the top.
    /// </summary>
    public void SetEntries(IReadOnlyList<SftpEntry> entries, string? select = null, bool keepSelection = false)
    {
        string? focused = Focused?.Path;
        _entries = [.. entries];
        _message = null;
        _now = DateTimeOffset.Now;
        if (!keepSelection)
        {
            _selected.Clear();
            _scroll = 0;
            focused = null;
        }
        if (select is not null)
        {
            _selected.Clear();
            _selected.Add(select);
            focused = select;
        }
        Rebuild(focused);
        if (select is not null)
            EnsureVisible(_cursor);
    }

    /// <summary>Sorts by <paramref name="column"/>; the same column again reverses the order.</summary>
    public void SortBy(FileSortColumn column)
    {
        _descending = column == _sortColumn && !_descending;
        _sortColumn = column;
        Rebuild(Focused?.Path);
    }

    public void SelectAll()
    {
        _selected.Clear();
        foreach (SftpEntry row in _rows)
            _selected.Add(row.Path);
        InvalidatePaint();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects only <paramref name="path"/> (when shown) and moves the cursor there.</summary>
    public void SelectPath(string path)
    {
        int index = _rows.FindIndex(r => r.Path == path);
        if (index >= 0)
            SelectOnly(index);
    }

    private void Rebuild(string? focusedPath = null)
    {
        _rows = FileFormat.Sort(_entries.Where(e => FileFormat.Shows(e, _filter, _showHidden)), _sortColumn, _descending);
        _selected.RemoveWhere(p => !_rows.Any(r => r.Path == p));
        _cursor = focusedPath is null ? -1 : _rows.FindIndex(r => r.Path == focusedPath);
        if (_cursor < 0 && _rows.Count > 0)
            _cursor = _selected.Count > 0 ? _rows.FindIndex(r => _selected.Contains(r.Path)) : 0;
        _anchor = _cursor;
        _hover = -1;
        ScrollTo(_scroll);
        InvalidatePaint();
        SelectionChanged?.Invoke();
    }

    // ---- geometry ----

    private float ListH => Math.Max(0, H - HeaderH);
    private float ContentH => _rows.Count * RowH + 4;
    private float MaxScroll => Math.Max(0, ContentH - ListH);
    private bool ShowsDate => W >= 460;
    private bool ShowsPermissions => W >= 600;

    // Right edges of the right-aligned columns; the name column takes what is left.
    private (float SizeRight, float DateLeft, float PermLeft, float NameRight) Columns()
    {
        float right = W - 14;
        float permLeft = ShowsPermissions ? right - PermW : right;
        float dateLeft = ShowsDate ? permLeft - DateW : permLeft;
        float sizeRight = dateLeft - 16;
        return (sizeRight, dateLeft, permLeft, sizeRight - SizeW - 8);
    }

    private int RowAt(float y)
    {
        if (y < HeaderH)
            return -1;
        int index = (int)Math.Floor((y - HeaderH + _scroll) / RowH);
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    private void ScrollTo(float value)
    {
        float clamped = Math.Clamp(value, 0, MaxScroll);
        SetAndPaint(ref _scroll, clamped);
    }

    private void EnsureVisible(int index)
    {
        if (index < 0)
            return;
        float top = index * RowH, bottom = top + RowH;
        if (top < _scroll)
            ScrollTo(top);
        else if (bottom > _scroll + ListH)
            ScrollTo(bottom - ListH + 2);
    }

    protected override void LayoutChildren() => ScrollTo(_scroll); // a resize may lower the maximum

    // ---- mouse ----

    private void OnMove(object? sender, MouseEventArgs e)
    {
        if (_thumbDrag)
        {
            (float top, float height) = Thumb();
            float track = ListH - height;
            if (track > 0)
                ScrollTo((e.Relative.Y - HeaderH - _thumbGrab) / track * MaxScroll);
            return;
        }
        SetAndPaint(ref _hover, RowAt(e.Relative.Y));
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _hover = -1;
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        float x = e.Relative.X, y = e.Relative.Y;
        if (y < HeaderH)
        {
            if (e.Button == 0 && HeaderColumnAt(x) is { } column)
                SortBy(column);
            return;
        }
        if (e.Button == 0 && MaxScroll > 0 && x >= W - ThumbW - 4)
        {
            (float top, float height) = Thumb();
            float local = y - HeaderH;
            if (local < top || local > top + height)
                ScrollTo((local - height / 2) / Math.Max(1, ListH - height) * MaxScroll);
            _thumbGrab = local - Thumb().Top;
            _thumbDrag = true;
            CapturePointer();
            return;
        }
        int index = RowAt(y);
        if (e.Button == 1)
        {
            if (index < 0)
                ClearSelection();
            else if (!_selected.Contains(_rows[index].Path))
                SelectOnly(index);
            MenuRequested?.Invoke(e.Global.X, e.Global.Y);
            return;
        }
        if (e.Button != 0)
            return;
        if (index < 0)
        {
            ClearSelection();
            return;
        }
        KeyModifiers mods = KeyboardHub.CurrentModifiers;
        if ((mods & KeyModifiers.Shift) != 0 && _anchor >= 0)
            SelectRange(_anchor, index, add: (mods & KeyModifiers.Ctrl) != 0);
        else if ((mods & KeyModifiers.Ctrl) != 0)
            Toggle(index);
        else
            SelectOnly(index);

        long now = UiClock.NowMs;
        if (mods == KeyModifiers.None && index == _lastClickRow && now - _lastClickMs < DoubleClickMs)
        {
            _lastClickRow = -1;
            Activated?.Invoke(_rows[index]);
            return;
        }
        _lastClickRow = index;
        _lastClickMs = now;
    }

    private FileSortColumn? HeaderColumnAt(float x)
    {
        (float sizeRight, float dateLeft, float permLeft, float nameRight) = Columns();
        if (ShowsPermissions && x >= permLeft)
            return FileSortColumn.Permissions;
        if (ShowsDate && x >= dateLeft)
            return FileSortColumn.Modified;
        if (x >= nameRight)
            return FileSortColumn.Size;
        return FileSortColumn.Name;
    }

    // ---- selection ----

    private void SelectOnly(int index)
    {
        _selected.Clear();
        _selected.Add(_rows[index].Path);
        _cursor = _anchor = index;
        EnsureVisible(index);
        InvalidatePaint();
        SelectionChanged?.Invoke();
    }

    private void Toggle(int index)
    {
        if (!_selected.Remove(_rows[index].Path))
            _selected.Add(_rows[index].Path);
        _cursor = _anchor = index;
        InvalidatePaint();
        SelectionChanged?.Invoke();
    }

    private void SelectRange(int from, int to, bool add)
    {
        if (!add)
            _selected.Clear();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            _selected.Add(_rows[i].Path);
        _cursor = to;
        EnsureVisible(to);
        InvalidatePaint();
        SelectionChanged?.Invoke();
    }

    private void ClearSelection()
    {
        if (_selected.Count == 0)
            return;
        _selected.Clear();
        InvalidatePaint();
        SelectionChanged?.Invoke();
    }

    private void MoveCursor(int index, bool extend)
    {
        if (_rows.Count == 0)
            return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (extend && _anchor >= 0)
            SelectRange(_anchor, index, add: false);
        else
            SelectOnly(index);
    }

    // ---- keyboard ----

    public bool OnKey(KeyStroke k)
    {
        bool shift = k.Modifiers == KeyModifiers.Shift;
        if (k.Modifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            if (k.Is(Key.A, KeyModifiers.Ctrl))
            {
                SelectAll();
                return true;
            }
            return false;
        }
        int page = Math.Max(1, (int)(ListH / RowH) - 1);
        switch (k.Key)
        {
            case Key.Down:
                MoveCursor(_cursor + 1, shift);
                return true;
            case Key.Up:
                MoveCursor(_cursor < 0 ? 0 : _cursor - 1, shift);
                return true;
            case Key.PageDown:
                MoveCursor(_cursor + page, shift);
                return true;
            case Key.PageUp:
                MoveCursor(_cursor - page, shift);
                return true;
            case Key.Home:
                MoveCursor(0, shift);
                return true;
            case Key.End:
                MoveCursor(_rows.Count - 1, shift);
                return true;
            case Key.Enter or Key.KeypadEnter when !shift && !k.IsRepeat && Focused is { } entry:
                if (!_selected.Contains(entry.Path))
                    SelectOnly(_cursor);
                Activated?.Invoke(entry);
                return true;
            case Key.Escape when !shift && _selected.Count > 0:
                ClearSelection();
                return true;
            default:
                return false;
        }
    }

    // Typing jumps to the first name starting with what was typed (letters typed quickly add up).
    public void OnText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _rows.Count == 0)
            return;
        long now = UiClock.NowMs;
        _typed = now - _typedAt < TypeAheadMs ? _typed + text : text;
        _typedAt = now;
        int start = Math.Max(0, _cursor);
        for (int n = 0; n < _rows.Count; n++)
        {
            int i = (start + n + (_typed.Length == 1 ? 1 : 0)) % _rows.Count;
            if (_rows[i].Name.StartsWith(_typed, StringComparison.OrdinalIgnoreCase))
            {
                SelectOnly(i);
                return;
            }
        }
    }

    // ---- painting ----

    private (float Top, float Height) Thumb()
    {
        float track = ListH;
        float height = Math.Max(24, track * track / Math.Max(track, ContentH));
        float top = MaxScroll > 0 ? (track - height) * (_scroll / MaxScroll) : 0;
        return (top, height);
    }

    protected override void Paint(SKCanvas c)
    {
        (float sizeRight, float dateLeft, float permLeft, float nameRight) = Columns();
        PaintHeader(c, sizeRight, dateLeft, permLeft);

        c.Save();
        c.ClipRect(new SKRect(0, HeaderH, W, H));
        if (_rows.Count == 0 || _message is not null)
        {
            string text = _message ?? (_entries.Count == 0 ? "This folder is empty."
                : _filter.Length > 0 ? $"Nothing here matches “{_filter}”."
                : "Only hidden files here (Ctrl+H shows them).");
            foreach ((string line, int i) in Gfx.Wrap(text, Gfx.Font(Theme.FontBase), Math.Max(40, W - 48), 4).Select((l, i) => (l, i)))
                Gfx.Text(c, line, W / 2f, HeaderH + 40 + i * 20, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center);
            c.Restore();
            return;
        }

        SKFont nameFont = Gfx.Font(Theme.FontBase);
        SKFont smallFont = Gfx.Font(Theme.FontSm);
        SKFont monoFont = Gfx.Font(Theme.FontSm, Theme.Mono);
        int first = Math.Max(0, (int)(_scroll / RowH));
        int last = Math.Min(_rows.Count - 1, (int)((_scroll + ListH) / RowH) + 1);
        for (int i = first; i <= last; i++)
        {
            SftpEntry entry = _rows[i];
            float y = HeaderH + i * RowH - _scroll;
            float cy = y + RowH / 2f;
            var r = new SKRect(4, y + 1, W - 4 - (MaxScroll > 0 ? ThumbW : 0), y + RowH - 1);
            bool selected = _selected.Contains(entry.Path);
            if (selected)
                Gfx.FillRound(c, r, Theme.RadiusSm, HasFocus ? Theme.Accent.WithAlpha(56) : Theme.SurfaceHover);
            else if (i == _hover)
                Gfx.FillRound(c, r, Theme.RadiusSm, Theme.SurfaceRaised);
            if (i == _cursor && HasFocus && _selected.Count > 1)
                Gfx.StrokeRound(c, r, Theme.RadiusSm, Theme.Accent.WithAlpha(150));

            bool dir = entry.IsDirectory;
            Icons.Draw(c, dir ? "folder" : "file", IconX, cy, 16, dir ? Theme.Accent : Theme.TextSecondary);
            if (entry.Kind == SftpEntryKind.Symlink)
                Icons.Draw(c, "link", IconX + 7, cy + 6, 9, entry.IsBrokenLink ? Theme.Danger : Theme.TextMuted);

            float nameLeft = 40;
            string name = FileFormat.Printable(entry.Name);
            SKColor nameColor = entry.IsHidden ? Theme.TextSecondary : Theme.TextPrimary;
            float nameMax = Math.Max(0, nameRight - nameLeft);
            Gfx.Text(c, name, nameLeft, cy, nameFont, nameColor, TextAlignment.Left, nameMax);
            if (entry.Kind == SftpEntryKind.Symlink)
            {
                float used = Math.Min(nameMax, Gfx.Measure(name, nameFont));
                string target = entry.IsBrokenLink ? $"→ {FileFormat.Printable(entry.LinkTarget ?? "?")} (missing)" : $"→ {FileFormat.Printable(entry.LinkTarget ?? "")}";
                Gfx.Text(c, target, nameLeft + used + 8, cy, smallFont, entry.IsBrokenLink ? Theme.Danger : Theme.TextMuted,
                    TextAlignment.Left, nameMax - used - 8);
            }

            SKColor muted = selected ? Theme.TextSecondary : Theme.TextMuted;
            Gfx.Text(c, dir ? "—" : FileFormat.Size(entry.Size), sizeRight, cy, smallFont, muted, TextAlignment.Right);
            if (ShowsDate)
                Gfx.Text(c, FileFormat.Date(entry.Modified, _now), dateLeft, cy, smallFont, muted, TextAlignment.Left, DateW - 8);
            if (ShowsPermissions)
                Gfx.Text(c, entry.Permissions, permLeft, cy, monoFont, muted, TextAlignment.Left, PermW);
        }

        if (MaxScroll > 0)
        {
            (float top, float height) = Thumb();
            Gfx.FillRound(c, new SKRect(W - ThumbW - 2, HeaderH + top + 2, W - 2, HeaderH + top + height - 2), 3,
                _thumbDrag ? Theme.TextMuted : Theme.BorderStrong);
        }
        c.Restore();
    }

    private void PaintHeader(SKCanvas c, float sizeRight, float dateLeft, float permLeft)
    {
        Gfx.Line(c, 0, HeaderH - 0.5f, W, HeaderH - 0.5f, Theme.Border);
        float cy = HeaderH / 2f;
        Column(c, "Name", 40, cy, FileSortColumn.Name, TextAlignment.Left);
        Column(c, "Size", sizeRight, cy, FileSortColumn.Size, TextAlignment.Right);
        if (ShowsDate)
            Column(c, "Modified", dateLeft, cy, FileSortColumn.Modified, TextAlignment.Left);
        if (ShowsPermissions)
            Column(c, "Permissions", permLeft, cy, FileSortColumn.Permissions, TextAlignment.Left);
    }

    private void Column(SKCanvas c, string label, float x, float cy, FileSortColumn column, TextAlignment align)
    {
        bool active = column == _sortColumn;
        SKColor color = active ? Theme.TextPrimary : Theme.TextMuted;
        Gfx.Text(c, label, x, cy, Theme.FontXs, Theme.WeightSemibold, color, align);
        if (!active)
            return;
        float width = Gfx.Measure(label, Theme.FontXs, Theme.WeightSemibold);
        float ax = align == TextAlignment.Right ? x - width - 9 : x + width + 9;
        c.Save();
        if (!_descending)
            c.RotateDegrees(180, ax, cy);
        Icons.Draw(c, "chevron-down", ax, cy, 12, color);
        c.Restore();
    }
}
