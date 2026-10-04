using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using TGK.Client.Agents;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Main;

namespace TGK.Client.Views;

// Agents (MCP) in the main window: a status bar chip while agents are allowed on this device (with the number
// connected, accented while a call runs or waits for approval) that opens the activity list.
public sealed partial class MainView
{
    private void RefreshAgentChip()
    {
        if (_torndown)
            return;
        AgentService agents = App.Agents;
        int sessions = agents.Sessions.Count;
        bool busy = agents.Toolbox.Running > 0;
        string? text = !agents.IsRunning ? null
            : sessions > 0 ? $"{sessions} agent{(sessions == 1 ? "" : "s")}"
            : "Agents";
        _status.SetAgents(text, busy);
    }

    /// <summary>Opens the list of what agents did (from the status bar chip or Settings → Agents).</summary>
    public void ShowAgentActivity() => new AgentActivityDialog(this).Open();

    /// <summary>Shows the agent log tab (opening it the first time).</summary>
    public void ShowAgentLog()
    {
        if (_tabs.OfType<AgentLogTabContent>().FirstOrDefault() is { } open)
            ActivateTab(open);
        else
            OpenTab(new AgentLogTabContent());
    }

    private void ShowAgentMenu(SKRect chip)
    {
        AgentService agents = App.Agents;
        int sessions = agents.Sessions.Count;
        var items = new List<MenuItem>
        {
            new() { Text = sessions == 0 ? "Agents are allowed · none connected" : $"Connected: {string.Join(", ", agents.Sessions.Select(s => s.Client).Distinct())}", IsHeader = true },
            MenuItem.Separator,
            new() { Text = "Activity…", Icon = "agent", Action = ShowAgentActivity },
            new() { Text = "Agent log in a tab", Icon = "terminal", Action = ShowAgentLog },
            new() { Text = "Disconnect agents", Icon = "x", IsEnabled = sessions > 0, Action = agents.DisconnectAll },
            MenuItem.Separator,
            new() { Text = "Agent settings…", Icon = "settings", Action = () =>
            {
                var settings = new SettingsDialog(this);
                settings.Open();
                settings.ShowTab(SettingsDialog.AgentsTab);
            } },
        };
        Menu.Show(items, chip.Right - 300, chip.Bottom, 300, chip.Height);
    }
}
