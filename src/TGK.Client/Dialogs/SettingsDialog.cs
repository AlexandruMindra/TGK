using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Main;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// Settings: terminal preferences for this device (saved in <c>ClientServices.Prefs</c>, raising <c>PrefsChanged</c>),
/// the connection defaults every host inherits and the "reopen my tabs" preference, which live in the vault and sync to
/// all devices.
/// </summary>
public sealed class SettingsDialog : TabbedDialog
{
    public const int TerminalTab = 0, ConnectionTab = 1, SessionTab = 2, AppearanceTab = 3, StartupTab = 4;
    private const string LocalSubtitle = "Terminal preferences for this device only (not synced).";
    private const string SyncedSubtitle = "Connection defaults for all hosts, synced to all your devices. Groups and hosts can override them.";
    private const string LocalVaultSubtitle = "Connection defaults for all hosts, kept in your local vault. Groups and hosts can override them.";
    private const string StartupSubtitle = "Reopening your tabs (synced to all your devices) and update notices (this device).";
    private const string LocalStartupSubtitle = "Reopening your tabs (kept in your local vault) and update notices.";
    private static readonly float[] FontSizes = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
    private static readonly int[] ScrollbackSizes = [1_000, 5_000, 10_000, 50_000, 100_000];
    private static readonly string[] CursorShapes = [TerminalSettings.CursorBlock, TerminalSettings.CursorBar, TerminalSettings.CursorUnderline];

    private readonly TerminalSettings _draft;
    private readonly Label _fontCaption, _scrollbackCaption, _cursorCaption, _previewCaption;
    private readonly Dropdown _fontSize, _scrollback;
    private readonly SegmentedControl _cursor;
    private readonly Checkbox _blink, _copyOnSelect;
    private readonly TerminalPreview _preview;
    private readonly OptionsEditor _defaults;
    private readonly Checkbox _restoreTabs;
    private readonly Label _restoreHint, _restoreSaved;
    private readonly Checkbox _checkUpdates;
    private readonly Label _updatesHint;

