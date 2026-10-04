using System;
using System.Linq;
using System.Threading.Tasks;
using Blossom.Core.Visual;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// Create, rename or delete a host group and edit the settings its hosts inherit (Connection, Session, Appearance, Agents;
/// a host's own values win, and unset values come from the connection defaults).
/// </summary>
public sealed class GroupSettingsDialog : TabbedDialog
{
    public const int GeneralTab = 0, ConnectionTab = 1, SessionTab = 2, AppearanceTab = 3, AgentsTab = 4;
    private readonly IVaultService _vault;
    private readonly HostGroup _group;
    private readonly bool _isNew;
    private readonly Label _nameCaption, _info;
    private readonly TextField _name;
    private readonly OptionsEditor _options;

    /// <param name="group">The group to edit, or null for a new one.</param>
    public GroupSettingsDialog(TgkView view, HostGroup? group)
        : base(view, group is null ? "New group" : "Group settings", 640, "General", "Connection", "Session", "Appearance", "Agents")
    {
        _vault = view.Services.Vault;
        _isNew = group is null;
        _group = group?.Clone() ?? new HostGroup
        {
            Name = "",
            SortOrder = _vault.Current.Groups.Select(g => g.SortOrder).DefaultIfEmpty(-1).Max() + 1,
        };
        Subtitle = !_isNew ? _group.Name
            : view.Services.IsLocal ? "Groups and their settings are kept in your local vault." : "Groups and their settings sync to all your devices.";

        FormPage general = PageAt(GeneralTab);
        _nameCaption = general.Add(Form.Caption("Name"));
        _name = general.Add(new TextField("e.g. Production") { Text = _group.Name, MaxLength = 100 });
        _name.Changed += _ =>
        {
            _name.HasError = false;
            ClearError();
        };
        int count = _vault.Current.Hosts.Count(h => h.GroupId == _group.Id);
        string hosts = _isNew ? "New hosts can be added to it in their General settings."
            : count == 0 ? "The group has no hosts yet." : $"The group has {count} host{(count == 1 ? "" : "s")}.";
        _info = general.Add(new Label(
            $"{hosts} They use the group's Connection, Session, Appearance and Agents settings unless they set their own; " +
            "what the group leaves unset comes from the connection defaults in Settings.",
            Theme.FontBase, Theme.TextSecondary) { MaxLines = 4 });
        general.Layout = LayoutGeneral;

        _options = new OptionsEditor(view, _group.Options, OptionsLevel.Group,
            () => EffectiveOptions.Resolve(null, null, _vault.Current.Defaults), Candidate,
            PageAt(ConnectionTab), PageAt(SessionTab), PageAt(AppearanceTab), PageAt(AgentsTab));
        _options.LayoutChanged += InvalidateLayout;

        if (!_isNew)
            AddLeftButton("Delete", ButtonVariant.Ghost, Delete).Icon = "trash";
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton(_isNew ? "Create group" : "Save", ButtonVariant.Primary, Accept);
    }

    protected override VisualElement? InitialFocus => _name;

    // The vault with this group saved with `options`.
    private VaultData Candidate(HostOptions options)
    {
        VaultData vault = _vault.Current.ShallowCopy();
        HostGroup group = _group.Clone();
        group.Options = options;
        int index = vault.Groups.FindIndex(g => g.Id == group.Id);
        if (index >= 0)
            vault.Groups[index] = group;
        else
            vault.Groups.Add(group);
        return vault;
    }

    protected override void OnTabShown(int index) => _options.RefreshInherited();

    private float LayoutGeneral(float width)
    {
        float y = Form.Place(_nameCaption, _name, 0, 0, width) + Form.RowGap;
        float h = _info.MeasureHeight(width);
        _info.Transform.SetLocalFrame(0, y, width, h);
        return y + h;
    }

    protected override void Accept()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Enter a name for the group.", PageAt(GeneralTab), _name);
            return;
        }
        if (_vault.Current.Groups.Exists(g => g.Id != _group.Id && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError($"There is already a group named “{name}”.", PageAt(GeneralTab), _name);
            return;
        }
        HostOptions options = _group.Options.Clone(); // keeps members a newer client added
        if (_options.Store(options) is { } problem)
        {
            ShowError(problem.Message, problem.Page, problem.Field);
            return;
        }
        _group.Name = name;
        _group.Options = options;
        if (View.RunVault(() => _vault.SaveGroupAsync(_group)))
            Close();
    }

    private async void Delete()
    {
        if (await ConfirmDelete(View, _group) && View.RunVault(() => _vault.DeleteGroupAsync(_group.Id)))
            Close();
    }

    /// <summary>Asks before deleting <paramref name="group"/>; its hosts become ungrouped and keep their own settings.</summary>
    public static Task<bool> ConfirmDelete(TgkView view, HostGroup group)
    {
        int count = view.Services.Vault.Current.Hosts.Count(h => h.GroupId == group.Id);
        string hosts = count == 0 ? "" : $" Its {count} host{(count == 1 ? " becomes" : "s become")} ungrouped and no longer inherit{(count == 1 ? "s" : "")} the group's settings.";
        return ConfirmDialog.ShowAsync(view, "Delete group?", $"“{group.Name}” will be removed{(view.Services.IsLocal ? "" : " on all devices")}.{hosts}", "Delete", danger: true);
    }
}
