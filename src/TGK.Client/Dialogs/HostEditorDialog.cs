using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>Create or edit a saved host. Saves (and deletes) through the vault.</summary>
public sealed class HostEditorDialog : DialogBase
{
    private const float PortW = 100, Gap = 16;
    private readonly IVaultService _vault;
    private readonly HostEntry _entry;
    private readonly bool _isNew;
    private readonly List<Identity> _identities;
    private readonly Label _nameCaption, _hostCaption, _portCaption, _userCaption, _identityCaption, _groupCaption, _colorCaption, _notesCaption;
    private readonly TextField _name, _host, _port, _user, _group, _notes;
    private readonly Dropdown _identity;
    private readonly ColorSwatches _color;
    private readonly Label _error;

    /// <param name="host">The host to edit, or null for a new one.</param>
    /// <param name="template">Prefill for a new host (e.g. when duplicating).</param>
    public HostEditorDialog(TgkView view, HostEntry? host, HostEntry? template = null)
        : base(view, host is null ? "New host" : "Edit host", 600)
    {
        _vault = view.Services.Vault;
        _isNew = host is null;
        _entry = host?.Clone() ?? template?.Clone() ?? new HostEntry();
        if (_isNew)
        {
            _entry.Id = Guid.NewGuid();
            _entry.LastConnected = null;
        }
        VaultData vault = _vault.Current;
        _identities = vault.Identities.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Subtitle = _isNew ? "Saved hosts sync to all your devices." : _entry.DisplayName;

        _nameCaption = AddBody(Form.Caption("Name"));
        _name = AddBody(new TextField("e.g. web-01") { Text = _entry.Name });
        _hostCaption = AddBody(Form.Caption("Host"));
        _host = AddBody(new TextField("hostname or IP address") { Text = _entry.Host, Mono = true });
        _portCaption = AddBody(Form.Caption("Port"));
        _port = AddBody(new TextField("22") { Text = _entry.Port.ToString(CultureInfo.InvariantCulture), Mono = true, MaxLength = 5 });
        _userCaption = AddBody(Form.Caption("Username"));
        _user = AddBody(new TextField("from identity") { Text = _entry.Username ?? "" });
        _identityCaption = AddBody(Form.Caption("Identity"));
        _identity = AddBody(new Dropdown
        {
            Options = _identities.Select(i => i.Name).Prepend("None (ask for password)").ToList(),
        });
        _identity.SelectedIndex = _identities.FindIndex(i => i.Id == _entry.IdentityId) + 1;
        _groupCaption = AddBody(Form.Caption("Group"));
        _group = AddBody(new TextField("No group") { Text = vault.FindGroup(_entry.GroupId)?.Name ?? "", TrailingIcon = "chevron-down" });
        _group.TrailingClicked += ShowGroupMenu;
        _colorCaption = AddBody(Form.Caption("Tag color"));
        _color = AddBody(new ColorSwatches { Selected = _entry.TagColor });
        _notesCaption = AddBody(Form.Caption("Notes"));
        _notes = AddBody(new TextField("Optional") { Text = _entry.Notes ?? "" });
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { Visible = false });

        _host.Changed += _ => ClearError(_host);
        _port.Changed += _ => ClearError(_port);