    public SettingsDialog(TgkView view) : base(view, "Settings", 640, "Terminal", "Connection", "Session", "Appearance", "Startup")
    {
        _draft = view.Services.Prefs.Terminal.Clone();
        Subtitle = LocalSubtitle;
        FormPage page = PageAt(TerminalTab);

        _fontCaption = page.Add(Form.Caption("Font size"));
        _fontSize = page.Add(new Dropdown { Options = FontSizes.Select(s => $"{s.ToString(CultureInfo.InvariantCulture)} px").ToList() });
        _fontSize.SelectedIndex = NearestIndex(FontSizes, _draft.FontSize);
        _fontSize.SelectionChanged += i => { _draft.FontSize = FontSizes[i]; _preview!.InvalidatePaint(); };

        _scrollbackCaption = page.Add(Form.Caption("Scrollback"));
        _scrollback = page.Add(new Dropdown { Options = ScrollbackSizes.Select(n => $"{n.ToString("N0", CultureInfo.InvariantCulture)} lines").ToList() });
        _scrollback.SelectedIndex = NearestIndex(ScrollbackSizes.Select(n => (float)n).ToArray(), _draft.ScrollbackLines);
        _scrollback.SelectionChanged += i => _draft.ScrollbackLines = ScrollbackSizes[i];

        _cursorCaption = page.Add(Form.Caption("Cursor style"));
        _cursor = page.Add(new SegmentedControl("Block", "Bar", "Underline"));
        _cursor.SelectedIndex = Math.Max(0, Array.IndexOf(CursorShapes, _draft.CursorShape));
        _cursor.SelectionChanged += i => { _draft.CursorShape = CursorShapes[i]; _preview!.InvalidatePaint(); };

        _blink = page.Add(new Checkbox("Blinking cursor", _draft.CursorBlink));
        _blink.CheckedChanged += on => _draft.CursorBlink = on;
        _copyOnSelect = page.Add(new Checkbox("Copy text on select", _draft.CopyOnSelect));
        _copyOnSelect.CheckedChanged += on => _draft.CopyOnSelect = on;

        _previewCaption = page.Add(Form.Caption("Preview"));
        _preview = page.Add(new TerminalPreview(_draft));
        page.Layout = LayoutTerminal;

        // The built-in font size default is this device's terminal setting, including a change not saved yet.
        _defaults = new OptionsEditor(view, view.Services.Vault.Current.Defaults, OptionsLevel.Global,
            () => EffectiveOptions.Resolve(null, null, null, _draft.FontSize),
            options =>
            {
                VaultData vault = view.Services.Vault.Current.ShallowCopy();
                vault.Defaults = options;
                return vault;
            },
            PageAt(ConnectionTab), PageAt(SessionTab), PageAt(AppearanceTab));
        _defaults.LayoutChanged += InvalidateLayout;

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
        startup.Layout = LayoutStartup;

        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton("Save", ButtonVariant.Primary, Accept);
    }

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
        Subtitle = index == TerminalTab ? LocalSubtitle
            : index == StartupTab ? (local ? LocalStartupSubtitle : StartupSubtitle)
            : local ? LocalVaultSubtitle : SyncedSubtitle;
        _defaults.RefreshInherited();
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
        return y + updatesH;
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
        float col = (width - 16) / 2f;
        float y = 0;
        Form.Place(_fontCaption, _fontSize, 0, y, col);
        y = Form.Place(_scrollbackCaption, _scrollback, col + 16, y, col) + Form.RowGap;
        y = Form.Place(_cursorCaption, _cursor, 0, y, width) + Form.RowGap;
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
        if (JsonSerializer.Serialize(defaults) != JsonSerializer.Serialize(vault.Current.Defaults) && !View.RunVault(() => vault.SaveDefaultsAsync(defaults)))
            return;
        if (View is MainView main)
        {
            if (_restoreTabs.Checked != vault.Current.Workspace.RestoreTabs)
                main.SetRestoreTabs(_restoreTabs.Checked);
            main.SetCheckForUpdates(_checkUpdates.Checked);
        }
        TerminalSettings settings = _draft.Clone();
        View.Services.UpdatePrefs(p => p.Terminal = settings);
        Close();
    }

    /// <summary>A few prompt lines in the terminal font showing the chosen size and cursor shape.</summary>
    private sealed class TerminalPreview(TerminalSettings settings) : Control
    {
        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.Radius, Theme.TerminalBg);
            Gfx.StrokeRound(c, r, Theme.Radius, Theme.Border);
            int save = c.Save();
            c.ClipRect(SKRect.Inflate(r, -1, -1));
            SKPaint font = Gfx.Font(settings.FontSize, Theme.Mono);
            float lineH = MathF.Ceiling(settings.FontSize * 1.35f);
            float y = 14 + lineH / 2f;
            float x = 14;
            const string prompt = "demo@web-01:~$ ";
            Gfx.Text(c, "Last login: Mon Sep 28 09:14 from 10.0.0.2", x, y, font, Theme.TextMuted);
            y += lineH;
            Gfx.Text(c, prompt, x, y, font, Theme.Success);
            float cx = x + Gfx.Measure(prompt, font);
            Gfx.Text(c, "ls -la", cx, y, font, Theme.TextPrimary);
            cx += Gfx.Measure("ls -la", font);
            float cw = Gfx.Measure("M", font);
            float top = y - lineH / 2f + 1, bottom = y + lineH / 2f - 1;
            SKRect cursor = settings.CursorShape switch
            {
                TerminalSettings.CursorBar => new SKRect(cx + 1, top, cx + 3, bottom),
                TerminalSettings.CursorUnderline => new SKRect(cx, bottom - 2, cx + cw, bottom),
                _ => new SKRect(cx, top, cx + cw, bottom),
            };
            Gfx.FillRect(c, cursor, Theme.TextPrimary.WithAlpha(220));
            c.RestoreToCount(save);
        }
    }
}
