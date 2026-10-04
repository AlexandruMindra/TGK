using System;
using System.Text;
using TGK.Client.Agents;
using TGK.Client.Terminal;
using TGK.Core.Agents;

namespace TGK.Client.Main;

/// <summary>
/// A read-only tab that mirrors what agents do as they do it: each call (who, which host, the command or path, the
/// outcome) followed by what the agent got back, in a terminal. Not saved by "reopen my tabs".
/// </summary>
public sealed class AgentLogTabContent : TabContent
{
    private const int MaxLinesPerCall = 80;
    private TerminalView _terminal = null!;
    private AgentService _agents = null!;

    public override Blossom.Core.Visual.VisualElement? DefaultFocus => _terminal;

    public override void OnAttached()
    {
        SetTitle("Agent log");
        _agents = Host.App.Agents;
        _terminal = new TerminalView(Host.Services.Prefs.Terminal) { InputEnabled = false };
        AddChild(_terminal);
        Write("\u001b[2mWhat agents do on your hosts appears here as it happens, with what they got back. Read only.\u001b[0m\r\n\r\n");
        foreach ((AgentActivityEntry entry, string result) in _agents.Transcript)
            Append(entry, result);
        _agents.CallFinished += Append;
        UpdateStatus();
        _agents.Changed += UpdateStatus;
    }

    public override void OnClosing()
    {
        _agents.CallFinished -= Append;
        _agents.Changed -= UpdateStatus;
    }

    protected override void LayoutChildren()
    {
        _terminal.Transform.SetLocalFrame(0, 0, Transform.Computed.Width, Transform.Computed.Height);
        _terminal.FitToSize();
    }

    private void UpdateStatus()
    {
        int sessions = _agents.Sessions.Count;
        SetStatus(sessions > 0 ? TabStatus.Connected : TabStatus.None,
            !_agents.IsRunning ? "Agent log · agents are off on this device"
            : sessions == 0 ? "Agent log · no agent connected"
            : $"Agent log · {sessions} agent{(sessions == 1 ? "" : "s")} connected");
    }

    private void Append(AgentActivityEntry entry, string result)
    {
        var sb = new StringBuilder();
        string color = entry.Outcome switch { AgentOutcome.Denied => "33", AgentOutcome.Failed => "31", _ => "32" };
        string outcome = entry.Outcome switch
        {
            AgentOutcome.Denied => "denied",
            AgentOutcome.Failed => "failed",
            _ => entry.ApprovedBy is "user" or "session" ? "approved" : "ok",
        };
        sb.Append($"\u001b[2m{entry.Time.ToLocalTime():HH:mm:ss}\u001b[0m \u001b[36m{entry.Client}\u001b[0m → \u001b[1m{entry.Host ?? "-"}\u001b[0m  {entry.Tool}  \u001b[{color}m[{outcome}]\u001b[0m \u001b[2m{entry.Duration.TotalSeconds:0.0} s\u001b[0m\r\n");
        if (entry.Summary.Length > 0)
            sb.Append($"\u001b[33m{(entry.Tool == AgentTools.RunCommand ? "$ " : "▸ ")}{Clean(entry.Summary)}\u001b[0m\r\n");
        string[] lines = result.TrimEnd('\n').Split('\n');
        int shown = Math.Min(lines.Length, MaxLinesPerCall);
        for (int i = 0; i < shown; i++)
            sb.Append("  ").Append(Clean(lines[i])).Append("\r\n");
        if (lines.Length > shown)
            sb.Append($"  \u001b[2m… {lines.Length - shown} more lines\u001b[0m\r\n");
        sb.Append("\r\n");
        Write(sb.ToString());
        RequestAttention();
    }

    // Output is shown as text: escape sequences from the remote side must not drive this terminal.
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c is '\t' ? "    " : char.IsControl(c) ? "" : c.ToString());
        return sb.ToString();
    }

    private void Write(string text) => _terminal.Write(Encoding.UTF8.GetBytes(text));
}
