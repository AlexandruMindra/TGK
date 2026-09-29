using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Views;

/// <summary>
/// Sign-in screen: one centered card that switches between signing in, creating an account and setting up an
/// authenticator (the second registration step, also used when an administrator reset the account's authenticator),
/// or, with "This device only", creating or unlocking the local vault (no server).
/// </summary>
public sealed class LoginView : TgkView
{
    private enum Step { SignIn, Register, Authenticator, LocalCreate, LocalUnlock }

    private const float CardW = 400, CardPad = 32, Inner = CardW - 2 * CardPad, FieldH = 36, RowGap = 12, QrSize = 200;
    private const int CodeLength = 6;

    private LoginRoot _root = null!;
    private Card _card = null!;
    private Label _title = null!, _subtitle = null!, _footer = null!, _error = null!, _switchPrompt = null!;
    private Notice _notice = null!;
    private Label _serverCaption = null!, _userCaption = null!, _passwordCaption = null!, _confirmCaption = null!, _inviteCaption = null!, _codeCaption = null!;
    private TextField _server = null!, _user = null!, _password = null!, _confirm = null!, _invite = null!, _code = null!, _setupCode = null!;
    private Label _userHint = null!, _keyCaption = null!, _setupCodeCaption = null!;
    private Label _localNote = null!, _masterCaption = null!, _masterConfirmCaption = null!;
    private TextField _master = null!, _masterConfirm = null!;
    private PasswordMeter _meter = null!, _masterMeter = null!;
    private SegmentedControl _mode = null!;
    private Checkbox _keepUnlocked = null!;
    private QrCodeView _qr = null!;
    private SetupKeyBox _key = null!;
    private Checkbox _keep = null!;
    private Button _primary = null!;
    private LinkButton _switchLink = null!;

    private Step _step;
    private bool _enrollForRegistration; // Authenticator step: a new account, or a reset authenticator at sign-in
    private TotpEnrollment? _enrollment;
    private string? _enrollmentUser;
    private ServerInfo? _serverInfo;
    private int _probeGeneration;
    private bool _busy;

    public LoginView(ClientServices services) : base("Login", services)
    {
    }

    protected override VisualElement? FocusScope => _card;

    // After a click on the background, typing and Enter continue in the form.
    protected override VisualElement? DefaultFocus => FirstEmptyField();

    private TextField[] Fields => [_server, _user, _password, _confirm, _invite, _code, _setupCode, _master, _masterConfirm];

    private bool IsLocalStep => _step is Step.LocalCreate or Step.LocalUnlock;

    protected override void Build()
    {
        _root = new LoginRoot(this);
        _card = new Card();
        _root.AddChild(_card);

        _title = Add(new Label("", Theme.FontXl, Theme.TextPrimary, Theme.WeightSemibold, TextAlignment.Center));
        _subtitle = Add(new Label("", Theme.FontBase, Theme.TextMuted, align: TextAlignment.Center) { MaxLines = 3 });
        _notice = Add(new Notice());
        _mode = Add(new SegmentedControl("Server account", "This device only"));
        _mode.SelectionChanged += OnModeSelected;
        _localNote = Add(new Label("", Theme.FontSm, Theme.TextSecondary) { MaxLines = 3 });
        _masterCaption = Add(Form.Caption("Master password"));
        _master = Add(new TextField { IsPassword = true });
        _masterMeter = Add(new PasswordMeter());
        _masterConfirmCaption = Add(Form.Caption("Confirm master password"));
        _masterConfirm = Add(new TextField("Repeat the master password") { IsPassword = true });
        _keepUnlocked = Add(new Checkbox("Keep unlocked on this device"));
        _serverCaption = Add(Form.Caption("Server URL"));
        _server = Add(new TextField("https://tgk.example.com"));
        _userCaption = Add(Form.Caption("Username"));
        _user = Add(new TextField { MaxLength = UsernameRules.MaxLength });
        _userHint = Add(new Label("", Theme.FontXs, Theme.TextMuted));
        _passwordCaption = Add(Form.Caption("Password"));
        _password = Add(new TextField { IsPassword = true });
        _meter = Add(new PasswordMeter());
        _confirmCaption = Add(Form.Caption("Confirm password"));
        _confirm = Add(new TextField("Repeat the password") { IsPassword = true });
        _inviteCaption = Add(Form.Caption(""));
        _invite = Add(new TextField { Mono = true, MaxLength = 32 });
        _codeCaption = Add(Form.Caption("Authenticator code"));
        _code = Add(CodeField());
        _qr = Add(new QrCodeView());
        _keyCaption = Add(new Label("Can't scan it? Enter this setup key in the app:", Theme.FontSm, Theme.TextSecondary));
        _key = Add(new SetupKeyBox());
        _key.Copied += () => ShowToast("Setup key copied", ToastKind.Success);
        _setupCodeCaption = Add(Form.Caption("Code shown in the app"));
        _setupCode = Add(CodeField());
        _keep = Add(new Checkbox("Keep me signed in"));
        _error = Add(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 3, Visible = false });
        _primary = Add(new Button("", ButtonVariant.Primary) { FontSize = Theme.FontMd });
        _primary.Clicked += Submit;
        _switchPrompt = Add(new Label("", Theme.FontSm, Theme.TextMuted));
        _switchLink = Add(new LinkButton());
        _switchLink.Clicked += SwitchStep;
        _footer = new Label("", Theme.FontSm, Theme.TextMuted, align: TextAlignment.Center);
        _root.AddChild(_footer);
        AddFullWindow(_root);

