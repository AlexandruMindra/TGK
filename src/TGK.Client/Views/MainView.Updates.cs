using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Main;
using TGK.Client.Platform;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Views;

// Updates: GitHub is asked about newer releases on every start, in the background (TgkApplication sends the request as
// TGK starts; nothing waits for it: the last answer, kept in the preferences, shows until the new one arrives), then
// about twice a day while TGK runs.
// A newer version only adds a quiet chip to the status bar. Its menu installs it ("Update now": the release archive is
// downloaded, checked and unpacked in the background, then swapped in when TGK restarts or closes), opens the release
// page, skips that version or hides the chip until the next start. Settings → Startup turns the check off; the account
// menu checks on demand. A development build can't install itself and only offers the release page.
public sealed partial class MainView
{
    private const long FirstUpdateCheckDelayMs = 45_000;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    private enum UpdateStage
    {
        None,
        Available,
        Downloading,
        Ready,
    }

    private long _nextUpdateCheck = -1; // UiClock time of the next scheduled check
    private bool _updateChecking;
    private bool _updateDismissed; // "Remind me later": hidden until the next start
    private UpdateInfo? _update; // what the status bar chip offers
    private UpdateStage _updateStage;
    private int _downloadPercent;
    private CancellationTokenSource? _download;

    private void StartUpdateChecks()
    {
        // The scheduled check also removes what an earlier update left behind, a little after the start; with the start's
        // own answer just in, it asks GitHub only when that request failed.
        _nextUpdateCheck = UiClock.NowMs + FirstUpdateCheckDelayMs;
        if (Services.Prefs.CheckForUpdates && App.TakeStartupUpdateCheck() is { } startup)
        {
            ShowUpdate(CachedUpdate(Services.Prefs)); // the last answer, until the new one arrives
            ApplyStartupUpdateCheck(startup);
        }
        // An update downloaded before a sign-out or lock in this run is still waiting to be installed.
        if (App.PendingUpdate is not null && App.PendingUpdateInfo is { } pending)
        {
            _update = pending;
            _updateStage = UpdateStage.Ready;
            RefreshUpdateChip();
        }
        // An update that failed to install when TGK last closed is mentioned once.
        if (Services.Prefs.UpdateError is { } error)
        {
            Services.UpdatePrefs(p => p.UpdateError = null);
            UiThread.Post(() => ShowToast(error, ToastKind.Error));
        }
    }

    private void TickUpdates()
    {
        if (_nextUpdateCheck < 0 || UiClock.NowMs < _nextUpdateCheck || _updateChecking)
            return;
        _nextUpdateCheck = UiClock.NowMs + (long)UpdateCheckInterval.TotalMilliseconds;
        if (SelfUpdate.PackageName is not null && _updateStage < UpdateStage.Downloading && App.PendingUpdate is null)
            Task.Run(() => { using UpdateInstaller installer = SelfUpdate.CreateInstaller(); installer.CleanUp(); }); // files an update replaced
        ClientPrefs prefs = Services.Prefs;
        if (!prefs.CheckForUpdates || _updateStage >= UpdateStage.Downloading)
            return;
        // Asked recently (in this run or an earlier one): that answer stands until the interval is over. The cached
        // answer has no download details, so "Update now" asks GitHub again when it is chosen.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (prefs.LastUpdateCheck is { } last && last <= now && now - last < UpdateCheckInterval)
        {
            ShowUpdate(CachedUpdate(prefs));
            _nextUpdateCheck = UiClock.NowMs + (long)(last + UpdateCheckInterval - now).TotalMilliseconds;
            return;
        }
        CheckForUpdates(manual: false);
    }

    // The answer to the request sent as TGK started (TgkApplication), taken like a scheduled check's.
    private async void ApplyStartupUpdateCheck(Task<(bool Ok, UpdateInfo? Update)> startup)
    {
        _updateChecking = true;
        (bool ok, UpdateInfo? update) = await startup;
        _updateChecking = false;
        if (_torndown || !ok || _updateStage >= UpdateStage.Downloading)
            return;
        RememberUpdateCheck(update);
        ShowUpdate(update);
    }

    private void RememberUpdateCheck(UpdateInfo? update) => Services.UpdatePrefs(p =>
    {
        p.LastUpdateCheck = DateTimeOffset.UtcNow;
        p.AvailableUpdate = update?.Version.ToString();
        p.AvailableUpdateUrl = update?.Url;
    });

    /// <summary>The update channel of this device: the one chosen, else the installed build's kind.</summary>
    public UpdateChannel CurrentChannel => Services.Prefs.UpdateChannel ?? UpdateChecker.DefaultChannel(AppInfo.Version);

