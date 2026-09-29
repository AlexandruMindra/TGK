using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// Create or edit a saved host: General (address, credentials, group), Connection, Session, Tunnels and Appearance.
/// Saves (and deletes) through the vault.
/// </summary>
public sealed class HostEditorDialog : TabbedDialog
{
    public const int GeneralTab = 0, ConnectionTab = 1, SessionTab = 2, TunnelsTab = 3, AppearanceTab = 4;
    private const float PortW = 100, Gap = 16;
    private readonly IVaultService _vault;
    private readonly HostEntry _entry;
    private readonly bool _isNew;
    private readonly List<Identity> _identities;
    private readonly Label _nameCaption, _hostCaption, _portCaption, _userCaption, _identityCaption, _groupCaption, _colorCaption, _notesCaption;
    private readonly TextField _name, _host, _port, _user, _group, _notes;
    private readonly Dropdown _identity;
    private readonly ColorSwatches _color;
    private readonly OptionsEditor _options;
    private readonly TunnelsEditor _tunnels;

    /// <param name="host">The host to edit, or null for a new one.</param>
    /// <param name="template">Prefill for a new host (e.g. when duplicating).</param>
    public HostEditorDialog(TgkView view, HostEntry? host, HostEntry? template = null)
        : base(view, host is null ? "New host" : "Edit host", 640, "General", "Connection", "Session", "Tunnels", "Appearance")
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

        FormPage general = PageAt(GeneralTab);
        _nameCaption = general.Add(Form.Caption("Name"));
        _name = general.Add(new TextField("e.g. web-01") { Text = _entry.Name });
        _hostCaption = general.Add(Form.Caption("Host"));
        _host = general.Add(new TextField("hostname or IP address") { Text = _entry.Host, Mono = true });
        _portCaption = general.Add(Form.Caption("Port"));
        _port = general.Add(new TextField("22") { Text = _entry.Port.ToString(CultureInfo.InvariantCulture), Mono = true, MaxLength = 5 });
        _userCaption = general.Add(Form.Caption("Username"));
        _user = general.Add(new TextField("from identity") { Text = _entry.Username ?? "" });
        _identityCaption = general.Add(Form.Caption("Identity"));
        _identity = general.Add(new Dropdown
        {
            Options = _identities.Select(i => i.Name).Prepend("None (ask for password)").ToList(),
        });
        _identity.SelectedIndex = _identities.FindIndex(i => i.Id == _entry.IdentityId) + 1;
        _groupCaption = general.Add(Form.Caption("Group"));
        _group = general.Add(new TextField("No group") { Text = vault.FindGroup(_entry.GroupId)?.Name ?? "", TrailingIcon = "chevron-down" });
        _group.TrailingClicked += ShowGroupMenu;
        _colorCaption = general.Add(Form.Caption("Tag color"));
        _color = general.Add(new ColorSwatches { Selected = _entry.TagColor });
        _notesCaption = general.Add(Form.Caption("Notes"));
        _notes = general.Add(new TextField("Optional") { Text = _entry.Notes ?? "" });
        general.Layout = LayoutGeneral;

        _options = new OptionsEditor(view, _entry.Options, OptionsLevel.Host, ResolveInherited, Candidate,
            PageAt(ConnectionTab), PageAt(SessionTab), PageAt(AppearanceTab), _entry.Id);
        _options.LayoutChanged += InvalidateLayout;
        _tunnels = new TunnelsEditor(PageAt(TunnelsTab), _entry.Tunnels);
        _tunnels.LayoutChanged += InvalidateLayout;

        _host.Changed += _ => ClearError(_host);
        _port.Changed += _ => ClearError(_port);

