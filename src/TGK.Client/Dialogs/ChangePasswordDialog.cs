using Blossom.Core.Visual;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>Changes the account password (confirmed with the current password and an authenticator code).</summary>
public sealed class ChangePasswordDialog : DialogBase
{
    private readonly IVaultService _vault;
    private readonly Label _currentCaption, _codeCaption, _newCaption, _confirmCaption, _error;
    private readonly TextField _current, _code, _new, _confirm;
    private readonly PasswordMeter _meter;
    private readonly Checkbox _signOutOthers;
    private readonly Button _save;
    private bool _busy;

    public ChangePasswordDialog(TgkView view) : base(view, "Change password", 480)
    {
        _vault = view.Services.Vault;
        Subtitle = "Only your vault key is re-encrypted; your hosts and keys stay as they are.";
        _currentCaption = AddBody(Form.Caption("Current password"));
        _current = AddBody(new TextField { IsPassword = true });
        _codeCaption = AddBody(Form.Caption("Authenticator code"));
        _code = AddBody(new TextField("6-digit code") { DigitsOnly = true, MaxLength = 6, Mono = true });
        _newCaption = AddBody(Form.Caption("New password"));
        _new = AddBody(new TextField($"At least {PasswordMeter.MinLength} characters") { IsPassword = true });
        _meter = AddBody(new PasswordMeter());
        _confirmCaption = AddBody(Form.Caption("Confirm new password"));
        _confirm = AddBody(new TextField { IsPassword = true });
        _signOutOthers = AddBody(new Checkbox("Sign out my other devices", isChecked: true));
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 3, Visible = false });
        foreach (TextField field in new[] { _current, _code, _new, _confirm })
            field.Changed += _ => SetError(null);
        _new.Changed += text => _meter.Password = text;

        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        _save = AddButton("Change password", ButtonVariant.Primary, Accept);
    }

    protected override VisualElement? InitialFocus => _current;

    protected override float LayoutBody(float left, float top, float width)
    {
        float col = (width - 16) / 2f;
        float y = top;
        Form.Place(_currentCaption, _current, left, y, col + 40);
        y = Form.Place(_codeCaption, _code, left + col + 56, y, col - 40) + Form.RowGap;
        y = Form.Place(_newCaption, _new, left, y, width);
        _meter.Transform.SetLocalFrame(left, y + 4, width, 16);
        y += 20 + Form.RowGap;
        y = Form.Place(_confirmCaption, _confirm, left, y, width) + Form.RowGap;
        _signOutOthers.Transform.SetLocalFrame(left, y, _signOutOthers.PreferredWidth, 22);
        y += 22;
        if (_error.Visible)
        {
            float h = _error.MeasureHeight(width);
            _error.Transform.SetLocalFrame(left, y + 12, width, h);
            y += 12 + h;
        }
        return y - top;
    }

    protected override void Cancel()
    {
        if (!_busy)
            Close();
    }

    protected override async void Accept()
    {
        if (_busy)
            return;
        if (_current.Text.Length == 0)
        {
            SetError("Enter your current password.", _current);
            return;
        }
        if (_code.Text.Length != 6)
        {
            SetError("Enter the 6-digit code from your authenticator app.", _code);
            return;
        }
        if (_new.Text.Length < PasswordMeter.MinLength)
        {
            SetError($"Use at least {PasswordMeter.MinLength} characters for the new password.", _new);
            return;
        }
        if (_confirm.Text != _new.Text)
        {
            SetError("The new passwords don't match.", _confirm);
            return;
        }

        SetBusy(true);
        try
        {
            await _vault.ChangePasswordAsync(_current.Text, _code.Text, _new.Text, _signOutOthers.Checked);
            SetBusy(false);
            Close();
            View.ShowToast("Password changed", ToastKind.Success);
        }
        catch (VaultException ex)
        {
            SetBusy(false);
            SetError(ex.Message, ex.Code switch
            {
                VaultError.InvalidCredentials => _current,
                VaultError.TotpRequired or VaultError.TotpInvalid => _code,
                VaultError.ValidationFailed => _new,
                _ => null,
            });
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _save.Text = busy ? "Deriving keys…" : "Change password";
        foreach (Control control in new Control[] { _current, _code, _new, _confirm, _signOutOthers, _save })
            control.Enabled = !busy;
    }

    private void SetError(string? message, TextField? field = null)
    {
        if (message is null && !_error.Visible)
            return;
        _error.Text = message ?? "";
        _error.Visible = message is not null;
        foreach (TextField f in new[] { _current, _code, _new, _confirm })
            f.HasError = message is not null && f == field;
        if (field is not null)
        {
            field.Focus();
            field.SelectAll();
        }
        InvalidateLayout();
    }
}
