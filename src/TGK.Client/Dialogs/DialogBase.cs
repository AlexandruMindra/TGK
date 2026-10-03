using System;
using System.Collections.Generic;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Views;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Dialogs;

/// <summary>
/// Modal dialog: a full-window dimmed backdrop (root element, high ZIndex) with a centered card holding a title,
/// a body laid out by the subclass and right-aligned footer buttons. Escape cancels, Enter accepts, Tab cycles the
/// card's fields; every other key is swallowed while it is open. Dialogs are single-use: create, <see cref="Open"/>,
/// and they remove themselves when closed.
/// </summary>
public abstract class DialogBase : VisualElement, IKeyInput
{
    protected const float Pad = 24;
    private const float HeaderH = 64;
    private const float SubtitleH = 20;
    private const float BodyBottom = 22;
    private const float FooterH = 68;
    private const float ButtonH = 34;

    private readonly Card _card;
    private readonly IconButton _closeButton;
    private readonly List<Button> _footerRight = [];
    private Button? _footerLeft;
    private VisualElement? _previousFocus;
    private long _enterReadyAtMs;
    private bool _closed;

    protected DialogBase(TgkView view, string title, float width)
    {
        View = view;
        DialogWidth = width;
        Name = GetType().Name;
        ReceivesKeyboard = true;
        Style = new ElementStyle();
        _card = new Card(title);
        AddChild(_card);
        _closeButton = new IconButton("x");
        _closeButton.Clicked += Cancel;
        _card.AddChild(_closeButton);
    }

    public TgkView View { get; }

    public float DialogWidth { get; }

    public string Title
    {
        get => _card.Title;
        set => _card.Title = value;
    }

    /// <summary>Optional line under the title.</summary>
    public string? Subtitle
    {
        get => _card.Subtitle;
        set => _card.Subtitle = value;
    }

    /// <summary>When false, Enter does nothing (e.g. to make Cancel the safe default).</summary>
    protected bool EnterAccepts { get; set; } = true;

    /// <summary>
    /// For dialogs that can appear while the user is typing elsewhere: Enter only accepts once the keyboard has been
    /// quiet for this long since the dialog opened (every key pressed before that restarts the wait), so keystrokes
    /// meant for something else can't accept it.
    /// </summary>
    protected int EnterGuardMs { get; set; }

    private float HeaderHeight => Subtitle is null ? HeaderH : HeaderH + SubtitleH;

    /// <summary>The tallest body that keeps the dialog inside the window (with a 16 px margin above and below).</summary>
    protected float MaxBodyHeight => View.Height - 32 - (HeaderHeight + BodyBottom + FooterH);

    /// <summary>Adds a body element to the card; position it in <see cref="LayoutBody"/> with card-local coordinates.</summary>
    protected T AddBody<T>(T element) where T : VisualElement
    {
        _card.AddChild(element);
        return element;
    }

    /// <summary>Adds a right-aligned footer button (added left to right; put the primary action last).</summary>
    protected Button AddButton(string text, ButtonVariant variant, Action onClick)
    {
        var button = new Button(text, variant);
        button.Clicked += onClick;
        _footerRight.Add(button);
        _card.AddChild(button);
        return button;
    }

    /// <summary>Adds the single left-aligned footer button (e.g. "Delete").</summary>
    protected Button AddLeftButton(string text, ButtonVariant variant, Action onClick)
    {
        _footerLeft = new Button(text, variant);
        _footerLeft.Clicked += onClick;
        _card.AddChild(_footerLeft);
        return _footerLeft;
    }

    /// <summary>
    /// Positions the body elements (card-local, via <c>SetLocalFrame</c>) starting at (<paramref name="left"/>, <paramref name="top"/>)
    /// within <paramref name="width"/>, and returns the body height.
    /// </summary>
    protected abstract float LayoutBody(float left, float top, float width);

    /// <summary>Element to focus when the dialog opens (default: the first tab stop).</summary>
    protected virtual VisualElement? InitialFocus => null;

    /// <summary>Enter (when <see cref="EnterAccepts"/>) and the primary button.</summary>
    protected abstract void Accept();

    /// <summary>Escape and the close button. Default: close.</summary>
    protected virtual void Cancel() => Close();

    public void Open()
    {
        _enterReadyAtMs = UiClock.NowMs + EnterGuardMs;
        _previousFocus = View.ActiveKeyboardElement;
        ZIndex = 2000 + 10 * View.PushDialog(this);
        View.AddFullWindow(this);
        View.Menu.Close();
        ForceLayoutSubtree();
        if (InitialFocus is { } initial)
            View.SetActiveKeyboardElement(initial);
        else if (!FocusNavigator.FocusFirst(_card))
            View.SetActiveKeyboardElement(this);
        if (View.ActiveKeyboardElement is IFocusable focusable)
            focusable.OnTabFocus();
    }

