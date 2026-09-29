using System.Collections.Generic;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// A dialog holding a form: an error line under the fields, and a busy state that disables the fields and buttons
/// while an operation runs (Escape and the close button wait for it).
/// </summary>
public abstract class FormDialog : DialogBase
{
    private readonly List<Control> _controls = [];
    private readonly Label _error;

    protected FormDialog(TgkView view, string title, float width) : base(view, title, width)
    {
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 3, Visible = false });
    }

    protected bool IsBusy { get; private set; }

    /// <summary>Adds a body control that is disabled while busy; editing a text field clears the error.</summary>
    protected T AddField<T>(T control) where T : Control
    {
        AddBody(control);
        _controls.Add(control);
        if (control is TextField field)
            field.Changed += _ => SetError(null);
        return control;
    }

    /// <summary>Adds a footer button that is disabled while busy.</summary>
    protected Button AddFormButton(string text, ButtonVariant variant, System.Action onClick)
    {
        Button button = AddButton(text, variant, onClick);
        _controls.Add(button);
        return button;
    }

    /// <summary>"Choose a password" with a strength meter, and "Confirm" (see <see cref="CheckNewPassword"/>).</summary>
    protected NewPasswordRows AddNewPassword(string caption, string confirmCaption)
    {
        var rows = new NewPasswordRows(
            AddBody(Form.Caption(caption)),
            AddField(new TextField { IsPassword = true }),
            AddBody(new PasswordMeter()),
            AddBody(Form.Caption(confirmCaption)),
            AddField(new TextField { IsPassword = true }));
        rows.Password.Changed += text => rows.Meter.Password = text;
        return rows;
    }

    /// <summary>Shows the error for a too short or unconfirmed new password; true when it is fine.</summary>
    protected bool CheckNewPassword(NewPasswordRows rows)
    {
        if (rows.Password.Text.Length < LocalVaultService.MinPasswordLength)
        {
            SetError($"Use at least {LocalVaultService.MinPasswordLength} characters for the password.", rows.Password);
            return false;
        }
        if (rows.Confirm.Text != rows.Password.Text)
        {
            SetError("The passwords don't match.", rows.Confirm);
            return false;
        }
        return true;
    }

    /// <summary>Disables (enables) the fields and buttons, and lays out again for the button labels that go with it.</summary>
    protected void SetBusy(bool busy)
    {
        IsBusy = busy;
        foreach (Control control in _controls)
            control.Enabled = !busy;
        InvalidateLayout();
    }

    /// <summary>Shows <paramref name="message"/> under the form (null clears it) and marks, focuses and selects <paramref name="field"/>.</summary>
    protected void SetError(string? message, TextField? field = null)
    {
        if (message is null && !_error.Visible)
            return;
        _error.Text = message ?? "";
        _error.Visible = message is not null;
        foreach (Control control in _controls)
        {
            if (control is TextField f)
                f.HasError = message is not null && f == field;
        }
        if (field is not null)
        {
            field.Focus();
            field.SelectAll();
        }
        InvalidateLayout();
    }

    /// <summary>Places the error line (when shown) below <paramref name="y"/>; returns the new bottom.</summary>
    protected float PlaceError(float left, float y, float width)
    {
        if (!_error.Visible)
            return y;
        float h = _error.MeasureHeight(width);
        _error.Transform.SetLocalFrame(left, y + 12, width, h);
        return y + 12 + h;
    }

    protected override void Cancel()
    {
        if (!IsBusy)
            Close();
    }

    /// <summary>The rows added by <see cref="AddNewPassword"/>.</summary>
    protected sealed record NewPasswordRows(Label Caption, TextField Password, PasswordMeter Meter, Label ConfirmCaption, TextField Confirm)
    {
        /// <summary>Stacked: password, meter, confirm. Returns the bottom of the confirm field.</summary>
        public float Place(float x, float y, float width)
        {
            y = Form.Place(Caption, Password, x, y, width);
            Meter.Transform.SetLocalFrame(x, y + 4, width, 16);
            return Form.Place(ConfirmCaption, Confirm, x, y + 20 + Form.RowGap - 4, width);
        }

        /// <summary>Password and confirm side by side, the meter under the password. Returns the bottom of the meter.</summary>
        public float PlaceSideBySide(float x, float y, float width)
        {
            float col = (width - 16) / 2f;
            float bottom = Form.Place(Caption, Password, x, y, col);
            Form.Place(ConfirmCaption, Confirm, x + col + 16, y, col);
            Meter.Transform.SetLocalFrame(x, bottom + 4, col, 16);
            return bottom + 20;
        }
    }
}
