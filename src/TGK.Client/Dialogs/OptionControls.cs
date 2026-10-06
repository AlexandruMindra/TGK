using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Terminal;
using TGK.Core.Models;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Dialogs;

/// <summary>
/// Caption of an inheritable option: the name, and under it what applies while the field is empty
/// ("Inherited · 15 s from group Production") or, once the field overrides it, a "Reset to 15 s" link.
/// </summary>
public sealed class OptionCaption : Control
{
    public const float Height = 34;
    private const float HintY = 26;
    private readonly string _caption;
    private string _value = "";
    private string? _source;
    private bool _overridden, _linkHover;
    private float _linkRight;

    public OptionCaption(string caption)
    {
        _caption = caption;
        Events.OnMouseMove += (_, e) => SetAndPaint(ref _linkHover, OverLink(e.Relative.X, e.Relative.Y));
        Events.OnClick += (_, e) =>
        {
            if (!OverLink(e.Relative.X, e.Relative.Y))
                return;
            e.Handled = true;
            ResetClicked?.Invoke();
        };
    }

    /// <summary>The "Reset to …" link was clicked: the owner clears the field (and sets <see cref="Overridden"/> false).</summary>
    public event Action? ResetClicked;

    public bool Overridden
    {
        get => _overridden;
        set
        {
            if (SetAndPaint(ref _overridden, value) && !value)
                _linkHover = false;
        }
    }

    /// <summary>
    /// The value that applies when nothing is set here, and where it comes from (e.g. "from group Production"),
    /// or null for a built-in default.
    /// </summary>
    public void SetInherited(string value, string? source)
    {
        _value = value;
        _source = source;
        InvalidatePaint();
    }

    private bool OverLink(float x, float y) => _overridden && x <= _linkRight && y >= HintY - 9;

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _linkHover = false;
    }

    protected override void Paint(SKCanvas c)
    {
        Gfx.Text(c, _caption, 0, 8, Theme.FontSm, Theme.WeightSemibold, Theme.TextSecondary, TextAlignment.Left, W);
        if (_overridden)
        {
            string text = $"Reset to {_value}";
            float tw = Math.Min(W - 16, Gfx.Measure(text, Theme.FontXs));
            _linkRight = 16 + tw;
            SKColor color = _linkHover ? Theme.AccentHover : Theme.Accent;
            Icons.Draw(c, "refresh", 6, HintY, 12, color);
            Gfx.Text(c, text, 16, HintY, Theme.FontXs, Theme.WeightRegular, color, TextAlignment.Left, tw);
            if (_linkHover)
                Gfx.Line(c, 16, HintY + 7.5f, 16 + tw, HintY + 7.5f, color);
            return;
        }
        string hint = _source is null ? $"Default · {_value}" : $"Inherited · {_value} {_source}";
        Gfx.Text(c, hint, 0, HintY, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, W);
    }
}

/// <summary>
/// Environment variable rows (name, value, remove) and "Add variable". While nothing is set at this level it lists
/// the inherited variables instead; adding a variable starts from a copy of them (lists replace, never merge).
/// </summary>
public sealed class EnvEditor : VisualElement
{
    private const float RowGap = 8, NameW = 190, AddH = 30, InheritedH = 20;
    private readonly List<Row> _rows = [];
    private readonly Label _inherited;
    private readonly Button _add;
    private readonly Label _help;
    private IReadOnlyList<EnvVar> _inheritedVars = [];
    private bool _overridden;

    public EnvEditor(string help)
    {
        Style = new ElementStyle();
        _inherited = new Label("", Theme.FontSm, Theme.TextMuted) { Mono = true, Visible = false };
        _add = new Button("Add variable", ButtonVariant.Secondary, "plus");
        _add.Clicked += AddVariable;
        _help = new Label(help, Theme.FontXs, Theme.TextMuted) { MaxLines = 2 };
        AddChild(_inherited);
        AddChild(_add);
        AddChild(_help);
    }

    /// <summary>Rows were added or removed, or the list started or stopped overriding: relayout and update the caption.</summary>
    public event Action? Changed;

    public bool Overridden => _overridden;

    public void Load(IReadOnlyList<EnvVar>? own)
    {
        foreach (Row row in _rows.ToList())
            RemoveRow(row, notify: false);
        _overridden = own is not null;
        foreach (EnvVar variable in own ?? [])
            AddRow(variable);
    }

    public void SetInherited(IReadOnlyList<EnvVar> variables)
    {
        _inheritedVars = variables;
        _inherited.Text = string.Join("   ", variables.Select(v => $"{v.Name}={v.Value}"));
    }

    public void Reset()
    {
        Load(null);
        Changed?.Invoke();
    }

