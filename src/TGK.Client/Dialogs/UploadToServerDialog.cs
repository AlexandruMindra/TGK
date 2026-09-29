using System;
using System.Threading.Tasks;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Dialogs;

/// <summary>
/// Local mode: moves the local vault to a server account, a new one (registration with an authenticator, then the
/// vault is uploaded as it is) or an existing one (its data is merged in). On success the app continues in server
/// mode, and the local vault is removed if asked (only once everything reached the server and none of its entries
/// lost a conflict with the account).
/// </summary>
public sealed class UploadToServerDialog : FormDialog
{
    private enum Page { Form, Authenticator, Done }

    private const int CodeLength = 6;
    private const float QrSize = 176;

    private readonly Label _serverCaption, _userCaption, _inviteCaption, _codeCaption, _passwordCaption;
    private readonly TextField _server, _user, _invite, _code, _password;
    private readonly NewPasswordRows _newPassword;
    private readonly SegmentedControl _kind;
    private readonly Checkbox _keep, _remove;
    private readonly Label _qrNote, _keyCaption, _setupCodeCaption, _result;
    private readonly QrCodeView _qr;
    private readonly SetupKeyBox _key;
    private readonly TextField _setupCode;
    private readonly Button _back, _primary;
    private Page _page;
    private TotpEnrollment? _enrollment;
    private string? _enrollmentUser;

    public UploadToServerDialog(TgkView view) : base(view, "Upload to a server account", 560)
    {
        ClientPrefs prefs = view.Services.Prefs;
        Subtitle = "Moves your local vault to a TGK server, so it syncs to all your devices.";
        _kind = AddField(new SegmentedControl("New account", "Existing account"));
        _serverCaption = AddBody(Form.Caption("Server URL"));
        _server = AddField(new TextField("https://tgk.example.com") { Text = prefs.LastServer ?? "" });
        _userCaption = AddBody(Form.Caption("Username"));
        _user = AddField(new TextField { MaxLength = UsernameRules.MaxLength });
        _kind.SelectionChanged += _ =>
        {
            if (!NewAccount && _user.Text.Length == 0)
                _user.Text = prefs.LastUsername ?? "";
            ShowPage(Page.Form);
        };
        _inviteCaption = AddBody(Form.Caption("Invite code (optional)"));
        _invite = AddField(new TextField("Only if the server admin gave you one") { Mono = true, MaxLength = 32 });
        _passwordCaption = AddBody(Form.Caption("Password"));
        _password = AddField(new TextField("Your account password") { IsPassword = true });
        _codeCaption = AddBody(Form.Caption("Authenticator code"));
        _code = AddField(CodeField());
        _newPassword = AddNewPassword("Password", "Confirm password");
        _keep = AddField(new Checkbox("Keep me signed in", prefs.KeepSignedIn));
        _remove = AddField(new Checkbox("Remove the local vault after the upload", isChecked: true));

        _qrNote = AddBody(new Label("Scan the QR code with Google Authenticator (or any TOTP app), then enter the 6-digit code it shows.",
            Theme.FontSm, Theme.TextSecondary) { MaxLines = 2 });
        _qr = AddBody(new QrCodeView());
        _keyCaption = AddBody(new Label("Can't scan it? Enter this setup key in the app:", Theme.FontSm, Theme.TextSecondary));
        _key = AddField(new SetupKeyBox());
        _key.Copied += () => View.ShowToast("Setup key copied", ToastKind.Success);
        _setupCodeCaption = AddBody(Form.Caption("Code shown in the app"));
        _setupCode = AddField(CodeField());
        _setupCode.Changed += text =>
        {
            if (text.Length == CodeLength)
                Accept();
        };
        _result = AddBody(new Label("", Theme.FontBase, Theme.TextSecondary) { MaxLines = 8 });

        _back = AddFormButton("Cancel", ButtonVariant.Secondary, Back);
        _primary = AddFormButton("", ButtonVariant.Primary, Accept);
        ShowPage(Page.Form);
    }

    private bool NewAccount => _kind.SelectedIndex == 0;

    protected override VisualElement? InitialFocus => _server.Text.Length == 0 ? _server : _user;

    private static TextField CodeField() => new("6-digit code") { DigitsOnly = true, MaxLength = CodeLength, Mono = true };

    /// <summary>Shows <paramref name="page"/> without the previous error (callers set a new one after).</summary>
    private void ShowPage(Page page)
    {
        SetError(null);
        _page = page;
        bool form = page == Page.Form, auth = page == Page.Authenticator;
        foreach (VisualElement e in new VisualElement[] { _kind, _serverCaption, _server, _userCaption, _user, _keep, _remove })
            e.Visible = form;
        foreach (VisualElement e in new VisualElement[] { _inviteCaption, _invite, _newPassword.Caption, _newPassword.Password, _newPassword.Meter, _newPassword.ConfirmCaption, _newPassword.Confirm })
            e.Visible = form && NewAccount;
        foreach (VisualElement e in new VisualElement[] { _codeCaption, _code, _passwordCaption, _password })
            e.Visible = form && !NewAccount;
        foreach (VisualElement e in new VisualElement[] { _qrNote, _qr, _keyCaption, _key, _setupCodeCaption, _setupCode })
            e.Visible = auth;
        _result.Visible = page == Page.Done;
        _back.Visible = page != Page.Done;
        _back.Text = auth ? "Back" : "Cancel";
        UpdatePrimary(busy: false);
        InvalidateLayout();
    }

