using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Ssh;

namespace TGK.Client.Dialogs;

/// <summary>
/// Trust-on-first-use prompt for a server host key. An unknown key offers "Trust &amp; connect" (Enter works once the
/// dialog has been open for a moment); a CHANGED key shows a red warning, both fingerprints, and disables Enter.
/// </summary>
public sealed class HostKeyDialog : DialogBase
{
    private readonly HostKeyInfo _info;
    private readonly Banner _banner;
    private readonly Label _message;
    private readonly Label _typeCaption, _typeValue, _newCaption, _newValue;
    private readonly Label? _oldCaption, _oldValue;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancelRegistration;

    private HostKeyDialog(TgkView view, HostKeyInfo info)
        : base(view, info.IsChanged ? "Host key has changed" : "Unknown host key", 540)
    {
        _info = info;
        string target = info.Port == 22 ? info.Host : $"{info.Host}:{info.Port}";
        Subtitle = target;
        _banner = AddBody(new Banner(info.IsChanged));
        _message = AddBody(new Label(info.IsChanged
            ? $"The key presented by {target} does not match the one you trusted before. Someone may be intercepting the connection (man-in-the-middle), or the server was reinstalled. Only continue if you have verified the new fingerprint."
            : $"The authenticity of {target} can't be established because this is the first connection. Verify the fingerprint with the server's administrator before trusting it.",
            Theme.FontBase, Theme.TextSecondary) { MaxLines = 6 });
        _typeCaption = AddBody(Form.Caption("Key type"));
        _typeValue = AddBody(new Label(info.KeyType, Theme.FontBase, Theme.TextPrimary) { Mono = true });
        _newCaption = AddBody(Form.Caption(info.IsChanged ? "New fingerprint" : "Fingerprint"));
        _newValue = AddBody(new Label(info.FingerprintSha256, Theme.FontBase, info.IsChanged ? Theme.Danger : Theme.TextPrimary) { Mono = true, MaxLines = 2 });
        if (info.IsChanged)
        {
            _oldCaption = AddBody(Form.Caption("Previously trusted"));
            _oldValue = AddBody(new Label(info.KnownFingerprint ?? "", Theme.FontBase, Theme.TextMuted) { Mono = true, MaxLines = 2 });
            EnterAccepts = false;
            AddLeftButton("Trust new key", ButtonVariant.Danger, Trust);
            AddButton("Cancel", ButtonVariant.Primary, Cancel);
        }
        else
        {
            AddButton("Cancel", ButtonVariant.Secondary, Cancel);
            AddButton("Trust & connect", ButtonVariant.Primary, Trust);
            // The prompt takes the focus from wherever the user is typing (e.g. another tab): a stray Enter must
            // not trust a key nobody has looked at.
            EnterGuardMs = PromptDialog.UnsolicitedEnterGuardMs;
        }
    }

    /// <summary>
    /// Asks whether to trust <paramref name="info"/>. Call on the UI thread (from the SSH worker use
    /// <see cref="UiThread.InvokeAsync{T}"/>). Completes with true only when the user trusts the key; cancelling
    /// <paramref name="ct"/> closes the dialog with false.
    /// </summary>
    public static Task<bool> ShowAsync(TgkView view, HostKeyInfo info, CancellationToken ct = default)
    {
        var dialog = new HostKeyDialog(view, info);
        dialog.Open();
        if (ct.CanBeCanceled)
            dialog._cancelRegistration = ct.Register(() => UiThread.Post(dialog.Cancel));
        return dialog._result.Task;
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        _banner.Transform.SetLocalFrame(left, y, width, Banner.Height);
        y += Banner.Height + 14;
        float mh = _message.MeasureHeight(width);
        _message.Transform.SetLocalFrame(left, y, width, mh);
        y += mh + 16;
        y = Row(_typeCaption, _typeValue, left, y, width);
        y = Row(_newCaption, _newValue, left, y, width);
        if (_oldCaption is not null && _oldValue is not null)
            y = Row(_oldCaption, _oldValue, left, y, width);
        return y - top - 12; // no gap after the last row
    }

    private static float Row(Label caption, Label value, float x, float y, float w)
    {
        caption.Transform.SetLocalFrame(x, y, w, Form.CaptionH);
        float vh = value.MeasureHeight(w);
        value.Transform.SetLocalFrame(x, y + Form.CaptionH + 4, w, vh);
        return y + Form.CaptionH + 4 + vh + 12;
    }

    // Enter: trust an unknown key; for a changed key Enter is disabled and the focused default is Cancel.
    protected override void Accept() => Trust();

    private void Trust()
    {
        _result.TrySetResult(true);
        Close();
    }

    protected override void OnClosed()
    {
        _cancelRegistration.Dispose();
        _result.TrySetResult(false);
    }

    /// <summary>Colored notice at the top of the dialog (red for a changed key).</summary>
    private sealed class Banner(bool danger) : Control
    {
        public const float Height = 40;

        private string Message => danger ? "WARNING: possible man-in-the-middle attack" : "First connection to this server";

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            SKColor tint = danger ? Theme.Danger : Theme.Accent;
            Gfx.FillRound(c, r, Theme.Radius, tint.WithAlpha(30));
            Gfx.StrokeRound(c, r, Theme.Radius, tint.WithAlpha(90));
            Icons.Draw(c, danger ? "alert" : "shield", 22, H / 2f, 18, tint);
            Gfx.Text(c, Message, 40, H / 2f, Theme.FontBase, Theme.WeightSemibold, danger ? Theme.Danger : Theme.TextPrimary, TextAlignment.Left, W - 50);
        }
    }
}