    /// <summary>The variables to store (null = inherit). Rows with neither name nor value are skipped.</summary>
    public List<EnvVar>? Collect(out string? error, out TextField? field)
    {
        error = null;
        field = null;
        if (!_overridden)
            return null;
        var list = new List<EnvVar>();
        foreach (Row row in _rows)
        {
            string name = row.Name.Text.Trim();
            if (name.Length == 0 && row.Value.Text.Length == 0)
                continue;
            if (!EnvVar.IsValidName(name))
            {
                error = name.Length == 0
                    ? "Enter a name for each environment variable."
                    : $"\"{name}\" is not a valid variable name (letters, digits and _, not starting with a digit).";
                field = row.Name;
                return null;
            }
            if (list.Exists(v => v.Name == name))
            {
                error = $"The variable {name} is set twice.";
                field = row.Name;
                return null;
            }
            EnvVar variable = row.Source.Clone(); // keeps members a newer client added
            variable.Name = name;
            variable.Value = row.Value.Text;
            list.Add(variable);
        }
        return list;
    }

    private bool ShowsInherited => !_overridden && _inheritedVars.Count > 0;

    public float MeasureHeight() => (ShowsInherited ? InheritedH + RowGap : 0) + _rows.Count * (Theme.FieldHeight + RowGap) + AddH;

    /// <summary>Positions the rows for <paramref name="width"/> (the element's frame must be set first).</summary>
    public void Arrange(float width)
    {
        float y = 0;
        _inherited.Visible = ShowsInherited;
        if (ShowsInherited)
        {
            _inherited.Transform.SetLocalFrame(0, 0, width, InheritedH);
            y += InheritedH + RowGap;
        }
        float valueW = width - NameW - 8 - Theme.FieldHeight - 8;
        foreach (Row row in _rows)
        {
            row.Name.Transform.SetLocalFrame(0, y, NameW, Theme.FieldHeight);
            row.Value.Transform.SetLocalFrame(NameW + 8, y, valueW, Theme.FieldHeight);
            row.Remove.Transform.SetLocalFrame(width - Theme.FieldHeight, y, Theme.FieldHeight, Theme.FieldHeight);
            y += Theme.FieldHeight + RowGap;
        }
        float addW = _add.PreferredWidth;
        _add.Transform.SetLocalFrame(0, y, addW, AddH);
        _help.Transform.SetLocalFrame(addW + 14, y, Math.Max(0, width - addW - 14), AddH);
    }

    private void AddVariable()
    {
        if (!_overridden)
        {
            _overridden = true;
            foreach (EnvVar variable in _inheritedVars)
                AddRow(variable);
        }
        Row row = AddRow(new EnvVar());
        Changed?.Invoke();
        row.Name.Focus();
    }

    private Row AddRow(EnvVar variable)
    {
        var row = new Row(
            new TextField("NAME") { Text = variable.Name, Mono = true, MaxLength = 128 },
            new TextField("value") { Text = variable.Value, Mono = true, MaxLength = 4096 },
            new IconButton("x"),
            variable);
        row.Name.Changed += _ => row.Name.HasError = false;
        row.Remove.Clicked += () => RemoveRow(row, notify: true);
        _rows.Add(row);
        AddChild(row.Name);
        AddChild(row.Value);
        AddChild(row.Remove);
        return row;
    }

    private void RemoveRow(Row row, bool notify)
    {
        _rows.Remove(row);
        foreach (VisualElement element in new VisualElement[] { row.Name, row.Value, row.Remove })
        {
            RemoveChild(element);
            element.Dispose();
        }
        if (notify)
            Changed?.Invoke();
    }

    private sealed record Row(TextField Name, TextField Value, IconButton Remove, EnvVar Source);
}

/// <summary>
/// A list of text rules (one field per row, remove buttons, "Add …"), e.g. the commands agents may run. While nothing
/// is set at this level it shows the inherited rules; adding one starts from a copy of them (lists replace, never merge).
/// </summary>
public sealed class RuleListEditor : VisualElement
{
    private const float RowGap = 8, AddH = 30, InheritedH = 20;
    private readonly List<(TextField Field, IconButton Remove)> _rows = [];
    private readonly Label _inherited;
    private readonly Button _add;
    private readonly Label _help;
    private readonly string _placeholder;
    private IReadOnlyList<string> _inheritedRules = [];
    private bool _overridden;

    public RuleListEditor(string addText, string placeholder, string help)
    {
        Style = new ElementStyle();
        _placeholder = placeholder;
        _inherited = new Label("", Theme.FontSm, Theme.TextMuted) { Mono = true, Visible = false, MaxLines = 3 };
        _add = new Button(addText, ButtonVariant.Secondary, "plus");
        _add.Clicked += AddRule;
        _help = new Label(help, Theme.FontXs, Theme.TextMuted) { MaxLines = 3 };
        AddChild(_inherited);
        AddChild(_add);
        AddChild(_help);
    }

