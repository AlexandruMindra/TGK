using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Main;
using TGK.Client.Terminal;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>
/// Settings: the terminal's look and behaviour and the connection and session defaults every host inherits, plus the
/// "reopen my tabs" preference. All of it lives in the vault and syncs to all devices; only the update notices are
/// per device (<c>ClientServices.Prefs</c>).
/// </summary>
public sealed class SettingsDialog : TabbedDialog
{
    public const int TerminalTab = 0, ConnectionTab = 1, SessionTab = 2, StartupTab = 3;
    private const string TerminalSubtitle = "Synced to all your devices. Groups and hosts can override the color scheme and the font.";
    private const string LocalTerminalSubtitle = "Kept in your local vault. Groups and hosts can override the color scheme and the font.";
    private const string SyncedSubtitle = "Connection defaults for all hosts, synced to all your devices. Groups and hosts can override them.";
    private const string LocalVaultSubtitle = "Connection defaults for all hosts, kept in your local vault. Groups and hosts can override them.";
    private const string StartupSubtitle = "Reopening your tabs (synced to all your devices) and update notices (this device).";
    private const string LocalStartupSubtitle = "Reopening your tabs (kept in your local vault) and update notices.";
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

    public SettingsDialog(TgkView view) : base(view, "Settings", 640, "Terminal", "Connection", "Session", "Startup")
    {
        HostOptions saved = view.Services.Vault.Current.Defaults;
        EffectiveHostOptions current = EffectiveOptions.Resolve(null, null, saved);
        Subtitle = view.Services.IsLocal ? LocalTerminalSubtitle : TerminalSubtitle;
        FormPage page = PageAt(TerminalTab);

        // Color scheme, font and size: the appearance part of the defaults, shown on the Terminal page.
        _defaults = new OptionsEditor(view, saved, OptionsLevel.Global, () => EffectiveOptions.Resolve((HostOptions?)null, null, null),
            options =>
            {
                VaultData vault = view.Services.Vault.Current.ShallowCopy();
                vault.Defaults = options;
                return vault;
            },
            PageAt(ConnectionTab), PageAt(SessionTab), page, arrangeAppearance: false);
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
        startup.Layout = LayoutStartup;

        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton("Save", ButtonVariant.Primary, Accept);
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
        }
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
            SKPaint font = Gfx.Font(size, TerminalFonts.Get(family).Regular);
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