    private void UpdatePrimary(bool busy) => _primary.Text = (_page, busy) switch
    {
        (Page.Done, _) => "Done",
        (_, true) when _page == Page.Form && NewAccount => "Checking server…",
        (_, true) => "Uploading…",
        (Page.Form, _) => NewAccount ? "Continue" : "Sign in and upload",
        _ => "Create account and upload",
    };

    protected override float LayoutBody(float left, float top, float width)
    {
        float col = (width - 16) / 2f, right = left + col + 16;
        float y = top;
        switch (_page)
        {
            case Page.Form:
                _kind.Transform.SetLocalFrame(left, y, width, 34);
                y = Form.Place(_serverCaption, _server, left, y + 34 + Form.RowGap, width) + Form.RowGap;
                if (NewAccount)
                {
                    Form.Place(_userCaption, _user, left, y, col);
                    y = Form.Place(_inviteCaption, _invite, right, y, col) + Form.RowGap;
                    y = _newPassword.PlaceSideBySide(left, y, width) + Form.RowGap;
                }
                else
                {
                    Form.Place(_userCaption, _user, left, y, col);
                    y = Form.Place(_passwordCaption, _password, right, y, col) + Form.RowGap;
                    y = Form.Place(_codeCaption, _code, left, y, col) + Form.RowGap;
                }
                _keep.Transform.SetLocalFrame(left, y, _keep.PreferredWidth, 22);
                _remove.Transform.SetLocalFrame(left, y + 30, _remove.PreferredWidth, 22);
                y += 52;
                break;
            case Page.Authenticator:
                float noteH = _qrNote.MeasureHeight(width);
                _qrNote.Transform.SetLocalFrame(left, y, width, noteH);
                y += noteH + 12;
                _qr.Transform.SetLocalFrame(MathF.Round(left + (width - QrSize) / 2f), y, QrSize, QrSize);
                y += QrSize + 14;
                _keyCaption.Transform.SetLocalFrame(left, y, width, 16);
                _key.Transform.SetLocalFrame(left, y + 22, width, Theme.FieldHeight);
                y = Form.Place(_setupCodeCaption, _setupCode, left, y + 22 + Theme.FieldHeight + Form.RowGap, col);
                break;
            default:
                float h = _result.MeasureHeight(width);
                _result.Transform.SetLocalFrame(left, y, width, h);
                y += h;
                break;
        }
        return PlaceError(left, y, width) - top;
    }

    /// <summary>The footer's Back (Cancel) button and Escape: from the authenticator step back to the form, else close. The close button always closes.</summary>
    private void Back()
    {
        if (IsBusy)
            return;
        if (_page != Page.Authenticator)
        {
            Close();
            return;
        }
        ShowPage(Page.Form);
        _newPassword.Password.Focus();
    }

    public override bool OnKey(KeyStroke k)
    {
        if (_page != Page.Authenticator || !k.Is(Key.Escape) || k.IsRepeat)
            return base.OnKey(k);
        Back();
        return true;
    }

    protected override void Accept()
    {
        if (IsBusy)
            return;
        switch (_page)
        {
            case Page.Form when NewAccount:
                ContinueNewAccount();
                break;
            case Page.Form:
                UploadToExistingAccount();
                break;
            case Page.Authenticator:
                UploadToNewAccount();
                break;
            default:
                Close();
                break;
        }
    }

    /// <summary>Checks the form and the server, then shows the authenticator step.</summary>
    private async void ContinueNewAccount()
    {
        if (CheckServer() is not { } server)
            return;
        string user = _user.Text.Trim();
        if (UsernameRules.Validate(user) is { } userError)
        {
            SetError(userError, _user);
            return;
        }
        if (!CheckNewPassword(_newPassword))
            return;

        SetWorking(true);
        ServerInfo? info = await View.Services.Vault.GetServerInfoAsync(server);
        SetWorking(false);
        if (IsClosed)
            return;
        if (info is null)
        {
            SetError("Could not reach a TGK server at this address.", _server);
            return;
        }
        if (!info.RegistrationOpen && _invite.Text.Trim().Length == 0)
        {
            SetError("This server only accepts new accounts with an invite code from its admin.", _invite);
            return;
        }
        if (_enrollment is null || !string.Equals(_enrollmentUser, user, StringComparison.OrdinalIgnoreCase))
        {
            _enrollment = View.Services.Vault.BeginTotpEnrollment(user);
            _enrollmentUser = user;
        }
        _qr.Content = _enrollment.OtpAuthUri;
        _key.Secret = _enrollment.Secret;
        _setupCode.Text = "";
        ShowPage(Page.Authenticator);
        _setupCode.Focus();
    }

