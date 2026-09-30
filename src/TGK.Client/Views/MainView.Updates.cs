using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Blossom;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Platform;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Views;

// Update check: GitHub is asked about newer releases in the background, never while TGK starts (the first check waits
// until the window has been open for a while) and at most about twice a day (the answer is kept in the preferences).
// A newer version only adds a quiet chip to the status bar; its menu opens the release page, skips that version or
// hides the chip until the next start. Settings → Startup turns the check off; the account menu checks on demand.
public sealed partial class MainView
{
    private const long FirstUpdateCheckDelayMs = 45_000;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    private long _nextUpdateCheck = -1; // UiClock time of the next scheduled check
    private bool _updateChecking;
    private bool _updateDismissed; // "Remind me later": hidden until the next start
    private UpdateInfo? _update; // what the status bar chip offers

    private void StartUpdateChecks() => _nextUpdateCheck = UiClock.NowMs + FirstUpdateCheckDelayMs;

    private void TickUpdates()
    {
        if (_nextUpdateCheck < 0 || UiClock.NowMs < _nextUpdateCheck || _updateChecking)
            return;
        _nextUpdateCheck = UiClock.NowMs + (long)UpdateCheckInterval.TotalMilliseconds;
        ClientPrefs prefs = Services.Prefs;
        if (!prefs.CheckForUpdates)
            return;
        // Asked recently (in this run or an earlier one): that answer stands until the interval is over.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (prefs.LastUpdateCheck is { } last && last <= now && now - last < UpdateCheckInterval)
        {
            ShowUpdate(CachedUpdate(prefs));
            _nextUpdateCheck = UiClock.NowMs + (long)(last + UpdateCheckInterval - now).TotalMilliseconds;
            return;
        }
        CheckForUpdates(manual: false);
    }

    private static UpdateInfo? CachedUpdate(ClientPrefs prefs) =>
        prefs.AvailableUpdate is { } text && AppVersion.TryParse(text, out AppVersion version)
        && prefs.AvailableUpdateUrl is { } url && url.StartsWith(UpdateChecker.ReleasesUrl + "/", StringComparison.Ordinal)
            ? new UpdateInfo(version, url)
            : null;

    /// <summary>
    /// Asks GitHub now. <paramref name="manual"/> (the account menu): the answer is told in a toast, including "up to
    /// date" and failures, and a skipped version is offered again.
    /// </summary>
    public async void CheckForUpdates(bool manual)
    {
        if (_updateChecking)
            return;
        _updateChecking = true;
        UpdateInfo? update;
        try
        {
            using var checker = new UpdateChecker(userAgent: AppInfo.VersionText);
            update = await Task.Run(() => checker.CheckAsync(AppInfo.Version));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            Log.Warning($"Update check failed: {ex.Message}");
            if (manual && !_torndown)
                ShowToast("Couldn't check for updates: GitHub can't be reached right now.", ToastKind.Error);
            return;
        }
        finally
        {
            _updateChecking = false;
        }
        if (_torndown)
            return;
        Services.UpdatePrefs(p =>
        {
            p.LastUpdateCheck = DateTimeOffset.UtcNow;
            p.AvailableUpdate = update?.Version.ToString();
            p.AvailableUpdateUrl = update?.Url;
        });
        if (!manual)
        {
            ShowUpdate(update);
            return;
        }
        _updateDismissed = false;
        if (update is null)
        {
            ShowUpdate(null);
            ShowToast($"TGK {AppInfo.VersionText} is the latest version.", ToastKind.Success);
            return;
        }
        ShowUpdate(update, offerSkipped: true);
        ShowToast($"TGK {update.Version} is available: see the status bar.", ToastKind.Info);
    }

    // Shows or hides the status bar chip for `update` (skipped versions and "later" stay hidden unless `offerSkipped`).
    private void ShowUpdate(UpdateInfo? update, bool offerSkipped = false)
    {
        bool show = update is not null && update.Version > AppInfo.Version && !_updateDismissed
            && (offerSkipped || Services.Prefs.SkippedUpdate != update.Version.ToString());
        _update = show ? update : null;
        _status.SetUpdate(show ? $"TGK {update!.Version} available" : null);
    }

    /// <summary>Turns the update check on (it runs shortly) or off (the chip goes away). Per device.</summary>
    public void SetCheckForUpdates(bool on)
    {
        if (on == Services.Prefs.CheckForUpdates)
            return;
        Services.UpdatePrefs(p => p.CheckForUpdates = on);
        if (on)
            _nextUpdateCheck = UiClock.NowMs + 2_000;
        else
            ShowUpdate(null);
    }

    private void ShowUpdateMenu(SKRect chip)
    {
        if (_update is not { } update)
            return;
        var items = new List<MenuItem>
        {
            new() { Text = $"TGK {update.Version} is available · you have {AppInfo.VersionText}", IsHeader = true },
            MenuItem.Separator,
            new() { Text = "Download from GitHub…", Icon = "download", Action = () => OpenUpdatePage(update) },
            new() { Text = "Skip this version", Icon = "x", Action = () =>
            {
                Services.UpdatePrefs(p => p.SkippedUpdate = update.Version.ToString());
                ShowUpdate(null);
            } },
            new() { Text = "Remind me later", Icon = "clock", Action = () =>
            {
                _updateDismissed = true;
                ShowUpdate(null);
            } },
        };
        Menu.Show(items, chip.Right - 320, chip.Bottom, 320, chip.Height);
    }

    private void OpenUpdatePage(UpdateInfo update)
    {
        if (ExternalLink.Open(update.Url))
            return;
        Browser.SetClipboardText(update.Url);
        ShowToast($"No browser could be opened; the link was copied: {update.Url}", ToastKind.Info);
    }
}
