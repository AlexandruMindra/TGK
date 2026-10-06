using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Blossom;
using SkiaSharp;
using TGK.Client.Agents;
using TGK.Client.Controls;
using TGK.Client.Main;
using TGK.Client.Terminal;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// Settings: the terminal's look and behaviour and the connection and session defaults every host inherits, the
/// "reopen my tabs" preference, and agents — whether this device serves them and the default agent access. The vault
/// parts sync to all devices; the update notices, the update channel and "allow agents" are per device
/// (<c>ClientServices.Prefs</c>).
/// </summary>
public sealed class SettingsDialog : TabbedDialog
{
    public const int TerminalTab = 0, ConnectionTab = 1, SessionTab = 2, StartupTab = 3, AgentsTab = 4;
    private const string TerminalSubtitle = "Synced to all your devices. Groups and hosts can override the color scheme and the font.";
    private const string LocalTerminalSubtitle = "Kept in your local vault. Groups and hosts can override the color scheme and the font.";
    private const string SyncedSubtitle = "Connection defaults for all hosts, synced to all your devices. Groups and hosts can override them.";
    private const string LocalVaultSubtitle = "Connection defaults for all hosts, kept in your local vault. Groups and hosts can override them.";
    private const string StartupSubtitle = "Reopening your tabs (synced to all your devices) and update notices (this device).";
    private const string LocalStartupSubtitle = "Reopening your tabs (kept in your local vault) and update notices.";
    private const string AgentsSubtitle = "Agents on this device, and what they may do on hosts that don't say (synced).";
    private const string LocalAgentsSubtitle = "Agents on this device, and what they may do on hosts that don't say.";
    private static readonly int[] ScrollbackSizes = [1_000, 5_000, 10_000, 50_000, 100_000];
    private static readonly string[] CursorShapes = [TerminalSettings.CursorBlock, TerminalSettings.CursorBar, TerminalSettings.CursorUnderline];
    private static readonly TerminalSettings BuiltIn = new();

    private readonly Label _scrollbackCaption, _cursorCaption, _previewCaption;
    private readonly Dropdown _scrollback;
    private readonly SegmentedControl _cursor;
    private readonly Checkbox _blink, _copyOnSelect;
    private readonly TerminalPreview _preview;
    private readonly OptionsEditor _defaults;
    private readonly Checkbox _restoreTabs;
    private readonly Label _restoreHint, _restoreSaved;
    private readonly Checkbox _checkUpdates;
    private readonly Label _updatesHint;
    private readonly Label _channelCaption, _channelHint;
    private readonly SegmentedControl _channel;
    private readonly Checkbox _agentsOn;
    private readonly Label _agentsHint, _agentsStatus, _setupCaption, _setupCommand, _defaultsCaption;
    private readonly Button _copyCommand, _copyJson, _activity;