        if (!_isNew)
            AddLeftButton("Delete", ButtonVariant.Ghost, Delete).Icon = "trash";
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton(_isNew ? "Add host" : "Save", ButtonVariant.Primary, Accept);
    }

    protected override Blossom.Core.Visual.VisualElement? InitialFocus => _isNew && _entry.Host.Length == 0 ? _name : _host;

    protected override float LayoutBody(float left, float top, float width)
    {
        float col = (width - Gap) / 2f;
        float y = top;
        y = Form.Place(_nameCaption, _name, left, y, width) + Form.RowGap;
        Form.Place(_hostCaption, _host, left, y, width - PortW - Gap);
        y = Form.Place(_portCaption, _port, left + width - PortW, y, PortW) + Form.RowGap;
        Form.Place(_userCaption, _user, left, y, col);
        y = Form.Place(_identityCaption, _identity, left + col + Gap, y, col) + Form.RowGap;
        Form.Place(_groupCaption, _group, left, y, col);
        y = Form.Place(_colorCaption, _color, left + col + Gap, y, col) + Form.RowGap;
        y = Form.Place(_notesCaption, _notes, left, y, width);
        if (_error.Visible)
        {
            _error.Transform.SetLocalFrame(left, y + 10, width, 18);
            y += 28;
        }
        return y - top;
    }

    private void ShowGroupMenu()
    {
        var items = new List<MenuItem> { new() { Text = "No group", IsChecked = _group.Text.Trim().Length == 0, Action = () => _group.Text = "" } };
        items.AddRange(_vault.Current.Groups.OrderBy(g => g.SortOrder).Select(g => new MenuItem
        {
            Text = g.Name,
            IsChecked = string.Equals(g.Name, _group.Text.Trim(), StringComparison.OrdinalIgnoreCase),
            Action = () => _group.Text = g.Name,
        }));
        items.Add(MenuItem.Separator);
        items.Add(new MenuItem { Text = "Type a name to create a new group", IsHeader = true });
        var at = _group.Transform.Computed;
        View.Menu.Show(items, at.X, at.Y + at.Height + 4, at.Width, at.Height);
    }

    private void ShowError(string message, TextField? field)
    {
        _error.Text = message;
        _error.Visible = true;
        if (field is not null)
        {
            field.HasError = true;
            field.Focus();
        }
        InvalidateLayout();
    }

    private void ClearError(TextField field)
    {
        field.HasError = false;
        if (_error.Visible)
        {
            _error.Visible = false;
            InvalidateLayout();
        }
    }

    protected override void Accept()
    {
        string host = _host.Text.Trim();
        if (host.Length == 0 || host.AsSpan().IndexOfAny(" \t") >= 0)
        {
            ShowError(host.Length == 0 ? "Enter the host name or IP address." : "The host name must not contain spaces.", _host);
            return;
        }
        if (!int.TryParse(_port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            ShowError("The port must be a number between 1 and 65535.", _port);
            return;
        }

        _entry.Name = _name.Text.Trim();
        _entry.Host = host;
        _entry.Port = port;
        _entry.Username = _user.Text.Trim() is { Length: > 0 } user ? user : null;
        _entry.IdentityId = _identity.SelectedIndex > 0 ? _identities[_identity.SelectedIndex - 1].Id : null;
        _entry.TagColor = _color.Selected;
        _entry.Notes = _notes.Text.Trim() is { Length: > 0 } notes ? notes : null;

        string groupName = _group.Text.Trim();
        HostGroup? group = groupName.Length == 0 ? null
            : _vault.Current.Groups.Find(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (groupName.Length > 0 && group is null)
        {
            group = new HostGroup { Name = groupName, SortOrder = _vault.Current.Groups.Select(g => g.SortOrder).DefaultIfEmpty(-1).Max() + 1 };
            HostGroup newGroup = group;
            View.RunVault(() => _vault.SaveGroupAsync(newGroup));
        }
        _entry.GroupId = group?.Id;

        if (View.RunVault(() => _vault.SaveHostAsync(_entry)))
            Close();
    }

    private async void Delete()
    {
        bool confirmed = await ConfirmDialog.ShowAsync(View, "Delete host?",
            $"“{_entry.DisplayName}” will be removed from your vault on all devices. Open sessions stay connected.",
            "Delete", danger: true);
        if (confirmed && View.RunVault(() => _vault.DeleteHostAsync(_entry.Id)))
            Close();
    }

    /// <summary>Tag color picker: "none" plus <see cref="Theme.TagColors"/>.</summary>
    private sealed class ColorSwatches : Control
    {
        private const float Dot = 22, Spacing = 7;
        private string? _selected;
        private int _hover = -1;

        public ColorSwatches()
        {
            Cursor = StandardCursor.Hand;
            Events.OnMouseMove += (_, e) => SetAndPaint(ref _hover, IndexAt(e.Relative.X, e.Relative.Y));
            Events.OnClick += (_, e) =>
            {
                e.Handled = true;
                int i = IndexAt(e.Relative.X, e.Relative.Y);
                if (i >= 0)
                    Selected = i == 0 ? null : Theme.TagColors[i - 1];
            };
        }

        public string? Selected
        {
            get => _selected;
            set => SetAndPaint(ref _selected, value);
        }

        protected override void OnHoverChanged()
        {
            if (!IsHovered)
                _hover = -1;
        }

        private float Step => Math.Min(Dot + Spacing, W / (Theme.TagColors.Length + 1));

        private int IndexAt(float x, float y)
        {
            int i = (int)(x / Step);
            return i >= 0 && i <= Theme.TagColors.Length && y >= 0 && y <= H ? i : -1;
        }

        protected override void Paint(SKCanvas c)
        {
            float cy = H / 2f, r = Math.Min(Dot, Step - 4) / 2f;
            for (int i = 0; i <= Theme.TagColors.Length; i++)
            {
                float cx = i * Step + r + 2;
                string? color = i == 0 ? null : Theme.TagColors[i - 1];
                bool selected = string.Equals(color, _selected, StringComparison.OrdinalIgnoreCase);
                if (selected || i == _hover)
                    Gfx.Circle(c, cx, cy, r + 3, selected ? Theme.TextPrimary.WithAlpha(200) : Theme.BorderStrong);
                if (selected)
                    Gfx.Circle(c, cx, cy, r + 1.5f, Theme.Overlay);
                if (color is null)
                {
                    Gfx.Circle(c, cx, cy, r, Theme.Input);
                    Gfx.StrokeRound(c, new SKRect(cx - r, cy - r, cx + r, cy + r), r, Theme.BorderStrong);
                    Gfx.Line(c, cx - r * 0.55f, cy + r * 0.55f, cx + r * 0.55f, cy - r * 0.55f, Theme.TextMuted, 1.5f);
                }
                else
                {
                    Gfx.Circle(c, cx, cy, r, Theme.ParseHex(color, Theme.Idle));
                }
            }
        }
    }
}
