using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Core.Models;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Dialogs;

/// <summary>
/// The host editor's Tunnels page: the host's port forwardings (enable, edit, delete) and a form that adds or updates
/// one, with validation and a warning when two tunnels listen on the same port or one is open to the network.
/// </summary>
public sealed class TunnelsEditor
{
    private const float Gap = 16, PortW = 96, RowGap = 14;
    private static readonly string[] KindHelp =
    [
        "Local (-L): listens on this computer; connections go to the destination as seen from the server.",
        "Remote (-R): listens on the server; connections come back to the destination as seen from this computer.",
        "Dynamic (-D): a SOCKS5 proxy on this computer; connections leave from the server.",
    ];

    private readonly FormPage _page;
    private readonly List<PortForward> _tunnels;
    private readonly Label _intro;
    private readonly TunnelList _list;
    private readonly Label _formTitle;
    private readonly Label _kindCaption, _descriptionCaption, _bindCaption, _bindPortCaption, _destCaption, _destPortCaption;
    private readonly SegmentedControl _kind;
    private readonly TextField _description, _bind, _bindPort, _dest, _destPort;
    private readonly Label _kindHelp, _message;
    private readonly Button _save, _cancel;
    private PortForward? _editing;

    public TunnelsEditor(FormPage page, IEnumerable<PortForward> tunnels)
    {
        _page = page;
        _tunnels = tunnels.Select(t => t.Clone()).ToList();
        _intro = page.Add(new Label("Tunnels start with every session to this host and stop when it ends. They are not inherited.",
            Theme.FontSm, Theme.TextMuted));
        _list = page.Add(new TunnelList(this));
        _formTitle = page.Add(new Label("New tunnel", Theme.FontBase, Theme.TextPrimary, Theme.WeightSemibold));
        _kindCaption = page.Add(Form.Caption("Type"));
        _kind = page.Add(new SegmentedControl("Local", "Remote", "Dynamic"));
        _kind.SelectionChanged += _ => OnKindChanged();
        _descriptionCaption = page.Add(Form.Caption("Description"));
        _description = page.Add(new TextField("Optional, e.g. PostgreSQL") { MaxLength = 200 });
        _bindCaption = page.Add(Form.Caption("Listen on"));
        _bind = page.Add(new TextField("127.0.0.1") { Mono = true, MaxLength = 255 });
        _bindPortCaption = page.Add(Form.Caption("Port"));
        _bindPort = page.Add(new TextField("8080") { Mono = true, DigitsOnly = true, MaxLength = 5 });
        _destCaption = page.Add(Form.Caption("Destination"));
        _dest = page.Add(new TextField("host as seen from the server") { Mono = true, MaxLength = 255 });
        _destPortCaption = page.Add(Form.Caption("Port"));
        _destPort = page.Add(new TextField("5432") { Mono = true, DigitsOnly = true, MaxLength = 5 });
        _kindHelp = page.Add(new Label("", Theme.FontXs, Theme.TextMuted) { MaxLines = 2 });
        _save = page.Add(new Button("Add tunnel", ButtonVariant.Secondary, "plus"));
        _save.Clicked += () => Commit();
        _cancel = page.Add(new Button("Cancel", ButtonVariant.Ghost) { Visible = false });
        _cancel.Clicked += () => Edit(null);
        _message = page.Add(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 4, Visible = false });
        foreach (TextField field in new[] { _bind, _bindPort, _dest, _destPort })
            field.Changed += _ => { field.HasError = false; HideMessage(); };
        page.Layout = Arrange;
        OnKindChanged();
    }

    /// <summary>The page's height changed (a tunnel was added or removed): the owner re-lays out.</summary>
    public event Action? LayoutChanged;

    internal IReadOnlyList<PortForward> Tunnels => _tunnels;

    internal PortForward? Editing => _editing;

    /// <summary>
    /// The tunnels to save. A form the user filled in but did not add yet is added first; when it is invalid the
    /// page shows why and the result is null.
    /// </summary>
    public List<PortForward>? Collect() =>
        (_editing is not null || _bindPort.Text.Length > 0) && !Commit() ? null : _tunnels.Select(t => t.Clone()).ToList();

    /// <summary>A second enabled tunnel listens on the same port on the same side (local or server).</summary>
    internal bool IsDuplicate(PortForward tunnel) =>
        tunnel.Enabled && _tunnels.Exists(t => t != tunnel && t.Enabled && t.BindPort == tunnel.BindPort
            && (t.Kind == ForwardKind.Remote) == (tunnel.Kind == ForwardKind.Remote));

    internal void Toggle(PortForward tunnel)
    {
        tunnel.Enabled = !tunnel.Enabled;
        _list.InvalidatePaint();
    }

    internal void Remove(PortForward tunnel)
    {
        _tunnels.Remove(tunnel);
        if (_editing == tunnel)
            Edit(null);
        HideMessage();
        LayoutChanged?.Invoke();
    }

    /// <summary>Loads <paramref name="tunnel"/> into the form (null: an empty form for a new one).</summary>
    internal void Edit(PortForward? tunnel)
    {
        _editing = tunnel;
        _kind.SelectedIndex = (int)(tunnel?.Kind ?? ForwardKind.Local);
        _description.Text = tunnel?.Description ?? "";
        _bind.Text = tunnel is null || tunnel.BindAddress == "127.0.0.1" ? "" : tunnel.BindAddress;
        _bindPort.Text = tunnel is null ? "" : tunnel.BindPort.ToString(CultureInfo.InvariantCulture);
        _dest.Text = tunnel?.DestinationHost ?? "";
        _destPort.Text = tunnel?.DestinationPort?.ToString(CultureInfo.InvariantCulture) ?? "";
        foreach (TextField field in new[] { _bind, _bindPort, _dest, _destPort })
            field.HasError = false;
        _formTitle.Text = tunnel is null ? "New tunnel" : "Edit tunnel";
        _save.Text = tunnel is null ? "Add tunnel" : "Update tunnel";
        _save.Icon = tunnel is null ? "plus" : "check";
        _cancel.Visible = tunnel is not null;
        HideMessage();
        OnKindChanged();
        _list.InvalidatePaint();
    }

    /// <summary>Adds (or updates) the tunnel in the form; returns false and shows why when it is invalid.</summary>
    private bool Commit()
    {
        var kind = (ForwardKind)_kind.SelectedIndex;
        if (!int.TryParse(_bindPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int bindPort) || bindPort is < 1 or > 65535)
            return Fail("The listening port must be a number between 1 and 65535.", _bindPort);
        int? destPort = null;
        if (kind != ForwardKind.Dynamic)
        {
            if (_dest.Text.Trim().Length == 0)
                return Fail(kind == ForwardKind.Local ? "Enter the destination host (as seen from the server)." : "Enter the destination host (as seen from this computer).", _dest);
            if (!int.TryParse(_destPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
                return Fail("The destination port must be a number between 1 and 65535.", _destPort);
            destPort = port;
        }
        PortForward tunnel = _editing?.Clone() ?? new PortForward(); // an edit keeps its id, state and unknown members
        tunnel.Kind = kind;
        tunnel.BindAddress = _bind.Text.Trim() is { Length: > 0 } bind ? bind : "127.0.0.1";
        tunnel.BindPort = bindPort;
        tunnel.DestinationHost = kind == ForwardKind.Dynamic ? null : _dest.Text.Trim();
        tunnel.DestinationPort = destPort;
        tunnel.Description = _description.Text.Trim() is { Length: > 0 } description ? description : null;
        if (tunnel.Validate() is { } problem)
            return Fail(problem, tunnel.BindAddress.Any(char.IsWhiteSpace) ? _bind : _dest);

        int index = _editing is null ? -1 : _tunnels.IndexOf(_editing);
        if (index >= 0)
            _tunnels[index] = tunnel;
        else
            _tunnels.Add(tunnel);
        Edit(null);
        var warnings = new List<string>();
        if (IsDuplicate(tunnel))
            warnings.Add($"Another enabled tunnel also listens on port {bindPort}: only one of them can start.");
        if (tunnel.IsOpenToNetwork)
        {
            warnings.Add($"Listening on {tunnel.BindAddress} lets other computers on your network use this tunnel" + (kind == ForwardKind.Dynamic
                ? ": it is an open proxy into the server's network." : $" to reach {tunnel.DestinationHost}:{destPort}.")
                + " Use 127.0.0.1 unless that is intended.");
        }
        if (warnings.Count > 0)
            ShowMessage(string.Join(" ", warnings), Theme.Warning);
        LayoutChanged?.Invoke();
        return true;
    }

    private bool Fail(string message, TextField field)
    {
        field.HasError = true;
        field.Focus();
        ShowMessage(message, Theme.Danger);
        return false;
    }

    private void ShowMessage(string text, SKColor color)
    {
        _message.Text = text;
        _message.Color = color;
        _message.Visible = true;
        _page.Reveal(_message);
        LayoutChanged?.Invoke();
    }

    private void HideMessage()
    {
        if (!_message.Visible)
            return;
        _message.Visible = false;
        LayoutChanged?.Invoke();
    }

    private void OnKindChanged()
    {
        int kind = _kind.SelectedIndex;
        bool dynamic = kind == (int)ForwardKind.Dynamic;
        _dest.Enabled = _destPort.Enabled = !dynamic;
        _destCaption.Color = _destPortCaption.Color = dynamic ? Theme.TextDisabled : Theme.TextSecondary;
        _dest.Placeholder = dynamic ? "Chosen by each SOCKS client" : kind == (int)ForwardKind.Local ? "host as seen from the server" : "host as seen from this computer";
        _destPort.Placeholder = dynamic ? "" : "5432";
        _bindCaption.Text = kind == (int)ForwardKind.Remote ? "Listen on (server address)" : "Listen on";
        _kindHelp.Text = KindHelp[kind];
    }

    private float Arrange(float w)
    {
        float y = 0;
        _intro.Transform.SetLocalFrame(0, y, w, 18);
        y += 18 + 10;
        float listH = _list.MeasureHeight();
        _list.Transform.SetLocalFrame(0, y, w, listH);
        y += listH + 20;

        _formTitle.Transform.SetLocalFrame(0, y, w, 20);
        y += 20 + 10;
        float col = MathF.Floor((w - Gap) / 2f);
        Form.Place(_kindCaption, _kind, 0, y, col);
        y = Form.Place(_descriptionCaption, _description, col + Gap, y, w - col - Gap);
        y = Help(_kindHelp, y, w) + RowGap;

        float bindW = col - Gap / 2f - PortW, destLeft = col + Gap;
        Form.Place(_bindCaption, _bind, 0, y, bindW);
        Form.Place(_bindPortCaption, _bindPort, bindW + 8, y, PortW);
        Form.Place(_destCaption, _dest, destLeft, y, w - destLeft - PortW - 8);
        y = Form.Place(_destPortCaption, _destPort, w - PortW, y, PortW) + RowGap;

        float saveW = _save.PreferredWidth;
        _save.Transform.SetLocalFrame(0, y, saveW, Theme.ControlHeight);
        _cancel.Transform.SetLocalFrame(saveW + 8, y, _cancel.PreferredWidth, Theme.ControlHeight);
        y += Theme.ControlHeight;
        if (_message.Visible)
        {
            float mh = _message.MeasureHeight(w);
            _message.Transform.SetLocalFrame(0, y + 10, w, mh);
            y += 10 + mh;
        }
        return y;
    }

    private static float Help(Label label, float y, float w)
    {
        float h = Math.Max(16, label.MeasureHeight(w) - 3);
        label.Transform.SetLocalFrame(0, y + 6, w, h);
        return y + 6 + h;
    }

    /// <summary>The tunnel rows: enabled box, kind, addresses, description, and a delete button; a click edits the row.</summary>
    private sealed class TunnelList : Control
    {
        private const float RowH = 36, EmptyH = 44, BoxX = 12, TrashW = 32;
        private readonly TunnelsEditor _owner;
        private int _hover = -1;
        private bool _hoverTrash;

        public TunnelList(TunnelsEditor owner)
        {
            _owner = owner;
            Cursor = StandardCursor.Hand;
            Events.OnMouseMove += (_, e) =>
            {
                int row = RowAt(e.Relative.Y);
                bool trash = row >= 0 && e.Relative.X >= W - TrashW - 4;
                if (row != _hover || trash != _hoverTrash)
                {
                    _hover = row;
                    _hoverTrash = trash;
                    InvalidatePaint();
                }
            };
            Events.OnClick += OnClick;
        }

        public float MeasureHeight() => _owner.Tunnels.Count == 0 ? EmptyH : _owner.Tunnels.Count * RowH;

        protected override void OnHoverChanged()
        {
            if (!IsHovered)
                _hover = -1;
        }

        private int RowAt(float y) => y < 0 || _owner.Tunnels.Count == 0 ? -1 : Math.Min((int)(y / RowH), _owner.Tunnels.Count - 1);

        private void OnClick(object? sender, MouseEventArgs e)
        {
            e.Handled = true;
            int row = RowAt(e.Relative.Y);
            if (row < 0)
                return;
            PortForward tunnel = _owner.Tunnels[row];
            if (e.Relative.X >= W - TrashW - 4)
            {
                _hover = -1;
                _owner.Remove(tunnel);
            }
            else if (e.Relative.X < BoxX + 24)
                _owner.Toggle(tunnel);
            else
                _owner.Edit(tunnel);
        }

        protected override void Paint(SKCanvas c)
        {
            var frame = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, frame, Theme.Radius, Theme.Input);
            Gfx.StrokeRound(c, frame, Theme.Radius, Theme.BorderInput);
            IReadOnlyList<PortForward> tunnels = _owner.Tunnels;
            if (tunnels.Count == 0)
            {
                Gfx.Text(c, "No tunnels yet. Add one below.", W / 2f, H / 2f, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center);
                return;
            }
            SKFont mono = Gfx.Font(Theme.FontSm, Theme.Mono);
            for (int i = 0; i < tunnels.Count; i++)
            {
                PortForward t = tunnels[i];
                float top = i * RowH, cy = top + RowH / 2f;
                if (i > 0)
                    Gfx.Line(c, 1, top + 0.5f, W - 1, top + 0.5f, Theme.Border);
                if (t == _owner.Editing)
                    Gfx.FillRect(c, new SKRect(1, top + 1, W - 1, top + RowH - 1), Theme.AccentSoft);
                else if (i == _hover)
                    Gfx.FillRect(c, new SKRect(1, top + 1, W - 1, top + RowH - 1), Theme.SurfaceRaised);

                var box = new SKRect(BoxX, cy - 8, BoxX + 16, cy + 8);
                if (t.Enabled)
                {
                    Gfx.FillRound(c, box, Theme.RadiusSm, Theme.Accent);
                    Icons.Draw(c, "check", box.MidX, box.MidY, 14, Theme.TextOnAccent);
                }
                else
                {
                    Gfx.StrokeRound(c, box, Theme.RadiusSm, Theme.BorderStrong, 1.5f);
                }

                SKColor kindColor = t.Kind switch { ForwardKind.Local => Theme.Accent, ForwardKind.Remote => new SKColor(0xA3, 0x71, 0xF7), _ => Theme.Success };
                var badge = new SKRect(BoxX + 26, cy - 10, BoxX + 48, cy + 10);
                Gfx.FillRound(c, badge, Theme.RadiusSm, kindColor.WithAlpha(t.Enabled ? (byte)46 : (byte)22));
                Gfx.Text(c, t.Kind.ToString()[..1], badge.MidX, cy, Theme.FontXs, Theme.WeightSemibold, t.Enabled ? kindColor : Theme.TextMuted, TextAlignment.Center);

                float x = badge.Right + 10, right = W - TrashW - 10;
                if (_owner.IsDuplicate(t) || t.IsOpenToNetwork)
                {
                    Icons.Draw(c, "alert", right - 8, cy, 14, Theme.Warning);
                    right -= 22;
                }
                string route = t.Kind == ForwardKind.Dynamic
                    ? $"{t.BindAddress}:{t.BindPort}  (SOCKS)"
                    : $"{t.BindAddress}:{t.BindPort} → {t.DestinationHost}:{t.DestinationPort}";
                float routeW = Math.Min(Gfx.Measure(route, mono), right - x);
                Gfx.Text(c, route, x, cy, mono, t.Enabled ? Theme.TextPrimary : Theme.TextMuted, TextAlignment.Left, routeW);
                if (t.Description is { Length: > 0 } description && right - x - routeW > 40)
                    Gfx.Text(c, description, x + routeW + 14, cy, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, right - x - routeW - 14);

                if (i == _hover)
                {
                    float tx = W - TrashW / 2f - 6;
                    if (_hoverTrash)
                        Gfx.FillRound(c, new SKRect(tx - 12, cy - 12, tx + 12, cy + 12), Theme.RadiusSm, Theme.DangerSoft);
                    Icons.Draw(c, "trash", tx, cy, 15, _hoverTrash ? Theme.Danger : Theme.TextMuted);
                }
            }
        }
    }
}
