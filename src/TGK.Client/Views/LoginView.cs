using System;
using Blossom.Core;
using Blossom.Core.Visual;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Core.Services;

namespace TGK.Client.Views;

/// <summary>Sign-in screen: a centered card with server, username and password.</summary>
public sealed class LoginView : TgkView
{
    private const float CardW = 392, CardPad = 32, FieldGap = 14, FieldsTop = 160;
    private LoginRoot _root = null!;
    private Card _card = null!;
    private Label _serverCaption = null!, _userCaption = null!, _passwordCaption = null!, _error = null!, _footer = null!;
    private TextField _server = null!, _user = null!, _password = null!;
    private Checkbox _remember = null!;
    private Button _signIn = null!;
    private bool _busy;

    public LoginView(ClientServices services) : base("Login", services)
    {
    }

    protected override VisualElement? FocusScope => _card;

    // After a click on the background, typing and Enter continue in the form.
    protected override VisualElement? DefaultFocus => FirstEmptyField();

    protected override void Build()
    {
        _root = new LoginRoot(this);
        _card = new Card();
        _root.AddChild(_card);

        _serverCaption = Add(Form.Caption("Server URL"));
        _server = Add(new TextField("https://tgk.example.com"));
        _userCaption = Add(Form.Caption("Username"));
        _user = Add(new TextField("Your username"));
        _passwordCaption = Add(Form.Caption("Password"));
        _password = Add(new TextField("Your password") { IsPassword = true });
        _remember = Add(new Checkbox("Remember server & username"));
        _error = Add(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 2, Visible = false });
        _signIn = Add(new Button("Sign in", ButtonVariant.Primary) { FontSize = Theme.FontMd });
        _signIn.Clicked += Submit;
        _footer = new Label("Development mode · mock server", Theme.FontSm, Theme.TextMuted, align: TextAlignment.Center);
        _root.AddChild(_footer);
        foreach (TextField field in new[] { _server, _user, _password })
            field.Changed += _ => SetError(null);
        AddFullWindow(_root);

        Prefill();
        if (Services.Dev.AutoLogin)
        {
            _server.Text = DevOptions.DevServer;
            _user.Text = DevOptions.DevUser;
            _password.Text = DevOptions.DevPassword;
            UiThread.Post(Submit);
        }
    }

    protected override void OnShown()
    {
        base.OnShown();
        FocusFirstEmpty();
    }

    /// <summary>Back from a sign-out: clear the password and any error.</summary>
    public void Reset()
    {
        SetBusy(false);
        SetError(null);
        _password.Text = "";
        Prefill();
        FocusFirstEmpty();
    }

    private T Add<T>(T element) where T : VisualElement
    {
        _card.AddChild(element);
        return element;
    }

    private void Prefill()
    {
        var prefs = Services.Prefs;
        _remember.Checked = prefs.RememberLogin;
        _server.Text = prefs.RememberLogin && !string.IsNullOrWhiteSpace(prefs.LastServer) ? prefs.LastServer : DevOptions.DevServer;
        _user.Text = prefs.RememberLogin ? prefs.LastUsername ?? "" : "";
    }

    private TextField FirstEmptyField() => _server.Text.Length == 0 ? _server : _user.Text.Length == 0 ? _user : _password;

    private void FocusFirstEmpty() => FirstEmptyField().Focus();

    private async void Submit()
    {
        if (_busy)
            return;
        string server = _server.Text.Trim(), user = _user.Text.Trim(), password = _password.Text;
        TextField? missing = server.Length == 0 ? _server : user.Length == 0 ? _user : password.Length == 0 ? _password : null;
        if (missing is not null)
        {
            missing.HasError = true;
            missing.Focus();
            SetError(missing == _server ? "Enter the server address." : missing == _user ? "Enter your username." : "Enter your password.");
            return;
        }

        SetBusy(true);
        SetError(null);
        LoginResult result = await Services.Vault.LoginAsync(server, user, password);
        SetBusy(false);
        if (!result.Success)
        {
            SetError(result.Error ?? "Sign-in failed.");
            _password.Focus();
            _password.SelectAll();
            return;
        }

        bool remember = _remember.Checked;
        Services.UpdatePrefs(p =>
        {
            p.RememberLogin = remember;
            p.LastServer = remember ? server : null;
            p.LastUsername = remember ? user : null;
        });
        _password.Text = "";
        App.ShowMain();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _signIn.Text = busy ? "Signing in…" : "Sign in";
        _signIn.Enabled = !busy;
        foreach (Control c in new Control[] { _server, _user, _password, _remember })
            c.Enabled = !busy;
    }

    private void SetError(string? message)
    {
        _error.Text = message ?? "";
        _error.Visible = message is not null;
        if (message is null)
        {
            _server.HasError = _user.HasError = _password.HasError = false;
        }
        _root.InvalidateLayout();
    }

    // ---- layout ----

    private void Layout(float w, float h)
    {
        float innerW = CardW - 2 * CardPad;
        float errorH = _error.Visible ? _error.MeasureHeight(innerW) + 10 : 0;
        float cardH = FieldsTop + 3 * Form.RowHeight(36) + 2 * FieldGap + 16 + 20 + 18 + errorH + 40 + CardPad;
        float cardX = MathF.Round((w - CardW) / 2f);
        float cardY = MathF.Round(Math.Max(16, (h - cardH - 36) / 2f));
        _card.Transform.SetLocalFrame(cardX, cardY, CardW, cardH);
        _footer.Transform.SetLocalFrame(0, cardY + cardH + 18, w, 18);

        float y = FieldsTop;
        y = Form.Place(_serverCaption, _server, CardPad, y, innerW, 36) + FieldGap;
        y = Form.Place(_userCaption, _user, CardPad, y, innerW, 36) + FieldGap;
        y = Form.Place(_passwordCaption, _password, CardPad, y, innerW, 36) + 16;
        _remember.Transform.SetLocalFrame(CardPad, y, _remember.PreferredWidth, 20);
        y += 20 + 18;
        if (_error.Visible)
        {
            _error.Transform.SetLocalFrame(CardPad, y - 4, innerW, errorH - 10);
            y += errorH;
        }
        _signIn.Transform.SetLocalFrame(CardPad, y, innerW, 40);
    }

    /// <summary>Full-window background (subtle accent glow) that also handles Enter for the form.</summary>
    private sealed class LoginRoot(LoginView view) : Control, IKeyInput
    {
        protected override void LayoutChildren() => view.Layout(W, H);

        public bool OnKey(KeyStroke k)
        {
            if (k.IsEnter && !k.IsRepeat)
            {
                view.Submit();
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

    /// <summary>The sign-in card: surface, brand mark, name and tagline.</summary>
    private sealed class Card : Control
    {
        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, 14, Theme.Overlay);
            Gfx.StrokeRound(c, r, 14, Theme.BorderStrong);
            BrandMark.Draw(c, W / 2f - 24, 60, 48);
            Gfx.Text(c, "TGK", W / 2f, 106, Theme.FontXl, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Center);
            Gfx.Text(c, "SSH sessions, everywhere", W / 2f, 128, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center);
        }
    }
}