    /// <summary>Switches between stable releases and nightly builds (per device); GitHub is asked again shortly.</summary>
    public void SetUpdateChannel(UpdateChannel channel)
    {
        if (channel == CurrentChannel)
            return;
        Services.UpdatePrefs(p =>
        {
            p.UpdateChannel = channel;
            p.LastUpdateCheck = null; // the cached answer was for the other channel
            p.AvailableUpdate = null;
            p.AvailableUpdateUrl = null;
        });
        if (_updateStage < UpdateStage.Downloading)
        {
            ShowUpdate(null);
            if (Services.Prefs.CheckForUpdates)
                _nextUpdateCheck = UiClock.NowMs + 2_000;
        }
    }

    private static UpdateInfo? CachedUpdate(ClientPrefs prefs) =>
        prefs.AvailableUpdate is { } text && AppVersion.TryParse(text, out AppVersion version)
        && prefs.AvailableUpdateUrl is { } url && url.StartsWith(UpdateChecker.ReleasesUrl + "/", StringComparison.Ordinal)
            ? new UpdateInfo(version, url)
            : null;

    // Asks GitHub; null when that failed (logged).
    internal static async Task<(bool Ok, UpdateInfo? Update)> AskGitHubAsync(UpdateChannel channel)
    {
        try
        {
            using var checker = new UpdateChecker(userAgent: AppInfo.VersionText);
            return (true, await Task.Run(() => checker.CheckAsync(AppInfo.Version, SelfUpdate.PackageName, channel: channel)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            Log.Warning($"Update check failed: {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// Asks GitHub now. <paramref name="manual"/> (the account menu): the answer is told in a toast, including "up to
    /// date" and failures, and a skipped version is offered again.
    /// </summary>
    public async void CheckForUpdates(bool manual)
    {
        if (_updateChecking)
            return;
        if (_updateStage >= UpdateStage.Downloading)
        {
            if (manual)
                ShowToast(_updateStage == UpdateStage.Ready ? $"TGK {_update?.Version} is ready: restart to finish updating." : "The update is downloading.", ToastKind.Info);
            return;
        }
        _updateChecking = true;
        (bool ok, UpdateInfo? update) = await AskGitHubAsync(CurrentChannel);
        _updateChecking = false;
        if (_torndown)
            return;
        if (!ok)
        {
            if (manual)
                ShowToast("Couldn't check for updates: GitHub can't be reached right now.", ToastKind.Error);
            return;
        }
        RememberUpdateCheck(update);
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
        if (_updateStage >= UpdateStage.Downloading)
            return;
        bool show = update is not null && update.Version > AppInfo.Version && !_updateDismissed
            && (offerSkipped || Services.Prefs.SkippedUpdate != update.Version.ToString());
        _update = show ? update : null;
        _updateStage = show ? UpdateStage.Available : UpdateStage.None;
        RefreshUpdateChip();
    }

    private void RefreshUpdateChip() => _status.SetUpdate(_update is not { } update ? null : _updateStage switch
    {
        UpdateStage.Available => $"TGK {update.Version} available",
        UpdateStage.Downloading => $"Downloading TGK {update.Version} · {_downloadPercent}%",
        UpdateStage.Ready => $"Restart to update to TGK {update.Version}",
        _ => null,
    });

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
        var items = new List<MenuItem>();
        switch (_updateStage)
        {
            case UpdateStage.Available:
                items.Add(new MenuItem { Text = $"TGK {update.Version} is available · you have {AppInfo.VersionText}", IsHeader = true });
                items.Add(MenuItem.Separator);
                if (SelfUpdate.PackageName is not null)
                    items.Add(new MenuItem { Text = "Update now", Icon = "download", Action = () => DownloadUpdate(update) });
                items.Add(new MenuItem { Text = "Download from GitHub…", Icon = SelfUpdate.PackageName is null ? "download" : "tab-out", Action = () => OpenUpdatePage(update) });
                items.Add(new MenuItem { Text = "Skip this version", Icon = "x", Action = () =>
                {
                    Services.UpdatePrefs(p => p.SkippedUpdate = update.Version.ToString());
                    ShowUpdate(null);
                } });
                items.Add(new MenuItem { Text = "Remind me later", Icon = "clock", Action = () =>
                {
                    _updateDismissed = true;
                    ShowUpdate(null);
                } });
                break;
            case UpdateStage.Downloading:
                string size = update.Package is { Size: > 0 } p ? $" of {p.Size / (1024.0 * 1024):0} MB" : "";
                items.Add(new MenuItem { Text = $"Downloading TGK {update.Version}: {_downloadPercent}%{size}", IsHeader = true });
                items.Add(MenuItem.Separator);
                items.Add(new MenuItem { Text = "Cancel the update", Icon = "x", Action = () => _download?.Cancel() });
                break;
            case UpdateStage.Ready:
                items.Add(new MenuItem { Text = $"TGK {update.Version} is downloaded and ready to install", IsHeader = true });
                items.Add(MenuItem.Separator);
                items.Add(new MenuItem { Text = "Restart now", Icon = "refresh", Action = RestartToUpdate });
                items.Add(new MenuItem { Text = "Install when I close TGK", Icon = "clock", Action = () =>
                    ShowToast($"TGK {update.Version} will be installed when you close TGK.", ToastKind.Info) });
                items.Add(new MenuItem { Text = "What's new…", Icon = "tab-out", Action = () => OpenUpdatePage(update) });
                items.Add(MenuItem.Separator);
                items.Add(new MenuItem { Text = "Don't install it", Icon = "x", Action = DiscardUpdate });
                break;
            default:
                return;
        }
        Menu.Show(items, chip.Right - 340, chip.Bottom, 340, chip.Height);
    }

    // "Update now": downloads, checks and unpacks the release in the background; the chip shows the progress. Once it
    // is ready the app installs it when it closes, or right away with "Restart now".
    private async void DownloadUpdate(UpdateInfo update)
    {
        if (_updateStage != UpdateStage.Available)
            return;
        using UpdateInstaller installer = SelfUpdate.CreateInstaller();
        if (installer.CheckInstallable() is { } problem)
        {
            ShowToast($"{problem} Download the new version from GitHub instead.", ToastKind.Error);
            OpenUpdatePage(update);
            return;
        }
        _updateStage = UpdateStage.Downloading;
        _downloadPercent = 0;
        RefreshUpdateChip();
        _download = new CancellationTokenSource();
        CancellationToken ct = _download.Token;
        try
        {
            // A chip from the cached answer has no download details yet: ask GitHub for them.
            if (update.Package is null)
            {
                (bool ok, UpdateInfo? fresh) = await AskGitHubAsync(CurrentChannel);
                if (!ok || fresh?.Package is null)
                    throw new UpdateException(ok ? "This release has no download for this platform." : "GitHub can't be reached right now.");
                update = fresh;
                _update = fresh;
            }
            var progress = new Progress<double>(fraction =>
            {
                int percent = (int)(fraction * 100);
                if (percent == _downloadPercent || _updateStage != UpdateStage.Downloading)
                    return;
                _downloadPercent = percent;
                RefreshUpdateChip();
            });
            string staged = await Task.Run(() => installer.DownloadAsync(update, progress, ct), ct);
            App.PendingUpdate = staged; // kept by the app: installed on exit even if this view is gone by now
            App.PendingUpdateInfo = update;
            if (_torndown)
                return;
            _updateStage = UpdateStage.Ready;
            RefreshUpdateChip();
            ShowToast($"TGK {update.Version} is ready: restart to finish updating (or it installs when you close TGK).", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            _updateStage = UpdateStage.Available;
            RefreshUpdateChip();
        }
        catch (UpdateException ex)
        {
            Log.Warning($"Update failed: {ex}");
            _updateStage = UpdateStage.Available;
            RefreshUpdateChip();
            ShowToast($"{ex.Message} You can download it from GitHub instead (status bar).", ToastKind.Error);
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
    }

    /// <summary>A download in progress stops with the view (sign-out, lock); a finished one stays with the app.</summary>
    private void StopUpdates() => _download?.Cancel();

    private void DiscardUpdate()
    {
        App.PendingUpdate = null;
        App.PendingUpdateInfo = null;
        Task.Run(() => { using UpdateInstaller installer = SelfUpdate.CreateInstaller(); installer.DeleteStaging(); });
        _updateStage = UpdateStage.Available;
        RefreshUpdateChip();
    }

    // Closes TGK (saving the tabs as on any close), installs the update and starts the new version. Asks first when
    // sessions are connected.
    private async void RestartToUpdate()
    {
        int connected = _tabs.Count(t => t.Status is TabStatus.Connected or TabStatus.Connecting);
        if (connected > 0)
        {
            string reopen = Services.Vault.Current.Workspace.RestoreTabs ? " Your tabs are reopened after the restart." : "";
            if (!await ConfirmDialog.ShowAsync(this, $"Restart to update to TGK {_update?.Version}?",
                    $"{connected} open session{(connected == 1 ? "" : "s")} will be disconnected.{reopen}", "Restart now"))
                return;
        }
        App.RestartForUpdate();
    }

    private void OpenUpdatePage(UpdateInfo update)
    {
        if (ExternalLink.Open(update.Url))
            return;
        Shell.SetClipboardText(update.Url);
        ShowToast($"No browser could be opened; the link was copied: {update.Url}", ToastKind.Info);
    }
}
