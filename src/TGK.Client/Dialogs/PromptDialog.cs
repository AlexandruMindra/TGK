using System.Threading;
using System.Threading.Tasks;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Ssh;

namespace TGK.Client.Dialogs;

/// <summary>Asks for one line of text (optionally masked). Completes with the text, or null when cancelled.</summary>
public sealed class PromptDialog : DialogBase
{
    /// <summary>How long the keyboard must be quiet before Enter accepts a prompt that took the focus from elsewhere.</summary>
    public const int UnsolicitedEnterGuardMs = 1000;

    private readonly Label _message;
    private readonly Label _caption;
    private readonly TextField _field;
    private readonly Label _error;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _allowEmpty;
    private CancellationTokenRegistration _cancelRegistration;

    /// <param name="guardEnter">
    /// The prompt appears while the user may be typing elsewhere (e.g. in another tab): Enter only accepts once the
    /// keyboard has been quiet for <see cref="UnsolicitedEnterGuardMs"/>, so an Enter meant for that place can't send
    /// what was typed there.
    /// </param>
    public PromptDialog(TgkView view, string title, string? subtitle, string? message, string caption, string okText,
        bool isPassword = false, string? error = null, string initialText = "", bool allowEmpty = false, bool guardEnter = false)
        : base(view, title, 440)
    {
        Subtitle = subtitle;
        _allowEmpty = allowEmpty;
        EnterGuardMs = guardEnter ? UnsolicitedEnterGuardMs : 0;
        _message = AddBody(new Label(message ?? "", Theme.FontBase, Theme.TextSecondary) { MaxLines = 6, Visible = message is not null });
        _caption = AddBody(Form.Caption(caption));
        _field = AddBody(new TextField { IsPassword = isPassword, Text = initialText });
        _error = AddBody(new Label(error ?? "", Theme.FontSm, Theme.Danger) { MaxLines = 3, Visible = error is not null });
        _field.HasError = error is not null;
        _field.Changed += _ => _field.HasError = false;
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton(okText, ButtonVariant.Primary, Accept);
    }

    /// <summary>Shows the dialog; <paramref name="ct"/> closes it (result null) when cancelled.</summary>
    public Task<string?> ShowAsync(CancellationToken ct = default)
    {
        Open();
        if (ct.CanBeCanceled)
            _cancelRegistration = ct.Register(() => UiThread.Post(Cancel));
        return _result.Task;
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        if (_message.Visible)
        {
            float mh = _message.MeasureHeight(width);
            _message.Transform.SetLocalFrame(left, y, width, mh);
            y += mh + 14;
        }
        y = Form.Place(_caption, _field, left, y, width);
        if (_error.Visible)
        {
            float eh = _error.MeasureHeight(width);
            _error.Transform.SetLocalFrame(left, y + 8, width, eh);
            y += 8 + eh;
        }
        return y - top;
    }

    protected override void Accept()
    {
        if (_field.Text.Length == 0 && !_allowEmpty)
        {
            _field.HasError = true;
            _field.Focus();
            return;
        }
        _result.TrySetResult(_field.Text);
        Close();
    }

    protected override void OnClosed()
    {
        _cancelRegistration.Dispose();
        _result.TrySetResult(null);
    }
}

/// <summary>The password prompt shown before connecting when a host has no stored password or key.</summary>
public static class PasswordPromptDialog
{
    /// <summary>
    /// Asks for the password for <paramref name="target"/> (e.g. <c>deploy@web-01:22</c>). Must be called on the UI
    /// thread (from a worker, wrap it in <see cref="UiThread.InvokeAsync{T}"/>). Completes with the password, or null
    /// when the user cancels or <paramref name="ct"/> is cancelled. Pass <paramref name="error"/> to re-ask after a failure,
    /// and <paramref name="guardEnter"/> when it opens while the user may be typing elsewhere (see <see cref="PromptDialog"/>).
    /// </summary>
    public static Task<string?> ShowAsync(TgkView view, string target, string? error = null, bool guardEnter = false, CancellationToken ct = default) =>
        new PromptDialog(view, "Password required", target, null, "Password", "Connect", isPassword: true, error: error, guardEnter: guardEnter)
            .ShowAsync(ct);
}

/// <summary>A question the server asks while signing in (keyboard-interactive), e.g. a one-time code.</summary>
public static class SignInPromptDialog
{
    /// <summary>
    /// Shows <paramref name="prompt"/> for <paramref name="target"/>; a secret (non-echoed) answer is masked. Must be
    /// called on the UI thread. Completes with the answer (possibly empty), or null when cancelled. For
    /// <paramref name="guardEnter"/> see <see cref="PromptDialog"/>.
    /// </summary>
    public static Task<string?> ShowAsync(TgkView view, string target, InteractivePrompt prompt, bool guardEnter = false, CancellationToken ct = default)
    {
        string message = string.IsNullOrWhiteSpace(prompt.Instruction)
            ? "The server asks for more information to finish signing in."
            : prompt.Instruction.Trim();
        string caption = prompt.Prompt.Trim().TrimEnd(':').Trim();
        return new PromptDialog(view, "Sign-in prompt", target, message, caption.Length > 0 ? caption : "Answer", "Continue",
            isPassword: !prompt.Echo, allowEmpty: true, guardEnter: guardEnter).ShowAsync(ct);
    }
}