    /// <summary>Rows were added or removed, or the list started or stopped overriding.</summary>
    public event Action? Changed;

    public bool Overridden => _overridden;

    public void Load(IReadOnlyList<string>? own)
    {
        foreach (var row in _rows.ToList())
            RemoveRow(row, notify: false);
        _overridden = own is not null;
        foreach (string rule in own ?? [])
            AddRow(rule);
    }

    public void SetInherited(IReadOnlyList<string> rules)
    {
        _inheritedRules = rules;
        _inherited.Text = string.Join("   ", rules);
    }

    public void Reset()
    {
        Load(null);
        Changed?.Invoke();
    }

    /// <summary>The rules to store (null = inherit); empty rows are skipped, and no rules at all inherit again.</summary>
    public List<string>? Collect()
    {
        if (!_overridden)
            return null;
        List<string> rules = _rows.Select(r => r.Field.Text.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return rules.Count > 0 ? rules : null;
    }

    private bool ShowsInherited => !_overridden && _inheritedRules.Count > 0;

    public float MeasureHeight(float width) =>
        (ShowsInherited ? Math.Max(InheritedH, _inherited.MeasureHeight(width)) + RowGap : 0) + _rows.Count * (Theme.FieldHeight + RowGap) + AddH;

    public void Arrange(float width)
    {
        float y = 0;
        _inherited.Visible = ShowsInherited;
        if (ShowsInherited)
        {
            float h = Math.Max(InheritedH, _inherited.MeasureHeight(width));
            _inherited.Transform.SetLocalFrame(0, 0, width, h);
            y += h + RowGap;
        }
        foreach (var (field, remove) in _rows)
        {
            field.Transform.SetLocalFrame(0, y, width - Theme.FieldHeight - 8, Theme.FieldHeight);
            remove.Transform.SetLocalFrame(width - Theme.FieldHeight, y, Theme.FieldHeight, Theme.FieldHeight);
            y += Theme.FieldHeight + RowGap;
        }
        float addW = _add.PreferredWidth;
        _add.Transform.SetLocalFrame(0, y, addW, AddH);
        _help.Transform.SetLocalFrame(addW + 14, y - 2, Math.Max(0, width - addW - 14), AddH + 6);
    }

    private void AddRule()
    {
        if (!_overridden)
        {
            _overridden = true;
            foreach (string rule in _inheritedRules)
                AddRow(rule);
        }
        var row = AddRow("");
        Changed?.Invoke();
        row.Field.Focus();
    }

    private (TextField Field, IconButton Remove) AddRow(string rule)
    {
        var row = (Field: new TextField(_placeholder) { Text = rule, Mono = true, MaxLength = HostOptions.MaxAgentRuleLength }, Remove: new IconButton("x"));
        row.Remove.Clicked += () => RemoveRow(row, notify: true);
        _rows.Add(row);
        AddChild(row.Field);
        AddChild(row.Remove);
        return row;
    }

    private void RemoveRow((TextField Field, IconButton Remove) row, bool notify)
    {
        _rows.Remove(row);
        foreach (VisualElement element in new VisualElement[] { row.Field, row.Remove })
        {
            RemoveChild(element);
            element.Dispose();
        }
        if (_rows.Count == 0)
            _overridden = false; // removing the last rule inherits again (it never silently drops inherited rules)
        if (notify)
            Changed?.Invoke();
    }
}

/// <summary>[−] size [+]: an empty field inherits (its placeholder shows the inherited size).</summary>
public sealed class FontStepper : VisualElement
{
    private readonly Button _minus = new("", ButtonVariant.Secondary, "minus");
    private readonly Button _plus = new("", ButtonVariant.Secondary, "plus");
    private float _inherited = 14;

    public FontStepper()
    {
        Style = new ElementStyle();
        Field = new TextField { DigitsOnly = true, MaxLength = 2 };
        Field.Changed += _ => Changed?.Invoke();
        _minus.Clicked += () => Step(-1);
        _plus.Clicked += () => Step(1);
        AddChild(_minus);
        AddChild(Field);
        AddChild(_plus);
    }

    /// <summary>The user changed the size (typed or stepped).</summary>
    public event Action? Changed;

    public TextField Field { get; }

    /// <summary>The size set here, or null to inherit.</summary>
    public float? Value
    {
        get => int.TryParse(Field.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int size) ? size : null;
        set => Field.Text = value is { } size ? MathF.Round(size).ToString(CultureInfo.InvariantCulture) : "";
    }

    public void SetInherited(float size)
    {
        _inherited = size;
        Field.Placeholder = MathF.Round(size).ToString(CultureInfo.InvariantCulture);
    }

