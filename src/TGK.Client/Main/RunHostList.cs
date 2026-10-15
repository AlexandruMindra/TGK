using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Main;

/// <summary>
/// The hosts of a run tab, grouped as in the sidebar and custom-drawn as one element inside a scroll container. The
/// check boxes choose where the command runs (a group's box checks all its hosts); each host shows how it is doing in
/// the current run. A click on a host shows its output alone, a second click shows all hosts again; a click on a group
/// folds it.
/// </summary>
public sealed class RunHostList : Control
{
    private const float GroupH = 30, HostH = 44, TopPad = 4, BoxX = 12, BoxSize = 14, BoxHit = 34;
    private readonly List<Row> _rows = [];
    private readonly HashSet<Guid> _checked = [];
    private readonly HashSet<Guid> _folded = []; // group ids (Guid.Empty: the ungrouped hosts)
    private readonly Dictionary<Guid, HostRun> _runs = [];
    private VaultData _vault = new();
    private int _hover = -1;
    private Guid? _focused;
    private bool _locked;

    public RunHostList()
    {
        Events.OnMouseMove += OnMove;
        Events.OnMouseDown += OnDown;
    }

    /// <summary>Raised when the number of rows (and so the content height) changed.</summary>
    public event Action? HeightChanged;

    /// <summary>Raised when the user checked or unchecked hosts.</summary>
    public event Action? CheckedChanged;

    /// <summary>Raised when the user clicked a host (its id) to see its output alone, or clicked it again (null).</summary>
    public event Action<Guid?>? FocusChanged;

    public float ContentHeight => TopPad + _rows.Sum(r => r.Height) + 8;

    /// <summary>The checked hosts that still exist (folded groups' too), in the order shown.</summary>
    public IReadOnlyList<HostEntry> CheckedHosts => _rows.Where(r => r.Host is null).SelectMany(r => r.Hosts).Where(h => _checked.Contains(h.Id)).ToList();

    /// <summary>The host whose output is shown alone, or null for all.</summary>
    public Guid? Focused
    {
        get => _focused;
        set => SetAndPaint(ref _focused, value);
    }

    /// <summary>While a run is going: the check boxes don't change.</summary>
    public bool Locked
    {
        get => _locked;
        set => SetAndPaint(ref _locked, value);
    }

    /// <summary>Checks <paramref name="ids"/> (and only them), folding the groups that have none of them.</summary>
    public void Check(IEnumerable<Guid> ids, VaultData vault)
    {
        _checked.Clear();
        _checked.UnionWith(ids);
        _folded.Clear();
        foreach (Guid group in vault.Groups.Select(g => g.Id).Append(Guid.Empty))
        {
            if (!vault.Hosts.Any(h => GroupOf(h, vault) == group && _checked.Contains(h.Id)))
                _folded.Add(group);
        }
        Rebuild(vault);
    }

    /// <summary>How <paramref name="host"/> is doing in the current run (null: not part of it).</summary>
    public void SetRun(Guid host, HostRun? run)
    {
        if (run is null)
            _runs.Remove(host);
        else
            _runs[host] = run;
        InvalidatePaint();
    }

    public void ClearRuns()
    {
        _runs.Clear();
        InvalidatePaint();
    }