    public SettingsDialog(TgkView view) : base(view, "Settings", 640, "Terminal", "Connection", "Session", "Startup", "Agents")
    {
        HostOptions saved = view.Services.Vault.Current.Defaults;
        EffectiveHostOptions current = EffectiveOptions.Resolve(null, null, saved);
        Subtitle = view.Services.IsLocal ? LocalTerminalSubtitle : TerminalSubtitle;
        FormPage page = PageAt(TerminalTab);

        FormPage agentsPage = PageAt(AgentsTab);
        _agentsOn = agentsPage.Add(new Checkbox("Allow agents (MCP) on this device", view.Services.Prefs.AgentsEnabled));
        _agentsHint = agentsPage.Add(new Label(
            "Lets Claude Code and other MCP clients on this computer use your saved hosts through TGK while it runs: commands, reading " +
            "and editing files. Passwords and keys never leave TGK. Hosts stay closed to agents unless you open them, below for all " +
            "hosts or in a group's or host's Agents tab; you approve what their access mode does not allow.",
            Theme.FontSm, Theme.TextMuted) { MaxLines = 5 });
        _agentsStatus = agentsPage.Add(new Label("", Theme.FontSm, Theme.TextSecondary) { MaxLines = 2 });
        _setupCaption = agentsPage.Add(Form.Caption("Add TGK to Claude Code (once)"));
        _setupCommand = agentsPage.Add(new Label(AgentService.ClaudeSetupCommand, Theme.FontSm, Theme.TextPrimary) { Mono = true, MaxLines = 3 });
        _copyCommand = agentsPage.Add(new Button("Copy command", ButtonVariant.Secondary, "copy"));
        _copyCommand.Clicked += () =>
        {
            Shell.SetClipboardText(AgentService.ClaudeSetupCommand);
            View.ShowToast("Copied. Run it in a terminal, then start Claude Code.", ToastKind.Success);
        };
        _copyJson = agentsPage.Add(new Button("Copy JSON config", ButtonVariant.Secondary, "clipboard"));
        _copyJson.Clicked += () =>
        {
            Shell.SetClipboardText(AgentService.JsonConfig);
            View.ShowToast("Copied the mcpServers entry (for .mcp.json and other MCP clients).", ToastKind.Success);
        };
        _activity = agentsPage.Add(new Button("Activity…", ButtonVariant.Secondary, "agent"));
        _activity.Clicked += () => (View as MainView)?.ShowAgentActivity();
        _defaultsCaption = agentsPage.Add(new Label("Defaults for all hosts", Theme.FontBase, Theme.TextPrimary, Theme.WeightSemibold));
        RefreshAgentsStatus();

        // Color scheme, font and size: the appearance part of the defaults, shown on the Terminal page; the agent-access
        // defaults are shown on the Agents page under the device settings above.
        _defaults = new OptionsEditor(view, saved, OptionsLevel.Global, () => EffectiveOptions.Resolve((HostOptions?)null, null, null),
            options =>
            {
                VaultData vault = view.Services.Vault.Current.ShallowCopy();
                vault.Defaults = options;
                return vault;
            },
            PageAt(ConnectionTab), PageAt(SessionTab), page, agentsPage, agentsHeader: LayoutAgentsHeader, arrangeAppearance: false);
        _defaults.LayoutChanged += InvalidateLayout;

        _scrollbackCaption = page.Add(Form.Caption("Scrollback"));
        _scrollback = page.Add(new Dropdown { Options = ScrollbackSizes.Select(n => $"{n.ToString("N0", CultureInfo.InvariantCulture)} lines").ToList() });
        _scrollback.SelectedIndex = NearestIndex(ScrollbackSizes.Select(n => (float)n).ToArray(), current.ScrollbackLines.Value);

        _cursorCaption = page.Add(Form.Caption("Cursor style"));
        _cursor = page.Add(new SegmentedControl("Block", "Bar", "Underline"));
        _cursor.SelectedIndex = Math.Max(0, Array.IndexOf(CursorShapes, current.CursorShape.Value));

        _blink = page.Add(new Checkbox("Blinking cursor", current.CursorBlink.Value));
        _copyOnSelect = page.Add(new Checkbox("Copy text on select", current.CopyOnSelect.Value));

        _previewCaption = page.Add(Form.Caption("Preview"));
        _preview = page.Add(new TerminalPreview(this));
        _cursor.SelectionChanged += _ => _preview.InvalidatePaint();
        _defaults.AppearanceChanged += _preview.InvalidatePaint;
        page.Layout = LayoutTerminal;

        FormPage startup = PageAt(StartupTab);
        Workspace workspace = view.Services.Vault.Current.Workspace;
        _restoreTabs = startup.Add(new Checkbox("Reopen my tabs and split views when TGK starts", workspace.RestoreTabs));
        _restoreHint = startup.Add(new Label(view.Services.IsLocal
            ? "Your open tabs and split views are saved in your local vault when you close or lock TGK, and reopened the next time you unlock it. Passwords are never saved: sessions ask again when needed and connect when you open their tab."
            : "Your open tabs and split views are saved in your vault when you close TGK or sign out (and as you work), and reopened on whichever device you sign in next, so you can pick up where you left off. Passwords are never saved: sessions ask again when needed and connect when you open their tab.",
            Theme.FontSm, Theme.TextMuted) { MaxLines = 5 });
        _restoreSaved = startup.Add(new Label(SavedText(workspace), Theme.FontSm, Theme.TextSecondary));
        _checkUpdates = startup.Add(new Checkbox("Tell me when a new version of TGK is available", view.Services.Prefs.CheckForUpdates));
        _updatesHint = startup.Add(new Label($"Checks GitHub's release list in the background, about twice a day, and shows a note in the status bar. This device only. You have {AppInfo.VersionText}.",
            Theme.FontSm, Theme.TextMuted) { MaxLines = 3 });
        _channelCaption = startup.Add(Form.Caption("Update channel"));
        _channel = startup.Add(new SegmentedControl("Stable", "Nightly"));
        UpdateChannel channel = view is MainView m ? m.CurrentChannel : view.Services.Prefs.UpdateChannel ?? UpdateChecker.DefaultChannel(AppInfo.Version);
        _channel.SelectedIndex = channel == UpdateChannel.Nightly ? 1 : 0;
        _channelHint = startup.Add(new Label("", Theme.FontSm, Theme.TextMuted) { MaxLines = 4 });
        _channel.SelectionChanged += _ => UpdateChannelHint();
        UpdateChannelHint();
        startup.Layout = LayoutStartup;

        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton("Save", ButtonVariant.Primary, Accept);
    }