    private async void UploadToNewAccount()
    {
        if (_enrollment is not { } enrollment)
            return;
        if (_setupCode.Text.Length != CodeLength)
        {
            SetError("Enter the 6-digit code shown in your authenticator app.", _setupCode);
            return;
        }
        string server = _server.Text.Trim(), user = _user.Text.Trim(), invite = _invite.Text.Trim();
        await RunMigration(() => View.Services.Migration.LocalToNewAccountAsync(server, user, _newPassword.Password.Text,
            invite.Length == 0 ? null : invite, enrollment.Secret, _setupCode.Text, _keep.Checked, _remove.Checked), server, user);
    }

    private async void UploadToExistingAccount()
    {
        if (CheckServer() is not { } server)
            return;
        string user = _user.Text.Trim();
        if (user.Length == 0)
        {
            SetError("Enter your username.", _user);
            return;
        }
        if (_password.Text.Length == 0)
        {
            SetError("Enter your password.", _password);
            return;
        }
        if (_code.Text.Length is > 0 and < CodeLength)
        {
            SetError("Enter all 6 digits of the authenticator code.", _code);
            return;
        }
        string? code = _code.Text.Length == 0 ? null : _code.Text;
        await RunMigration(() => View.Services.Migration.LocalToExistingAccountAsync(server, user, _password.Text, code,
            _keep.Checked, _remove.Checked), server, user);
    }

    private string? CheckServer()
    {
        string server = _server.Text.Trim();
        if (server.Length == 0)
        {
            SetError("Enter the server address.", _server);
            return null;
        }
        if (ServerAddress.IsMock(server))
        {
            SetError("Uploading needs a real TGK server.", _server);
            return null;
        }
        if (!ServerAddress.TryParse(server, out _, out string? error))
        {
            SetError(error, _server);
            return null;
        }
        return server;
    }

    private async Task RunMigration(Func<Task<MigrationResult>> migrate, string server, string user)
    {
        SetWorking(true);
        MigrationResult result;
        try
        {
            result = await migrate();
        }
        catch (Exception ex) when (ex is VaultException or InvalidOperationException)
        {
            SetWorking(false);
            SetError(ex.Message);
            return;
        }
        SetWorking(false);
        if (!result.Success)
        {
            ShowFailure(result.Login);
            return;
        }

        bool keep = _keep.Checked;
        View.Services.UpdatePrefs(p =>
        {
            p.Mode = VaultMode.Server;
            p.LastServer = server;
            p.LastUsername = user;
            p.KeepSignedIn = keep;
        });
        (View as MainView)?.OnVaultModeChanged();
        MergeCounts counts = result.Counts;
        string uploaded = NewAccount
            ? $"Uploaded {counts.Added} item{(counts.Added == 1 ? "" : "s")}."
            : $"{counts.Added} added, {counts.Skipped} already there.";
        if (counts.Conflicts > 0)
            uploaded += $" {counts.Conflicts} entr{(counts.Conflicts == 1 ? "y was" : "ies were")} already in the account in another version: the account's version was kept.";
        string pending = result.Uploaded ? "" : "Some items haven't reached the server yet; TGK keeps sending them while you're signed in. ";
        string local = result.LocalVaultRemoved ? "The local vault was removed from this computer."
            : !_remove.Checked ? "The local vault stays on this computer, locked."
            : !result.Uploaded ? "The local vault was kept on this computer (locked) until everything is on the server."
            : counts.Conflicts > 0 ? "The local vault was kept on this computer (locked), so its versions of those entries are not lost."
            : "The local vault could not be removed; you can delete it later from its unlock screen.";
        _result.Text = $"Your vault is now in the account “{user}” on {server}. {uploaded}\n\n{pending}{local}";
        Title = "Upload complete";
        Subtitle = null;
        ShowPage(Page.Done);
    }

    private void ShowFailure(LoginResult login)
    {
        string error = login.Error ?? "The upload failed.";
        switch (login.ErrorCode)
        {
            case VaultError.TotpRequired or VaultError.TotpInvalid:
                SetError(error, _page == Page.Authenticator ? _setupCode : _code);
                break;
            case VaultError.TotpSetupRequired:
                ShowPage(Page.Form);
                SetError("This account needs a new authenticator: sign in to it once from the sign-in screen, then upload again.");
                break;
            case VaultError.InvalidCredentials:
                ShowPage(Page.Form);
                SetError(error, NewAccount ? _newPassword.Password : _password);
                break;
            case VaultError.UsernameTaken:
                ShowPage(Page.Form);
                SetError(error, _user);
                break;
            case VaultError.InviteInvalid or VaultError.RegistrationClosed:
                ShowPage(Page.Form);
                SetError(error, _invite);
                break;
            case VaultError.InsecureUrl or VaultError.Network or VaultError.IncompatibleServer:
                ShowPage(Page.Form);
                SetError(error, _server);
                break;
            default:
                SetError(error);
                break;
        }
    }

    private void SetWorking(bool busy)
    {
        SetBusy(busy);
        UpdatePrimary(busy);
    }
}
