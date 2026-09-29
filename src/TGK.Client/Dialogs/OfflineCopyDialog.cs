using Blossom.Core.Visual;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// Server mode: copies the account's data into a local vault on this device (a new one, or replacing the existing one
/// after a warning), protected by its own master password. The account is not changed.
/// </summary>
public sealed class OfflineCopyDialog : FormDialog
{
    private readonly Label _note, _replaceNote;
    private readonly NewPasswordRows _master;
    private readonly Button _save;

    public OfflineCopyDialog(TgkView view) : base(view, "Save an offline copy", 480)
    {
        Subtitle = "A local vault on this computer that works without the server.";
        _note = AddBody(new Label("Your hosts, keys and settings are copied into it, encrypted with a master password of its own. " +
            "Your account doesn't change. To use the copy, choose “This device only” on the sign-in screen.",
            Theme.FontSm, Theme.TextSecondary) { MaxLines = 4 });
        _replaceNote = AddBody(new Label("This computer already has a local vault. Saving replaces it and everything in it.",
            Theme.FontSm, Theme.Warning) { MaxLines = 2, Visible = view.Services.Vault.Local.Exists });
        _master = AddNewPassword("Master password for the copy", "Confirm master password");
        AddFormButton("Cancel", ButtonVariant.Secondary, Cancel);
        _save = AddFormButton("Save copy", ButtonVariant.Primary, Accept);
    }

    protected override VisualElement? InitialFocus => _master.Password;

    protected override float LayoutBody(float left, float top, float width)
    {
        float h = _note.MeasureHeight(width);
        _note.Transform.SetLocalFrame(left, top, width, h);
        float y = top + h + 14;
        if (_replaceNote.Visible)
        {
            float rh = _replaceNote.MeasureHeight(width);
            _replaceNote.Transform.SetLocalFrame(left, y - 4, width, rh);
            y += rh + 10;
        }
        y = _master.Place(left, y, width);
        return PlaceError(left, y, width) - top;
    }

    protected override async void Accept()
    {
        if (IsBusy || !CheckNewPassword(_master))
            return;
        bool replace = View.Services.Vault.Local.Exists;
        if (replace && !await ConfirmDialog.ShowAsync(View, "Replace the local vault?",
                "The local vault on this computer and all hosts and keys in it are replaced by this copy. This can't be undone.",
                "Replace", danger: true))
            return;
        if (IsClosed)
            return;
        SetBusy(true);
        _save.Text = "Saving copy…";
        LoginResult result = await View.Services.Migration.SaveOfflineCopyAsync(_master.Password.Text, replace);
        SetBusy(false);
        _save.Text = "Save copy";
        if (result.Success)
        {
            Close();
            View.ShowToast("Offline copy saved on this computer", ToastKind.Success);
        }
        else
            SetError(result.Error ?? "Could not save the offline copy.", result.ErrorCode == VaultError.ValidationFailed ? _master.Password : null);
    }
}
