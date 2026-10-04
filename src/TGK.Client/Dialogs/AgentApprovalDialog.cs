using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Agents;

namespace TGK.Client.Dialogs;

/// <summary>
/// An agent asks to do something the host's policy does not allow on its own: run a command, change a file, read a
/// protected path. Shows what exactly (the command, or the diff of the change) and why approval is needed.
/// Allow (once), Allow for this session (the same command, or the same file, again), or Deny. Closing, Escape and
/// no answer in time are a denial.
/// </summary>
public sealed class AgentApprovalDialog : DialogBase
{
    private const float DetailMaxH = 300;
    private readonly Label _action, _reason, _subjectCaption, _subject, _detailCaption, _timeout;
    private readonly FormPage? _detailPage;
    private readonly CodeLines? _detail;
    private readonly TaskCompletionSource<ApprovalAnswer> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancelRegistration;

    private AgentApprovalDialog(TgkView view, ApprovalRequest request, TimeSpan timeout)
        : base(view, "Agent request", request.Detail is null ? 520 : 680)
    {
        Subtitle = $"{request.Client} on {request.Host}";
        _action = AddBody(new Label(request.Action, Theme.FontLg, Theme.TextPrimary) { MaxLines = 1 });
        _reason = AddBody(new Label(request.Reason, Theme.FontSm, Theme.TextMuted) { MaxLines = 3 });
        bool isCommand = request.Action.Contains("command", StringComparison.OrdinalIgnoreCase);
        _subjectCaption = AddBody(Form.Caption(isCommand ? "Command" : "Path"));
        _subject = AddBody(new Label(request.Subject, Theme.FontBase, Theme.TextPrimary) { Mono = true, MaxLines = 6 });
        _detailCaption = AddBody(Form.Caption(request.Action.StartsWith("Create", StringComparison.Ordinal) ? "Content" : "Changes"));
        _detailCaption.Visible = request.Detail is not null;
        if (request.Detail is { } detail)
        {
            _detailPage = AddBody(new FormPage());
            _detail = _detailPage.Add(new CodeLines(detail.TrimEnd('\n').Split('\n')));
            _detailPage.Layout = w =>
            {
                _detail.Transform.SetLocalFrame(0, 0, w, _detail.ContentHeight);
                return _detail.ContentHeight;
            };
        }
        _timeout = AddBody(new Label($"No answer within {timeout.TotalMinutes:0} minutes counts as Deny. \"For this session\" also allows the same {(isCommand ? "command" : "file")} again until the agent disconnects.",
            Theme.FontXs, Theme.TextMuted) { MaxLines = 3 });
        AddLeftButton("Deny", ButtonVariant.Secondary, Cancel);
        AddButton("Allow for this session", ButtonVariant.Secondary, () => Answer(ApprovalAnswer.AllowSession));
        AddButton("Allow", ButtonVariant.Primary, () => Answer(ApprovalAnswer.AllowOnce));
        // It takes the focus from wherever the user is typing: a stray Enter must not approve anything.
        EnterGuardMs = PromptDialog.UnsolicitedEnterGuardMs;
    }

    /// <summary>Asks on the UI thread; completes with the answer (Deny when closed, timed out or <paramref name="ct"/> cancelled).</summary>
    public static Task<ApprovalAnswer> ShowAsync(TgkView view, ApprovalRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var dialog = new AgentApprovalDialog(view, request, timeout);
        dialog.Open();
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        dialog._cancelRegistration = limit.Token.Register(() => UiThread.Post(dialog.Cancel));
        dialog._result.Task.ContinueWith(_ => limit.Dispose(), TaskScheduler.Default);
        return dialog._result.Task;
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        _action.Transform.SetLocalFrame(left, y, width, 24);
        y += 26;
        float rh = _reason.MeasureHeight(width);
        _reason.Transform.SetLocalFrame(left, y, width, rh);
        y += rh + 14;
        _subjectCaption.Transform.SetLocalFrame(left, y, width, Form.CaptionH);
        y += Form.CaptionH + 4;
        float sh = _subject.MeasureHeight(width);
        _subject.Transform.SetLocalFrame(left, y, width, sh);
        y += sh + 14;
        if (_detailPage is not null && _detail is not null)
        {
            _detailCaption.Transform.SetLocalFrame(left, y, width, Form.CaptionH);
            y += Form.CaptionH + 4;
            float available = Math.Max(80, MaxBodyHeight - (y - top) - 60);
            float dh = Math.Min(Math.Min(DetailMaxH, available), _detail.ContentHeight + 2);
            _detailPage.Transform.SetLocalFrame(left, y, width, dh);
            _detailPage.Arrange();
            y += dh + 12;
        }
        float th = _timeout.MeasureHeight(width);
        _timeout.Transform.SetLocalFrame(left, y, width, th);
        return y + th - top;
    }

    protected override void Accept() => Answer(ApprovalAnswer.AllowOnce);

    protected override void Cancel()
    {
        _result.TrySetResult(ApprovalAnswer.Deny);
        Close();
    }

    private void Answer(ApprovalAnswer answer)
    {
        _result.TrySetResult(answer);
        Close();
    }

    protected override void OnClosed()
    {
        _cancelRegistration.Dispose();
        _result.TrySetResult(ApprovalAnswer.Deny);
    }

    /// <summary>Monospaced lines, colored like a diff (+ added, - removed, @@ hunk headers).</summary>
    private sealed class CodeLines(IReadOnlyList<string> lines) : Control
    {
        private const float LineH = 17, PadX = 8, PadY = 6;

        public float ContentHeight => lines.Count * LineH + 2 * PadY;

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.Radius, Theme.TerminalBg);
            Gfx.StrokeRound(c, r, Theme.Radius, Theme.Border);
            SKFont font = Gfx.Font(12.5f, Theme.Mono);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                float y = PadY + i * LineH;
                SKColor color = Theme.TextSecondary;
                if (line.StartsWith('+'))
                {
                    Gfx.FillRect(c, new SKRect(1, y, W - 1, y + LineH), Theme.Success.WithAlpha(28));
                    color = Theme.Success;
                }
                else if (line.StartsWith('-'))
                {
                    Gfx.FillRect(c, new SKRect(1, y, W - 1, y + LineH), Theme.Danger.WithAlpha(28));
                    color = Theme.Danger;
                }
                else if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    color = Theme.Accent;
                }
                Gfx.Text(c, line.Replace("\t", "    "), PadX, y + LineH / 2f, font, color, TextAlignment.Left, W - 2 * PadX);
            }
        }
    }
}