    private void Step(int delta)
    {
        float current = Value ?? MathF.Round(_inherited);
        Value = Math.Clamp(current + delta, HostOptions.MinFontSize, HostOptions.MaxFontSize);
        Changed?.Invoke();
    }

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        _minus.Transform.SetLocalFrame(0, 0, h, h);
        Field.Transform.SetLocalFrame(h + 6, 0, Math.Max(0, w - 2 * h - 12), h);
        _plus.Transform.SetLocalFrame(w - h, 0, h, h);
    }
}

/// <summary>Color scheme cards with a live swatch (background, text, six colors); the first card inherits.</summary>
public sealed class SchemePicker : Control
{
    private const int Columns = 4;
    private const float CardH = 50, Gap = 8;
    private readonly string _inheritLabel;
    private string? _selected;
    private ColorScheme _inherited = ColorScheme.TgkDark;
    private int _hover = -1;

    /// <param name="inheritLabel">Caption of the first card, e.g. "Inherit" (or "Default" for the vault defaults).</param>
    public SchemePicker(string inheritLabel)
    {
        _inheritLabel = inheritLabel;
        Cursor = StandardCursor.Hand;
        Events.OnMouseMove += (_, e) => SetAndPaint(ref _hover, IndexAt(e.Relative.X, e.Relative.Y));
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            int i = IndexAt(e.Relative.X, e.Relative.Y);
            if (i < 0)
                return;
            Selected = i == 0 ? null : SchemeAt(i).Name;
            Changed?.Invoke();
        };
    }

    public event Action? Changed;

    /// <summary>The chosen scheme's name, or null to inherit.</summary>
    public string? Selected { get => _selected; set => SetAndPaint(ref _selected, value); }

    public void SetInherited(ColorScheme scheme)
    {
        _inherited = scheme;
        InvalidatePaint();
    }

    // The first card is the inherited scheme (or the default); the others are the remaining schemes, so none is twice.
    private static int Count => ColorScheme.All.Count;

    private ColorScheme SchemeAt(int i)
    {
        if (i == 0)
            return _inherited;
        int n = 0;
        foreach (ColorScheme scheme in ColorScheme.All)
        {
            if (scheme != _inherited && ++n == i)
                return scheme;
        }
        return _inherited;
    }

    public static float MeasureHeight()
    {
        int rows = (Count + Columns - 1) / Columns;
        return rows * CardH + (rows - 1) * Gap;
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _hover = -1;
    }

    private SKRect CardRect(int i)
    {
        float cw = (W - (Columns - 1) * Gap) / Columns;
        float x = MathF.Round(i % Columns * (cw + Gap)), y = i / Columns * (CardH + Gap);
        return new SKRect(x, y, MathF.Round(x + cw), y + CardH);
    }

    private int IndexAt(float x, float y)
    {
        for (int i = 0; i < Count; i++)
        {
            if (CardRect(i).Contains(x, y))
                return i;
        }
        return -1;
    }

    // The inherited scheme chosen explicitly (as older versions allowed) shows on the first card.
    // Compared through Find, which also knows schemes' earlier names.
    private bool IsSelected(int i) =>
        i == 0 ? _selected is null || ColorScheme.Find(_selected) == _inherited
            : _selected is not null && ColorScheme.Find(_selected) == SchemeAt(i);

    protected override void Paint(SKCanvas c)
    {
        for (int i = 0; i < Count; i++)
        {
            ColorScheme scheme = SchemeAt(i);
            SKRect r = CardRect(i);
            bool selected = IsSelected(i);
            Gfx.FillRound(c, r, Theme.Radius, scheme.Background);
            Gfx.StrokeRound(c, r, Theme.Radius, selected ? Theme.Accent : i == _hover ? Theme.TextMuted : Theme.BorderStrong, selected ? 2 : 1);
            string title = i == 0 ? $"{_inheritLabel} · {scheme.Name}" : scheme.Name;
            // A scheme with its own font shows its name in it.
            SKFont titleFont = scheme.Font is { } family ? Gfx.Font(Theme.FontXs + 5, TerminalFonts.Get(family).Regular) : Gfx.Font(Theme.FontXs, Theme.WeightSemibold);
            Gfx.Text(c, title, r.Left + 10, r.Top + 15, titleFont, scheme.Foreground, TextAlignment.Left, r.Width - (selected ? 34 : 18));
            for (int k = 0; k < 6; k++)
                Gfx.Circle(c, r.Left + 14 + k * 13, r.Bottom - 14, 4, scheme.Ansi[k + 1]);
            if (selected)
            {
                Gfx.Circle(c, r.Right - 14, r.Top + 14, 8, Theme.Accent);
                Icons.Draw(c, "check", r.Right - 14, r.Top + 14, 12, Theme.TextOnAccent);
            }
        }
    }
}