    /// <summary>Rebuilds the rows from <paramref name="vault"/> (hosts deleted meanwhile are dropped, new ones unchecked).</summary>
    public void Rebuild(VaultData vault)
    {
        float oldHeight = ContentHeight;
        _vault = vault;
        _rows.Clear();
        var sections = vault.Groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Id, Name: g.Name))
            .Append((Id: Guid.Empty, Name: "Ungrouped"));
        foreach ((Guid id, string name) in sections)
        {
            List<HostEntry> hosts = vault.Hosts.Where(h => GroupOf(h, vault) == id)
                .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            if (hosts.Count == 0)
                continue;
            _rows.Add(new Row(id, name, null, hosts));
            if (!_folded.Contains(id))
                _rows.AddRange(hosts.Select(h => new Row(id, h.DisplayName, h, [])));
        }
        _checked.IntersectWith(vault.Hosts.Select(h => h.Id));
        _hover = -1;
        InvalidatePaint();
        if (Math.Abs(oldHeight - ContentHeight) > 0.5f)
            HeightChanged?.Invoke();
    }

    private static Guid GroupOf(HostEntry host, VaultData vault) => vault.FindGroup(host.GroupId) is { } group ? group.Id : Guid.Empty;

    /// <summary>A group header (<see cref="Host"/> null, with its <see cref="Hosts"/>) or a host.</summary>
    private sealed record Row(Guid Group, string Label, HostEntry? Host, List<HostEntry> Hosts)
    {
        public float Height => Host is null ? GroupH : HostH;
    }

    private int RowAt(float y)
    {
        float top = TopPad;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (y >= top && y < top + _rows[i].Height)
                return i;
            top += _rows[i].Height;
        }
        return -1;
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            SetAndPaint(ref _hover, -1);
    }

    private void OnMove(object? sender, MouseEventArgs e) => SetAndPaint(ref _hover, RowAt(e.Relative.Y));

    private void OnDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        int index = RowAt(e.Relative.Y);
        if (index < 0 || e.Button != 0)
            return;
        Row row = _rows[index];
        bool onBox = e.Relative.X < BoxHit;
        if (row.Host is null)
        {
            if (onBox && !_locked)
            {
                bool all = row.Hosts.All(h => _checked.Contains(h.Id));
                foreach (HostEntry h in row.Hosts)
                {
                    if (all)
                        _checked.Remove(h.Id);
                    else
                        _checked.Add(h.Id);
                }
                InvalidatePaint();
                CheckedChanged?.Invoke();
            }
            else if (!onBox)
            {
                if (!_folded.Remove(row.Group))
                    _folded.Add(row.Group);
                Rebuild(_vault);
            }
            return;
        }
        HostEntry host = row.Host;
        if (onBox)
        {
            if (_locked)
                return;
            if (!_checked.Remove(host.Id))
                _checked.Add(host.Id);
            InvalidatePaint();
            CheckedChanged?.Invoke();
            return;
        }
        Focused = _focused == host.Id ? null : host.Id;
        FocusChanged?.Invoke(_focused);
    }

    protected override void Paint(SKCanvas c)
    {
        SKRect clip = c.LocalClipBounds;
        float y = TopPad;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            if (y + row.Height >= clip.Top && y <= clip.Bottom)
            {
                if (row.Host is { } host)
                    PaintHost(c, host, i, y);
                else
                    PaintGroup(c, row, i, y);
            }
            y += row.Height;
        }
        if (_rows.Count == 0)
            Gfx.Text(c, "No saved hosts yet", W / 2f, TopPad + 24, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center, W - 24);
    }

    private void PaintGroup(SKCanvas c, Row row, int index, float y)
    {
        float cy = y + GroupH / 2f;
        if (index == _hover)
            Gfx.FillRound(c, new SKRect(6, y + 2, W - 6, y + GroupH - 2), Theme.RadiusSm, Theme.SurfaceRaised);
        int on = row.Hosts.Count(h => _checked.Contains(h.Id));
        PaintBox(c, cy, on == row.Hosts.Count ? "check" : on > 0 ? "minus" : null);
        bool folded = _folded.Contains(row.Group);
        Icons.Draw(c, folded ? "chevron-right" : "chevron-down", BoxHit + 6, cy, 14, Theme.TextMuted);
        Gfx.Text(c, row.Label.ToUpperInvariant(), BoxHit + 18, cy, Theme.FontXs, Theme.WeightSemibold, Theme.TextSecondary, TextAlignment.Left, W - BoxHit - 70);
        Gfx.Text(c, $"{on}/{row.Hosts.Count}", W - 14, cy, Theme.FontXs, Theme.WeightRegular, on > 0 ? Theme.TextSecondary : Theme.TextMuted, TextAlignment.Right);
    }

    private void PaintHost(SKCanvas c, HostEntry host, int index, float y)
    {
        bool on = _checked.Contains(host.Id), focused = host.Id == _focused;
        var r = new SKRect(6, y + 1, W - 6, y + HostH - 1);
        if (focused)
            Gfx.FillRound(c, r, Theme.Radius, Theme.Accent.WithAlpha(56));
        else if (index == _hover)
            Gfx.FillRound(c, r, Theme.Radius, Theme.SurfaceRaised);
        PaintBox(c, y + HostH / 2f, on ? "check" : null);

        float x = BoxHit + 6, line1 = y + 15, line2 = y + 31;
        Gfx.Circle(c, x + 4, line1, 4, Theme.ParseHex(host.TagColor, Theme.Idle));
        _runs.TryGetValue(host.Id, out HostRun? run);
        (string? icon, SKColor color) = Look(run);
        float nameRight = W - 14;
        if (icon is not null)
        {
            Icons.Draw(c, icon, W - 20, line1, 13, color);
            nameRight -= 20;
        }
        Gfx.Text(c, host.DisplayName, x + 14, line1, Theme.FontBase, Theme.WeightRegular, on ? Theme.TextPrimary : Theme.TextMuted,
            TextAlignment.Left, nameRight - x - 14);
        string detail = run is null ? HostFormat.Address(host, _vault) : Detail(run);
        Gfx.Text(c, detail, x + 14, line2, Theme.FontSm, Theme.WeightRegular, run is null ? Theme.TextMuted : color, TextAlignment.Left, W - x - 28);
    }

    private void PaintBox(SKCanvas c, float cy, string? mark)
    {
        var box = new SKRect(BoxX, MathF.Round(cy - BoxSize / 2f), BoxX + BoxSize, MathF.Round(cy - BoxSize / 2f) + BoxSize);
        SKColor fill = _locked ? Theme.TextDisabled : Theme.Accent;
        if (mark is not null)
        {
            Gfx.FillRound(c, box, Theme.RadiusSm, fill);
            Icons.Draw(c, mark, box.MidX, box.MidY, 12, Theme.TextOnAccent);
        }
        else
        {
            Gfx.FillRound(c, box, Theme.RadiusSm, Theme.Input);
            Gfx.StrokeRound(c, box, Theme.RadiusSm, _locked ? Theme.Border : Theme.BorderStrong, 1.5f);
        }
    }

    private static (string? Icon, SKColor Color) Look(HostRun? run) => run?.State switch
    {
        null => (null, Theme.TextMuted),
        HostRunState.Waiting => ("clock", Theme.TextMuted),
        HostRunState.Connecting or HostRunState.Running => ("refresh", Theme.Warning),
        HostRunState.Succeeded => ("check", Theme.Success),
        HostRunState.Cancelled => ("x", Theme.TextMuted),
        _ => ("alert", Theme.Danger),
    };

    // The second line of a host in a run: where it is, or how it ended.
    private static string Detail(HostRun run) => run.State switch
    {
        HostRunState.Waiting => "Waiting",
        HostRunState.Connecting => "Connecting…",
        HostRunState.Running => run.StartedAt is { } started ? $"Running · {CommandOutput.Duration(DateTimeOffset.UtcNow - started)}" : "Running…",
        HostRunState.Succeeded or HostRunState.Failed => $"{run.Outcome} · {CommandOutput.Duration(CommandOutput.Took(run))}",
        _ => run.Error ?? run.Outcome,
    };
}