    /// <summary>Hides the dialog now and disposes it after the current input event.</summary>
    protected void Close()
    {
        if (_closed)
            return;
        _closed = true;
        View.RemoveDialog(this);
        View.RemoveFullWindow(this);
        Visible = false;
        // Give focus back to where it was, unless that is now covered by another (older) dialog.
        DialogBase? below = View.TopDialog;
        bool canRestore = _previousFocus is { IsDisposed: false, EffectiveVisible: true }
            && (below is null || below == _previousFocus || below.ContainsElement(_previousFocus));
        View.SetActiveKeyboardElement(canRestore ? _previousFocus : below);
        OnClosed();
        Shell.Post(Dispose);
    }

    protected virtual void OnClosed() { }

    public bool IsClosed => _closed;

    public virtual bool OnKey(KeyStroke k)
    {
        long now = UiClock.NowMs;
        bool enterReady = now >= _enterReadyAtMs;
        if (!enterReady)
            _enterReadyAtMs = now + EnterGuardMs; // still typing what was meant for the previous focus
        if (k.IsRepeat && (k.IsEnter || k.Key == Key.Escape))
            return true;
        if (k.Is(Key.Escape))
            Cancel();
        else if (k.IsEnter && (k.Modifiers & ~KeyModifiers.Ctrl) == 0 && EnterAccepts && enterReady)
            Accept();
        else if (k.Key == Key.Tab && (k.Modifiers & ~KeyModifiers.Shift) == 0 && FocusNavigator.Move(_card, k.Shift))
            InvalidateLayout(); // a scrolling page (FormPage) brings the focused field into view when it lays out
        return true; // modal: nothing leaks to the view behind
    }

    public void OnText(string text) { }

    protected override void LayoutChildren()
    {
        float vw = Transform.Computed.Width, vh = Transform.Computed.Height;
        float cw = MathF.Round(Math.Min(DialogWidth, vw - 32));
        float innerW = cw - 2 * Pad;

        // Measure the body with the card at its current position, then place the card and lay out again.
        float bodyH = LayoutBody(Pad, HeaderHeight, innerW);
        float ch = MathF.Round(HeaderHeight + bodyH + BodyBottom + FooterH);
        float cx = MathF.Round(Transform.Computed.X + (vw - cw) / 2f);
        float cy = MathF.Round(Transform.Computed.Y + Math.Max(16, (vh - ch) / 2f - vh * 0.04f));
        _card.Transform.SetAbsoluteFrame(cx, cy, cw, ch);
        LayoutBody(Pad, HeaderHeight, innerW);

        _closeButton.Transform.SetLocalFrame(cw - 16 - 28, 16, 28, 28);
        float bx = cw - Pad, by = ch - FooterH + (FooterH - ButtonH) / 2f;
        for (int i = _footerRight.Count - 1; i >= 0; i--)
        {
            Button b = _footerRight[i];
            float bw = Math.Max(88, b.PreferredWidth);
            bx -= bw;
            b.Transform.SetLocalFrame(bx, by, bw, ButtonH);
            bx -= 8;
        }
        if (_footerLeft is not null)
            _footerLeft.Transform.SetLocalFrame(Pad, by, Math.Max(88, _footerLeft.PreferredWidth), ButtonH);
        _card.FooterTop = ch - FooterH;
    }

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds) => cmds.Add(new DrawCallbackCommand(PaintBackdrop));

    private void PaintBackdrop(SKCanvas c)
    {
        Gfx.FillRect(c, new SKRect(0, 0, Transform.Computed.Width, Transform.Computed.Height), Theme.Scrim);
        var cc = _card.Transform.Computed;
        var card = SKRect.Create(cc.X - Transform.Computed.X, cc.Y - Transform.Computed.Y, cc.Width, cc.Height);
        Gfx.Shadow(c, card, Theme.RadiusLg, 36, 12, SKColors.Black.WithAlpha(140));
    }

    /// <summary>Card surface: background, border, title and the footer divider.</summary>
    private sealed class Card : Control
    {
        private string _title;
        private string? _subtitle;
        private float _footerTop;

        public Card(string title) => _title = title;

        public string Title { get => _title; set => SetAndPaint(ref _title, value); }
        public string? Subtitle { get => _subtitle; set => SetAndPaint(ref _subtitle, value); }
        public float FooterTop { get => _footerTop; set => SetAndPaint(ref _footerTop, value); }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.RadiusLg, Theme.Overlay);
            Gfx.StrokeRound(c, r, Theme.RadiusLg, Theme.BorderStrong);
            Gfx.Text(c, _title, Pad, 36, Theme.FontLg, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Left, W - Pad - 56);
            if (_subtitle is not null)
                Gfx.Text(c, _subtitle, Pad, 58, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, W - 2 * Pad);
            if (_footerTop > 0)
                Gfx.Line(c, 0, _footerTop + 0.5f, W, _footerTop + 0.5f, Theme.Border);
        }
    }
}