        foreach (TextField field in Fields)
            field.Changed += _ => SetError(null);
        _server.Changed += _ => ProbeServer();
        _user.Changed += _ => UpdateUserHint();
        _password.Changed += text => _meter.Password = text;
        _master.Changed += text => _masterMeter.Password = text;
        // A complete code submits by itself once everything else is filled in.
        _code.Changed += text =>
        {
            if (text.Length == CodeLength && _server.Text.Trim().Length > 0 && _user.Text.Trim().Length > 0 && _password.Text.Length > 0)
                Submit();
        };
        _setupCode.Changed += text =>
        {
            if (text.Length == CodeLength)
                Submit();
        };

        Prefill();
        ShowStep(InitialStep());
        switch (Services.Dev.Scene)
        {
            case "local-create":
                ShowStep(Step.LocalCreate);
                break;
            case "local-unlock":
                ShowStep(Step.LocalUnlock);
                break;
            case "register":
                ShowStep(Step.Register);
                break;
            case "register-totp" or "login-totp":
                if (_user.Text.Length == 0)
                    _user.Text = DevOptions.DevUser;
                StartAuthenticator(forRegistration: Services.Dev.Scene == "register-totp");
                break;
        }
        if (!App.TakeDevAutoLogin())
            return;
        if (Services.Dev.UsesLocalVault)
        {
            // Unlocks (or creates) the local vault of the dev config directory.
            _master.Text = _masterConfirm.Text = DevOptions.DevMasterPassword;
            _keepUnlocked.Checked = false;
            ShowStep(LocalStep());
            UiThread.Post(Submit);
        }
        else
        {
            _server.Text = DevOptions.DevServer;
            _user.Text = DevOptions.DevUser;
            _password.Text = DevOptions.DevPassword;
            ShowStep(Step.SignIn);
            UiThread.Post(Submit);
        }
    }

    protected override void OnShown()
    {
        base.OnShown();
        FirstEmptyField().Focus();
    }

    /// <summary>
    /// Back from a sign-out or lock: the sign-in (or local unlock) step with the passwords and codes cleared, and an
    /// optional notice (why the session ended).
    /// </summary>
    public void Reset(string? notice = null)
    {
        ClearSecrets();
        _notice.Text = notice ?? "";
        Prefill();
        ShowStep(InitialStep());
    }

    private void ClearSecrets()
    {
        foreach (TextField field in new[] { _password, _confirm, _invite, _code, _setupCode, _master, _masterConfirm })
            field.Text = "";
        _meter.Password = _masterMeter.Password = "";
        _enrollment = null;
    }

    /// <summary>The step for the remembered mode: server sign-in, or unlocking (creating) the local vault.</summary>
    private Step InitialStep() => Services.Prefs.Mode == VaultMode.Local ? LocalStep() : Step.SignIn;

    private Step LocalStep() => Services.Vault.Local.Exists ? Step.LocalUnlock : Step.LocalCreate;

    private T Add<T>(T element) where T : VisualElement
    {
        _card.AddChild(element);
        return element;
    }

    private static TextField CodeField() => new("6-digit code") { DigitsOnly = true, MaxLength = CodeLength, Mono = true, FontSize = Theme.FontMd };

    private void Prefill()
    {
        ClientPrefs prefs = Services.Prefs;
        _server.Text = prefs.LastServer ?? "";
        _user.Text = prefs.LastUsername ?? "";
        _keep.Checked = prefs.KeepSignedIn;
        _keepUnlocked.Checked = prefs.KeepLocalUnlocked;
        ProbeServer();
    }

    private TextField FirstEmptyField()
    {
        if (_step == Step.Authenticator)
            return _setupCode;
        if (_step == Step.LocalUnlock || (_step == Step.LocalCreate && _master.Text.Length == 0))
            return _master;
        if (_step == Step.LocalCreate)
            return _masterConfirm;
        TextField last = _step == Step.Register ? _confirm : _code;
        return new[] { _server, _user, _password, last }.FirstOrDefault(f => f.Text.Length == 0) ?? last;
    }

    // ---- steps ----

    private void ShowStep(Step step)
    {
        _step = step;
        bool signIn = step == Step.SignIn, register = step == Step.Register, auth = step == Step.Authenticator;
        bool create = step == Step.LocalCreate, unlock = step == Step.LocalUnlock, server = !create && !unlock;
        _title.Text = step switch
        {
            Step.SignIn or Step.LocalCreate or Step.LocalUnlock => "TGK",
            Step.Register => "Create your account",
            _ => "Set up Google Authenticator",
        };
        _subtitle.Text = step switch
        {
            Step.SignIn or Step.LocalCreate or Step.LocalUnlock => "SSH sessions, everywhere",
            Step.Register => "Your vault is end-to-end encrypted: the server never sees your hosts or keys.",
            _ when _enrollForRegistration => "Scan the QR code with Google Authenticator (or any TOTP app), then enter the 6-digit code it shows.",
            _ => "Your authenticator was reset. Scan the QR code with Google Authenticator, then enter the code it shows.",
        };
        SetVisible(!auth && server, _serverCaption, _server, _userCaption, _user, _passwordCaption, _password, _keep);
        SetVisible(register, _userHint, _meter, _confirmCaption, _confirm, _inviteCaption, _invite);
        SetVisible(signIn, _codeCaption, _code);
        SetVisible(auth, _qr, _keyCaption, _key, _setupCodeCaption, _setupCode);
        SetVisible(!server, _localNote, _masterCaption, _master, _keepUnlocked);
        SetVisible(create, _masterMeter, _masterConfirmCaption, _masterConfirm);
        _mode.Visible = signIn || !server;
        _mode.SelectedIndex = server ? 0 : 1;
        _footer.Visible = server;
        _localNote.Text = create
            ? "Your hosts and keys stay on this computer, encrypted with this password. It can't be recovered if forgotten."
            : "Your hosts and keys are stored on this computer. Enter the master password to unlock them.";
        _masterCaption.Text = create ? "Choose a master password" : "Master password";
        _notice.Visible = signIn && _notice.Text.Length > 0;
        _user.Placeholder = register ? "Choose a username" : "Your username";
        _password.Placeholder = register ? "Choose a password" : "Your password";
        UpdateUserHint();
        SetBusy(false);
        SetError(null);
        FirstEmptyField().Focus();
    }

    private static void SetVisible(bool visible, params VisualElement[] elements)
    {
        foreach (VisualElement element in elements)
            element.Visible = visible;
    }

    private void SwitchStep()
    {
        if (_busy)
            return;
        if (_step == Step.LocalUnlock)
            DeleteLocalVault();
        else
            ShowStep(_step == Step.SignIn || (_step == Step.Authenticator && _enrollForRegistration) ? Step.Register : Step.SignIn);
    }

    /// <summary>"Server account | This device only".</summary>
    private void OnModeSelected(int index)
    {
        if (_busy)
        {
            _mode.SelectedIndex = IsLocalStep ? 1 : 0;
            return;
        }
        ShowStep(index == 1 ? LocalStep() : Step.SignIn);
    }

    private void UpdateSwitchRow()
    {
        bool closed = _serverInfo is { RegistrationOpen: false };
        (_switchPrompt.Text, _switchLink.Text) = _step switch
        {
            Step.SignIn when closed => ("Have an invite code?", "Create an account"),
            Step.SignIn => ("New to TGK?", "Create an account"),
            Step.Register => ("Already have an account?", "Sign in"),
            Step.LocalCreate => ("Have a TGK server?", "Sign in to it"),
            Step.LocalUnlock => ("Forgot the master password?", "Delete this vault"),
            _ => ("", "Back"),
        };
        // An admin's invite (tgk-server user create) works on a server that is closed to everyone else.
        _inviteCaption.Text = closed ? "Invite code" : "Invite code (optional)";
        _invite.Placeholder = closed ? "Required by this server" : "Only if the server admin gave you one";
        _switchLink.Enabled = !_busy;
        _root.InvalidateLayout();
    }

    private void UpdateUserHint()
    {
        string user = _user.Text.Trim();
        string? problem = user.Length == 0 ? null : UsernameRules.Validate(user);
        _userHint.Text = problem ?? $"{UsernameRules.MinLength}-{UsernameRules.MaxLength} characters: letters, digits, '.', '_' and '-'.";
        _userHint.Color = problem is null ? Theme.TextMuted : Theme.Danger;
    }

    /// <summary>Asks the server (debounced) whether it is a TGK server and accepts new accounts.</summary>
    private async void ProbeServer()
    {
        int generation = ++_probeGeneration;
        string server = _server.Text.Trim();
        SetServerInfo(null, server);
        if (server.Length == 0)
            return;
        await Task.Delay(400);
        if (generation != _probeGeneration)
            return;
        ServerInfo? info = await Services.Vault.GetServerInfoAsync(server);
        if (generation == _probeGeneration)
            SetServerInfo(info, server);
    }

    private void SetServerInfo(ServerInfo? info, string server)
    {
        _serverInfo = info;
        _footer.Text = ServerAddress.IsMock(server) ? "Development mode · mock server" : info is null ? "" : $"{info.Name} {info.Version}";
        UpdateSwitchRow();
    }

    // ---- actions ----

    private void Submit()
    {
        if (_busy)
            return;
        switch (_step)
        {
            case Step.SignIn:
                SignIn();
                break;
            case Step.Register:
                ContinueRegistration();
                break;
            case Step.LocalCreate:
                CreateLocal();
                break;
            case Step.LocalUnlock:
                UnlockLocal();
                break;
            default:
                FinishAuthenticator();
                break;
        }
    }

    private async void SignIn()
    {
        string server = _server.Text.Trim(), user = _user.Text.Trim(), password = _password.Text, code = _code.Text;
        if (Require(_server, "Enter the server address.") || Require(_user, "Enter your username.") || Require(_password, "Enter your password."))
            return;
        if (code.Length is > 0 and < CodeLength)
        {
            SetError("Enter all 6 digits of the authenticator code.", _code);
            return;
        }

        _notice.Text = "";
        _notice.Visible = false;
        SetBusy(true);
        LoginResult result = await Services.Vault.LoginAsync(server, user, password, code.Length == 0 ? null : code, _keep.Checked);
        SetBusy(false);
        if (result.Success)
        {
            Complete();
            return;
        }
        if (result.ErrorCode == VaultError.TotpSetupRequired)
        {
            StartAuthenticator(forRegistration: false);
            return;
        }
        SetError(result.Error ?? "Sign-in failed.", result.ErrorCode switch
        {
            VaultError.TotpRequired or VaultError.TotpInvalid => _code,
            VaultError.InvalidCredentials => _password,
            VaultError.InsecureUrl or VaultError.Network or VaultError.IncompatibleServer => _server,
            _ => null,
        });
    }

    private async void ContinueRegistration()
    {
        string server = _server.Text.Trim();
        if (Require(_server, "Enter the server address."))
            return;
        if (!ServerAddress.IsMock(server) && !ServerAddress.TryParse(server, out _, out string? addressError))
        {
            SetError(addressError, _server);
            return;
        }
        if (UsernameRules.Validate(_user.Text.Trim()) is { } userError)
        {
            SetError(userError, _user);
            return;
        }
        if (_password.Text.Length < PasswordMeter.MinLength)
        {
            SetError($"Use at least {PasswordMeter.MinLength} characters for your password.", _password);
            return;
        }
        if (_confirm.Text != _password.Text)
        {
            SetError("The passwords don't match.", _confirm);
            return;
        }

        SetBusy(true);
        ServerInfo? info = await Services.Vault.GetServerInfoAsync(server);
        SetBusy(false);
        SetServerInfo(info, server);
        if (info is null)
            SetError("Could not reach a TGK server at this address.", _server);
        else if (!info.RegistrationOpen && _invite.Text.Trim().Length == 0)
            SetError("This server only accepts new accounts with an invite code from its admin.", _invite);
        else
            StartAuthenticator(forRegistration: true);
    }

    /// <summary>Shows the authenticator step with a QR code for a new secret (kept when going back and forth for the same user).</summary>
    private void StartAuthenticator(bool forRegistration)
    {
        _enrollForRegistration = forRegistration;
        string user = _user.Text.Trim();
        if (_enrollment is null || !string.Equals(_enrollmentUser, user, StringComparison.OrdinalIgnoreCase))
        {
            _enrollment = Services.Vault.BeginTotpEnrollment(user);
            _enrollmentUser = user;
        }
        _qr.Content = _enrollment.OtpAuthUri;
        _key.Secret = _enrollment.Secret;
        _setupCode.Text = "";
        ShowStep(Step.Authenticator);
    }

    private async void FinishAuthenticator()
    {
        if (_enrollment is not { } enrollment)
            return;
        string code = _setupCode.Text;
        if (code.Length != CodeLength)
        {
            SetError("Enter the 6-digit code shown in your authenticator app.", _setupCode);
            return;
        }

        string server = _server.Text.Trim(), user = _user.Text.Trim(), password = _password.Text;
        SetBusy(true);
        LoginResult result = _enrollForRegistration
            ? await Services.Vault.RegisterAsync(server, user, password, _invite.Text, enrollment.Secret, code, _keep.Checked)
            : await Services.Vault.CompleteTotpSetupAsync(server, user, password, enrollment.Secret, code, _keep.Checked);
        SetBusy(false);
        if (result.Success)
        {
            Complete();
            return;
        }
        string error = result.Error ?? "Something went wrong.";
        switch (result.ErrorCode)
        {
            case VaultError.TotpRequired or VaultError.TotpInvalid:
                SetError(error, _setupCode);
                break;
            case VaultError.UsernameTaken when _enrollForRegistration:
                ShowStep(Step.Register);
                SetError(error, _user);
                break;
            case VaultError.InviteInvalid or VaultError.RegistrationClosed when _enrollForRegistration:
                ShowStep(Step.Register);
                SetError(error, _invite);
                break;
            case VaultError.InvalidCredentials when !_enrollForRegistration:
                ShowStep(Step.SignIn);
                SetError(error, _password);
                break;
            default:
                SetError(error);
                break;
        }
    }

    private async void CreateLocal()
    {
        if (_master.Text.Length < LocalVaultService.MinPasswordLength)
        {
            SetError($"Use at least {LocalVaultService.MinPasswordLength} characters for the master password.", _master);
            return;
        }
        if (_masterConfirm.Text != _master.Text)
        {
            SetError("The passwords don't match.", _masterConfirm);
            return;
        }
        SetBusy(true);
        LoginResult result = await Services.Vault.CreateLocalAsync(_master.Text, _keepUnlocked.Checked);
        SetBusy(false);
        if (result.Success)
            Complete();
        else if (Services.Vault.Local.Exists)
        {
            ShowStep(Step.LocalUnlock); // created meanwhile (another TGK window)
            SetError(result.Error ?? "This device already has a local vault.");
        }
        else
            SetError(result.Error ?? "Could not create the local vault.", result.ErrorCode == VaultError.ValidationFailed ? _master : null);
    }

    private async void UnlockLocal()
    {
        if (_master.Text.Length == 0)
        {
            SetError("Enter your master password.", _master);
            return;
        }
        SetBusy(true);
        LoginResult result = await Services.Vault.UnlockLocalAsync(_master.Text, _keepUnlocked.Checked);
        SetBusy(false);
        if (result.Success)
            Complete();
        else if (result.ErrorCode == VaultError.NotFound)
        {
            ShowStep(Step.LocalCreate);
            SetError(result.Error ?? "This device has no local vault yet.");
        }
        else
            SetError(result.Error ?? "Could not unlock the local vault.", result.ErrorCode == VaultError.InvalidCredentials ? _master : null);
    }

    /// <summary>The way out of a forgotten master password: delete the vault (after a warning) and start over.</summary>
    private async void DeleteLocalVault()
    {
        if (!await ConfirmDialog.ShowAsync(this, "Delete the local vault?",
                "Without its master password nobody can read the hosts and keys in it. Deleting it lets you start over with an empty vault. This can't be undone.",
                "Delete vault", danger: true))
            return;
        SetBusy(true);
        _primary.Text = "Deleting…";
        try
        {
            await Task.Run(Services.Vault.Local.DeleteVault); // may wait on the keyring
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetBusy(false);
            SetError($"Could not delete the local vault: {ex.Message}");
            return;
        }
        SetBusy(false);
        ShowStep(Step.LocalCreate);
        ShowToast("Local vault deleted", ToastKind.Success);
    }

    private void Complete()
    {
        bool local = IsLocalStep;
        string server = _server.Text.Trim(), user = _user.Text.Trim();
        bool keep = _keep.Checked, keepUnlocked = _keepUnlocked.Checked;
        Services.UpdatePrefs(p =>
        {
            p.Mode = local ? VaultMode.Local : VaultMode.Server;
            if (local)
            {
                p.KeepLocalUnlocked = keepUnlocked;
                return;
            }
            p.LastServer = server;
            p.LastUsername = user;
            p.KeepSignedIn = keep;
        });
        ClearSecrets();
        App.ShowMain();
    }

    private bool Require(TextField field, string message)
    {
        if (field.Text.Trim().Length > 0)
            return false;
        SetError(message, field);
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _primary.Text = (_step, busy) switch
        {
            (Step.SignIn, false) => "Sign in",
            (Step.SignIn, true) => "Signing in…",
            (Step.Register, false) => "Continue",
            (Step.Register, true) => "Checking server…",
            (Step.LocalCreate, false) => "Create local vault",
            (Step.LocalCreate, true) => "Creating vault…",
            (Step.LocalUnlock, false) => "Unlock",
            (Step.LocalUnlock, true) => "Unlocking…",
            (_, false) => _enrollForRegistration ? "Create account" : "Verify and sign in",
            _ => _enrollForRegistration ? "Creating account…" : "Signing in…",
        };
        _primary.Enabled = !busy;
        foreach (Control control in new Control[] { _server, _user, _password, _confirm, _invite, _code, _setupCode, _keep, _key, _mode, _master, _masterConfirm, _keepUnlocked })
            control.Enabled = !busy;
        UpdateSwitchRow();
    }

    /// <summary>Shows <paramref name="message"/> above the button (null clears it) and marks, focuses and selects <paramref name="field"/>.</summary>
    private void SetError(string? message, TextField? field = null)
    {
        if (message is null && !_error.Visible)
            return;
        _error.Text = message ?? "";
        _error.Visible = message is not null;
        foreach (TextField f in Fields)
            f.HasError = message is not null && f == field;
        if (field is not null)
        {
            field.Focus();
            field.SelectAll();
        }
        _root.InvalidateLayout();
    }

    // ---- layout ----

    private void Layout(float w, float h)
    {
        // Small windows drop the brand mark so the whole card stays visible.
        float cardH = LayoutCard(compact: false);
        bool compact = h < cardH + 90;
        if (compact)
            cardH = LayoutCard(compact: true);
        float cardX = MathF.Round((w - CardW) / 2f);
        float cardY = MathF.Round(Math.Max(16, (h - cardH - 36) / 2f));
        _card.Transform.SetLocalFrame(cardX, cardY, CardW, cardH);
        LayoutCard(compact); // again, relative to the card's final position
        _footer.Transform.SetLocalFrame(0, cardY + cardH + 18, w, 18);
    }

    /// <summary>Places the card's children for the current step (card-local) and returns the card height.</summary>
    private float LayoutCard(bool compact)
    {
        const float x = CardPad;
        bool brand = _step != Step.Authenticator && !compact;
        _card.ShowBrand = brand;
        float y = brand ? 88 : 28;
        _title.Transform.SetLocalFrame(x, y, Inner, 28);
        y += 30;
        // The tagline goes first when space is short and the mode switch is shown.
        _subtitle.Visible = !(compact && _mode.Visible);
        if (_subtitle.Visible)
        {
            float subtitleH = _subtitle.MeasureHeight(Inner);
            _subtitle.Transform.SetLocalFrame(x, y, Inner, subtitleH);
            y += subtitleH;
        }
        y += 22;
        if (_mode.Visible)
        {
            _mode.Transform.SetLocalFrame(x, y, Inner, 34);
            y += 34 + 20;
        }
        if (_notice.Visible)
        {
            float noticeH = _notice.MeasureHeight(Inner);
            _notice.Transform.SetLocalFrame(x, y, Inner, noticeH);
            y += noticeH + 16;
        }

        switch (_step)
        {
            case Step.SignIn:
                y = Row(_serverCaption, _server, y);
                y = Row(_userCaption, _user, y);
                y = Row(_passwordCaption, _password, y);
                y = Row(_codeCaption, _code, y);
                y = Check(_keep, y);
                break;
            case Step.Register:
                y = Row(_serverCaption, _server, y);
                y = Hint(_userHint, Row(_userCaption, _user, y));
                y = Hint(_meter, Row(_passwordCaption, _password, y));
                y = Row(_confirmCaption, _confirm, y);
                y = Row(_inviteCaption, _invite, y);
                y = Check(_keep, y);
                break;
            case Step.LocalCreate or Step.LocalUnlock:
                float noteH = _localNote.MeasureHeight(Inner);
                _localNote.Transform.SetLocalFrame(x, y - 4, Inner, noteH);
                y += noteH + 12;
                y = Row(_masterCaption, _master, y);
                if (_step == Step.LocalCreate)
                {
                    y = Hint(_masterMeter, y);
                    y = Row(_masterConfirmCaption, _masterConfirm, y);
                }
                y = Check(_keepUnlocked, y);
                break;
            default:
                _qr.Transform.SetLocalFrame(MathF.Round((CardW - QrSize) / 2f), y, QrSize, QrSize);
                y += QrSize + 18;
                _keyCaption.Transform.SetLocalFrame(x, y, Inner, 16);
                y += 22;
                _key.Transform.SetLocalFrame(x, y, Inner, FieldH);
                y += FieldH + RowGap + 4;
                y = Row(_setupCodeCaption, _setupCode, y) + 6;
                break;
        }

        if (_error.Visible)
        {
            float errorH = _error.MeasureHeight(Inner);
            _error.Transform.SetLocalFrame(x, y - 4, Inner, errorH);
            y += errorH + 8;
        }
        _primary.Transform.SetLocalFrame(x, y, Inner, 40);
        y += 40 + 16;

        float promptW = _switchPrompt.Text.Length == 0 ? 0 : _switchPrompt.MeasureWidth() + 5, linkW = _switchLink.PreferredWidth;
        float sx = MathF.Round((CardW - promptW - linkW) / 2f);
        _switchPrompt.Transform.SetLocalFrame(sx, y, promptW, 18);
        _switchLink.Transform.SetLocalFrame(sx + promptW, y, linkW, 18);
        return y + 18 + CardPad - 6;
    }

    private static float Row(Label caption, TextField field, float y) => Form.Place(caption, field, CardPad, y, Inner, FieldH) + RowGap;

    /// <summary>A one-line hint tucked under the row that ends at <paramref name="y"/> (which already includes the row gap).</summary>
    private static float Hint(VisualElement hint, float y)
    {
        hint.Transform.SetLocalFrame(CardPad, y - RowGap + 4, Inner, 16);
        return y + 20;
    }

    private static float Check(Checkbox box, float y)
    {
        box.Transform.SetLocalFrame(CardPad, y + 2, box.PreferredWidth, 20);
        return y + 20 + 18;
    }

    /// <summary>Full-window background (subtle accent glow) that also handles Enter and Escape for the form.</summary>
    private sealed class LoginRoot(LoginView view) : Control, IKeyInput
    {
        protected override void LayoutChildren() => view.Layout(W, H);

        public bool OnKey(KeyStroke k)
        {
            if (k.IsRepeat)
                return false;
            if (k.IsEnter)
            {
                view.Submit();
                return true;
            }
            if (k.Is(Key.Escape) && view._step is Step.Register or Step.Authenticator)
            {
                view.SwitchStep();
                return true;
            }
            return false;
        }

        public void OnText(string text) { }

        protected override void Paint(SKCanvas c)
        {
            Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.AppBg);
            using var glow = new SKPaint
            {
                IsAntialias = true,
                IsDither = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(W / 2f, H * 0.18f), Math.Max(W, H) * 0.6f,
                    [Theme.Accent.WithAlpha(34), Theme.Accent.WithAlpha(0)], SKShaderTileMode.Clamp),
            };
            c.DrawRect(new SKRect(0, 0, W, H), glow);
            var card = view._card.Transform.Computed;
            var rect = SKRect.Create(card.X - Transform.Computed.X, card.Y - Transform.Computed.Y, card.Width, card.Height);
            Gfx.Shadow(c, rect, 14, 40, 14, SKColors.Black.WithAlpha(120));
        }
    }

    /// <summary>The card surface, with the brand mark above the title on the sign-in and registration steps.</summary>
    private sealed class Card : Control
    {
        private bool _showBrand;

        public bool ShowBrand { get => _showBrand; set => SetAndPaint(ref _showBrand, value); }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, 14, Theme.Overlay);
            Gfx.StrokeRound(c, r, 14, Theme.BorderStrong);
            if (_showBrand)
                BrandMark.Draw(c, W / 2f - 24, 56, 48);
        }
    }

    /// <summary>Amber box for "You were signed out: …".</summary>
    private sealed class Notice : Control
    {
        private const float PadY = 10, TextX = 34, LineH = 17;
        private string _text = "";

        public new string Text { get => _text; set => SetAndPaint(ref _text, value ?? ""); }

        public float MeasureHeight(float width) => Lines(width).Count * LineH + 2 * PadY;

        private List<string> Lines(float width) => Gfx.Wrap(_text, Gfx.Font(Theme.FontSm), width - TextX - 12, 4);

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.Radius, Theme.Warning.WithAlpha(26));
            Gfx.StrokeRound(c, r, Theme.Radius, Theme.Warning.WithAlpha(90));
            float y = PadY + LineH / 2f;
            Icons.Draw(c, "alert", 18, y, 16, Theme.Warning);
            foreach (string line in Lines(W))
            {
                Gfx.Text(c, line, TextX, y, Theme.FontSm, Theme.WeightRegular, Theme.TextPrimary);
                y += LineH;
            }
        }
    }

    /// <summary>Accent text that acts like a hyperlink ("Create an account", "Back").</summary>
    private sealed class LinkButton : Control
    {
        private string _text = "";

        public LinkButton()
        {
            Cursor = StandardCursor.Hand;
            Events.OnClick += (_, e) =>
            {
                e.Handled = true;
                Clicked?.Invoke();
            };
        }

        public event Action? Clicked;

        public new string Text { get => _text; set => SetAndPaint(ref _text, value); }

        public float PreferredWidth => MathF.Ceiling(Gfx.Measure(_text, Theme.FontSm, Theme.WeightSemibold));

        protected override void Paint(SKCanvas c)
        {
            SKColor color = !Enabled ? Theme.TextDisabled : IsHovered ? Theme.AccentHover : Theme.Accent;
            Gfx.Text(c, _text, 0, H / 2f, Theme.FontSm, Theme.WeightSemibold, color);
            if (IsHovered && Enabled)
                Gfx.Line(c, 0, H / 2f + 8, W, H / 2f + 8, color);
        }
    }
}