    private UpdateChannel SelectedChannel => _channel.SelectedIndex == 1 ? UpdateChannel.Nightly : UpdateChannel.Stable;

    private void UpdateChannelHint()
    {
        bool onNightlyBuild = AppInfo.Version.IsNightly;
        _channelHint.Text = SelectedChannel == UpdateChannel.Nightly
            ? "Nightly: the latest build of TGK's main branch, rebuilt with every change — new features first, less tested. Releases are offered too when they are newer. This device only."
            : onNightlyBuild
                ? "Stable: releases only. You have a nightly build, which stays until a release newer than it comes out (no downgrade). This device only."
                : "Stable: tested releases only. This device only.";
        InvalidateLayout();
    }

    private string CursorShape => CursorShapes[Math.Clamp(_cursor.SelectedIndex, 0, CursorShapes.Length - 1)];

    private static int NearestIndex(float[] values, float value)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (Math.Abs(values[i] - value) < Math.Abs(values[best] - value))
                best = i;
        }
        return best;
    }

    protected override void OnTabShown(int index)
    {
        bool local = View.Services.IsLocal;
        Subtitle = index == TerminalTab ? (local ? LocalTerminalSubtitle : TerminalSubtitle)
            : index == StartupTab ? (local ? LocalStartupSubtitle : StartupSubtitle)
            : index == AgentsTab ? (local ? LocalAgentsSubtitle : AgentsSubtitle)
            : local ? LocalVaultSubtitle : SyncedSubtitle;
        if (index == AgentsTab)
            RefreshAgentsStatus();
        _defaults.RefreshInherited();
    }

    private void RefreshAgentsStatus()
    {
        AgentService agents = View.App.Agents;
        int sessions = agents.Sessions.Count;
        _agentsStatus.Text = agents.Error is { } error ? $"Not serving agents: {error}"
            : !agents.IsRunning ? "Off: agents can't reach TGK on this device."
            : sessions == 0 ? "On: waiting for agents."
            : $"On: {sessions} agent{(sessions == 1 ? "" : "s")} connected ({string.Join(", ", agents.Sessions.Select(a => a.Client).Distinct())}).";
        _agentsStatus.Color = agents.Error is not null ? Theme.Warning : agents.IsRunning ? Theme.Success : Theme.TextSecondary;
    }

    // The device part of the Agents tab, above the access defaults (OptionsEditor).
    private float LayoutAgentsHeader(float width)
    {
        float y = 0;
        _agentsOn.Transform.SetLocalFrame(0, y, Math.Min(width, _agentsOn.PreferredWidth), 22);
        y += 22 + 8;
        float hintH = _agentsHint.MeasureHeight(width - 28);
        _agentsHint.Transform.SetLocalFrame(28, y, width - 28, hintH);
        y += hintH + 6;
        _agentsStatus.Transform.SetLocalFrame(28, y, width - 28, 18);
        y += 18 + Form.RowGap;
        _setupCaption.Transform.SetLocalFrame(0, y, width, Form.CaptionH);
        y += Form.CaptionH + 6;
        float ch = _setupCommand.MeasureHeight(width);
        _setupCommand.Transform.SetLocalFrame(0, y, width, ch);
        y += ch + 8;
        float x = 0;
        foreach (Button b in new[] { _copyCommand, _copyJson, _activity })
        {
            float bw = Math.Max(88, b.PreferredWidth);
            b.Transform.SetLocalFrame(x, y, bw, 32);
            x += bw + 8;
        }
        y += 32 + Form.RowGap + 8;
        _defaultsCaption.Transform.SetLocalFrame(0, y, width, 20);
        return y + 20 + 10;
    }

    private float LayoutStartup(float width)
    {
        float y = 0;
        _restoreTabs.Transform.SetLocalFrame(0, y, Math.Min(width, _restoreTabs.PreferredWidth), 22);
        y += 22 + 8;
        float hintH = _restoreHint.MeasureHeight(width - 28);
        _restoreHint.Transform.SetLocalFrame(28, y, width - 28, hintH);
        y += hintH + 8;
        _restoreSaved.Visible = _restoreSaved.Text.Length > 0;
        _restoreSaved.Transform.SetLocalFrame(28, y, width - 28, 18);
        y += (_restoreSaved.Visible ? 18 : 0) + Form.RowGap + 4;
        _checkUpdates.Transform.SetLocalFrame(0, y, Math.Min(width, _checkUpdates.PreferredWidth), 22);
        y += 22 + 8;
        float updatesH = _updatesHint.MeasureHeight(width - 28);
        _updatesHint.Transform.SetLocalFrame(28, y, width - 28, updatesH);
        y += updatesH + Form.RowGap;
        y = Form.Place(_channelCaption, _channel, 28, y, Math.Min(260, width - 28)) + 8;
        float channelH = _channelHint.MeasureHeight(width - 28);
        _channelHint.Transform.SetLocalFrame(28, y, width - 28, channelH);
        return y + channelH;
    }

    // "Saved tabs: 5, on laptop, 2h ago" while the preference is on and tabs are stored.
    private static string SavedText(Workspace workspace)
    {
        if (!workspace.RestoreTabs || workspace.Tabs.Count == 0)
            return "";
        string text = $"Last saved: {workspace.Tabs.Count} tab{(workspace.Tabs.Count == 1 ? "" : "s")}";
        if (workspace.SavedOn is { Length: > 0 } device)
            text += $", on {device}";
        if (workspace.SavedAt is { } at)
            text += $", {HostFormat.Ago(at, DateTimeOffset.UtcNow)}";
        return text + ".";
    }

    private float LayoutTerminal(float width)
    {
        float col = MathF.Floor((width - 16) / 2f);
        float y = _defaults.ArrangeAppearance(width, 0) + Form.RowGap;
        Form.Place(_scrollbackCaption, _scrollback, 0, y, col);
        y = Form.Place(_cursorCaption, _cursor, col + 16, y, width - col - 16) + Form.RowGap;
        _blink.Transform.SetLocalFrame(0, y, _blink.PreferredWidth, 22);
        _copyOnSelect.Transform.SetLocalFrame(col + 16, y, _copyOnSelect.PreferredWidth, 22);
        y += 22 + Form.RowGap + 4;
        return Form.Place(_previewCaption, _preview, 0, y, width, 84);
    }

    protected override void Accept()
    {
        IVaultService vault = View.Services.Vault;
        HostOptions defaults = vault.Current.Defaults.Clone();
        if (_defaults.Store(defaults) is { } problem)
        {
            ShowError(problem.Message, problem.Page, problem.Field);
            return;
        }
        // Built-in values are stored as "not set", so the defaults stay empty until something differs.
        int scrollback = ScrollbackSizes[Math.Clamp(_scrollback.SelectedIndex, 0, ScrollbackSizes.Length - 1)];
        defaults.ScrollbackLines = scrollback == BuiltIn.ScrollbackLines ? null : scrollback;
        defaults.CursorShape = CursorShape == BuiltIn.CursorShape ? null : CursorShape;
        defaults.CursorBlink = _blink.Checked == BuiltIn.CursorBlink ? null : _blink.Checked;
        defaults.CopyOnSelect = _copyOnSelect.Checked == BuiltIn.CopyOnSelect ? null : _copyOnSelect.Checked;
        if (JsonSerializer.Serialize(defaults) != JsonSerializer.Serialize(vault.Current.Defaults) && !View.RunVault(() => vault.SaveDefaultsAsync(defaults)))
            return;
        if (View is MainView main)
        {
            if (_restoreTabs.Checked != vault.Current.Workspace.RestoreTabs)
                main.SetRestoreTabs(_restoreTabs.Checked);
            main.SetCheckForUpdates(_checkUpdates.Checked);
            main.SetUpdateChannel(SelectedChannel);
        }
        bool agentsOn = _agentsOn.Checked;
        if (agentsOn != View.Services.Prefs.AgentsEnabled)
            View.Services.UpdatePrefs(p => p.AgentsEnabled = agentsOn);
        if (agentsOn != View.App.Agents.IsRunning)
            _ = View.App.Agents.ApplyAsync(agentsOn); // also retries when serving failed before
        Close();
    }

    /// <summary>A few prompt lines in the chosen color scheme, font, size and cursor shape.</summary>
    private sealed class TerminalPreview(SettingsDialog dialog) : Control
    {
        protected override void Paint(SKCanvas c)
        {
            (string schemeName, string family, float size) = dialog._defaults.Appearance;
            ColorScheme scheme = ColorScheme.Find(schemeName);
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.Radius, scheme.Background);
            Gfx.StrokeRound(c, r, Theme.Radius, Theme.Border);
            int save = c.Save();
            c.ClipRect(SKRect.Inflate(r, -1, -1));
            size = TerminalFonts.DrawSize(family, size);
            SKFont font = Gfx.Font(size, TerminalFonts.Get(family).Regular);
            float lineH = MathF.Ceiling(size * 1.35f);
            float y = 14 + lineH / 2f;
            float x = 14;
            const string prompt = "demo@web-01:~$ ";
            Gfx.Text(c, "Last login: Mon Sep 28 09:14 from 10.0.0.2", x, y, font, scheme.Foreground.WithAlpha(170));
            y += lineH;
            Gfx.Text(c, prompt, x, y, font, scheme.Ansi[10]);
            float cx = x + Gfx.Measure(prompt, font);
            Gfx.Text(c, "ls -la", cx, y, font, scheme.Foreground);
            cx += Gfx.Measure("ls -la", font);
            float cw = Gfx.Measure("M", font);
            float top = y - lineH / 2f + 1, bottom = y + lineH / 2f - 1;
            SKRect cursor = dialog.CursorShape switch
            {
                TerminalSettings.CursorBar => new SKRect(cx + 1, top, cx + 3, bottom),
                TerminalSettings.CursorUnderline => new SKRect(cx, bottom - 2, cx + cw, bottom),
                _ => new SKRect(cx, top, cx + cw, bottom),
            };
            Gfx.FillRect(c, cursor, scheme.Cursor.WithAlpha(220));
            c.RestoreToCount(save);
        }
    }
}
