using System.Threading.Tasks;
using TGK.Client.Controls;
using TGK.Client.Views;

namespace TGK.Client.Dialogs;

/// <summary>Yes/no question. <c>await ConfirmDialog.ShowAsync(...)</c> on the UI thread.</summary>
public sealed class ConfirmDialog : DialogBase
{
    private readonly Label _message;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ConfirmDialog(TgkView view, string title, string message, string confirmText, bool danger)
        : base(view, title, 440)
    {
        _message = AddBody(new Label(message, Theme.FontBase, Theme.TextSecondary) { MaxLines = 8 });
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton(confirmText, danger ? ButtonVariant.Danger : ButtonVariant.Primary, Accept);
    }

    /// <summary>Shows the question; completes with true when confirmed (button or Enter), false otherwise.</summary>
    public static Task<bool> ShowAsync(TgkView view, string title, string message, string confirmText = "OK", bool danger = false)
    {
        var dialog = new ConfirmDialog(view, title, message, confirmText, danger);
        dialog.Open();
        return dialog._result.Task;
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float h = _message.MeasureHeight(width);
        _message.Transform.SetLocalFrame(left, top, width, h);
        return h;
    }

    protected override void Accept()
    {
        _result.TrySetResult(true);
        Close();
    }

    protected override void OnClosed() => _result.TrySetResult(false);
}
