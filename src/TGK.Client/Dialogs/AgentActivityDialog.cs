using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using TGK.Client.Agents;
using TGK.Client.Controls;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Agents;

namespace TGK.Client.Dialogs;

/// <summary>
/// What agents did, newest first: each call with its time, agent, host, tool, command or path, and outcome (denied
/// calls stand out). Updates live. The same record goes to the audit log file.
/// </summary>
public sealed class AgentActivityDialog : DialogBase
{
    private readonly AgentService _agents;
    private readonly Label _status, _empty;
    private readonly FormPage _page;
    private readonly ActivityList _list;

    public AgentActivityDialog(TgkView view) : base(view, "Agent activity", 820)
    {
        _agents = view.App.Agents;
        _status = AddBody(new Label("", Theme.FontSm, Theme.TextSecondary) { MaxLines = 3 });
        _empty = AddBody(new Label("Nothing yet. Calls from agents appear here as they happen.", Theme.FontBase, Theme.TextMuted));
        _page = AddBody(new FormPage());
        _list = _page.Add(new ActivityList());
        _page.Layout = w =>
        {
            _list.Transform.SetLocalFrame(0, 0, w, _list.ContentHeight);
            return _list.ContentHeight;
        };
        AddLeftButton("Open log folder", ButtonVariant.Ghost, () =>
        {
            if (_agents.Activity.LogPath is { } log && !ExternalLink.OpenFolder(Path.GetDirectoryName(log)!))
                View.ShowToast($"The audit log is {log}.", ToastKind.Info);
        });
        AddButton("Show as tab", ButtonVariant.Secondary, () =>
        {
            Close();
            (View as MainView)?.ShowAgentLog();
        });
        AddButton("Disconnect agents", ButtonVariant.Secondary, () =>
        {
            _agents.DisconnectAll();
            View.ShowToast("Agents disconnected. They can connect again while agents are allowed.", ToastKind.Info);
        });
        AddButton("Close", ButtonVariant.Primary, Close);
        _agents.Changed += Refresh;
        Refresh();
    }

    protected override void Accept() => Close();

    protected override void OnClosed() => _agents.Changed -= Refresh;

    private void Refresh()
    {
        IReadOnlyList<AgentSession> sessions = _agents.Sessions;
        int open = _agents.Toolbox.Pool.OpenHosts.Count;
        _status.Text = !_agents.IsRunning
            ? _agents.Error is { } error ? $"Agents are not served: {error}" : "Agents are off on this device (Settings → Agents)."
            : sessions.Count == 0 ? $"Waiting for agents. {Connections(open)}"
            : $"Connected: {string.Join(", ", sessions.Select(s => s.Client).Distinct())}. {Connections(open)}";
        IReadOnlyList<AgentActivityEntry> entries = _agents.Activity.Recent;
        _list.Entries = entries;
        _empty.Visible = entries.Count == 0;
        _page.Visible = entries.Count > 0;
        InvalidateLayout();
    }

    private static string Connections(int open) => open switch
    {
        0 => "No open connections.",
        1 => "1 open connection.",
        _ => $"{open} open connections.",
    };

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        float sh = _status.MeasureHeight(width);
        _status.Transform.SetLocalFrame(left, y, width, sh);
        y += sh + 12;
        if (_empty.Visible)
        {
            _empty.Transform.SetLocalFrame(left, y, width, 40);
            return y + 40 - top;
        }
        float h = Math.Min(Math.Max(120, MaxBodyHeight - (y - top)), Math.Min(440, _list.ContentHeight + 2));
        _page.Transform.SetLocalFrame(left, y, width, h);
        _page.Arrange();
        return y + h - top;
    }

    /// <summary>One row per call: time · agent · host · tool, then the subject and outcome.</summary>
    private sealed class ActivityList : Control
    {
        private const float RowH = 44;
        private IReadOnlyList<AgentActivityEntry> _entries = [];

        public IReadOnlyList<AgentActivityEntry> Entries
        {
            get => _entries;
            set
            {
                _entries = value;
                InvalidatePaint();
            }
        }

        public float ContentHeight => _entries.Count * RowH;

        protected override void Paint(SKCanvas c)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                AgentActivityEntry e = _entries[i];
                float y = i * RowH;
                if (i > 0)
                    Gfx.Line(c, 0, y + 0.5f, W, y + 0.5f, Theme.Border);
                SKColor mark = e.Outcome switch
                {
                    AgentOutcome.Denied => Theme.Warning,
                    AgentOutcome.Failed => Theme.Danger,
                    _ => e.ApprovedBy is "user" or "session" ? Theme.Accent : Theme.Success,
                };
                Gfx.Circle(c, 8, y + 14, 3.5f, mark);
                string head = $"{e.Time.ToLocalTime():HH:mm:ss}  ·  {e.Host ?? "-"}  ·  {e.Tool}";
                Gfx.Text(c, head, 20, y + 14, Theme.FontSm, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Left, W * 0.6f);
                string right = $"{e.Client}  ·  {Outcome(e)}  ·  {e.Duration.TotalSeconds:0.0} s";
                Gfx.Text(c, right, W - 4, y + 14, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right, W * 0.38f);
                string detail = e.Outcome == AgentOutcome.Ok || e.Detail is null ? e.Summary : $"{e.Summary}  —  {e.Detail}";
                Gfx.Text(c, detail, 20, y + 32, Gfx.Font(12, Theme.Mono), e.Outcome == AgentOutcome.Ok ? Theme.TextSecondary : mark, TextAlignment.Left, W - 24);
            }
        }

        private static string Outcome(AgentActivityEntry e) => e.Outcome switch
        {
            AgentOutcome.Denied => "denied",
            AgentOutcome.Failed => "failed",
            _ => e.ApprovedBy switch
            {
                "user" => "approved",
                "session" => "approved for the session",
                _ => e.Detail ?? "ok",
            },
        };
    }
}