        if (!_isNew)
            AddLeftButton("Delete", ButtonVariant.Ghost, Delete).Icon = "trash";
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton(_isNew ? "Add host" : "Save", ButtonVariant.Primary, Accept);
    }

    protected override Blossom.Core.Visual.VisualElement? InitialFocus => _isNew && _entry.Host.Length == 0 ? _name : _host;

    // The options inherit from the group typed on the General page (it may have changed since the dialog opened).
    private EffectiveHostOptions ResolveInherited()
    {
        VaultData vault = _vault.Current;
        HostGroup? group = FindGroup(_group.Text.Trim());
        return EffectiveOptions.Resolve(null, group?.Options, vault.Defaults, View.Services.Prefs.Terminal.FontSize, _entry.Id)
            with { GroupName = group?.Name };
    }

    // The vault with this host saved with `options`, in the group typed on the General page.
    private VaultData Candidate(HostOptions options)
    {
        VaultData vault = _vault.Current.ShallowCopy();
        HostEntry host = _entry.Clone();
        host.Options = options;
        host.GroupId = FindGroup(_group.Text.Trim())?.Id;
        int index = vault.Hosts.FindIndex(h => h.Id == host.Id);
        if (index >= 0)
            vault.Hosts[index] = host;
        else
            vault.Hosts.Add(host);
        return vault;
    }

    private HostGroup? FindGroup(string name) => name.Length == 0 ? null
        : _vault.Current.Groups.Find(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    protected override void OnTabShown(int index) => _options.RefreshInherited();

    private float LayoutGeneral(float width)
    {
        float col = (width - Gap) / 2f;
        float y = 0;
        y = Form.Place(_nameCaption, _name, 0, y, width) + Form.RowGap;
        Form.Place(_hostCaption, _host, 0, y, width - PortW - Gap);
        y = Form.Place(_portCaption, _port, width - PortW, y, PortW) + Form.RowGap;
        Form.Place(_userCaption, _user, 0, y, col);
        y = Form.Place(_identityCaption, _identity, col + Gap, y, col) + Form.RowGap;
        Form.Place(_groupCaption, _group, 0, y, col);
        y = Form.Place(_colorCaption, _color, col + Gap, y, col) + Form.RowGap;
        return Form.Place(_notesCaption, _notes, 0, y, width);
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

    private void ClearError(TextField field)
    {
        field.HasError = false;
        ClearError();
    }

    protected override void Accept()
    {
        FormPage general = PageAt(GeneralTab);
        string host = _host.Text.Trim();
        if (host.Length == 0 || host.AsSpan().IndexOfAny(" \t") >= 0)
        {
            ShowError(host.Length == 0 ? "Enter the host name or IP address." : "The host name must not contain spaces.", general, _host);
            return;
        }
        if (!int.TryParse(_port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            ShowError("The port must be a number between 1 and 65535.", general, _port);
            return;
        }
        HostOptions options = _entry.Options.Clone(); // keeps members a newer client added
        if (_options.Store(options) is { } problem)
        {
            ShowError(problem.Message, problem.Page, problem.Field);
            return;
        }
        if (_tunnels.Collect() is not { } tunnels)
        {
            ShowTab(TunnelsTab);
            return;
        }

        _entry.Name = _name.Text.Trim();
        _entry.Host = host;
        _entry.Port = port;
        _entry.Username = _user.Text.Trim() is { Length: > 0 } user ? user : null;
        _entry.IdentityId = _identity.SelectedIndex > 0 ? _identities[_identity.SelectedIndex - 1].Id : null;
        _entry.TagColor = _color.Selected;
        _entry.Notes = _notes.Text.Trim() is { Length: > 0 } notes ? notes : null;
        _entry.Options = options;
        _entry.Tunnels = tunnels;

        string groupName = _group.Text.Trim();
        HostGroup? group = FindGroup(groupName);
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
        if (await ConfirmDelete(View, _entry) && View.RunVault(() => _vault.DeleteHostAsync(_entry.Id)))
            Close();
    }

    /// <summary>
    /// Asks before deleting <paramref name="host"/>. Hosts, groups and defaults that use it as their jump host are
    /// named: they keep pointing at it (the editors show "Deleted host") and can't connect until another is chosen.
    /// </summary>
    public static Task<bool> ConfirmDelete(TgkView view, HostEntry host)
    {
        VaultData vault = view.Services.Vault.Current;
        List<string> users = vault.Hosts.Where(h => h.Id != host.Id && h.Options.JumpHostId == host.Id).Select(h => h.DisplayName)
            .Concat(vault.Groups.Where(g => g.Options.JumpHostId == host.Id).Select(g => $"group {g.Name}"))
            .ToList();
        if (vault.Defaults.JumpHostId == host.Id)
            users.Add("the connection defaults");
        string jump = users.Count == 0 ? ""
            : $" It is the jump host of {string.Join(", ", users.Take(4))}{(users.Count > 4 ? $" and {users.Count - 4} more" : "")}: "
                + "those connections will fail until another jump host is chosen.";
        return ConfirmDialog.ShowAsync(view, "Delete host?",
            $"“{host.DisplayName}” will be removed from your vault on all devices. Open sessions stay connected.{jump}",
            "Delete", danger: true);
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
