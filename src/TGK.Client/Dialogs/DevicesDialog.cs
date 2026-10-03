using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Main;
using TGK.Client.Views;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>"Devices &amp; sessions": the account's signed-in devices, with per-device and "all other devices" sign-out.</summary>
public sealed class DevicesDialog : DialogBase
{
    private const float RowH = 60, RowGap = 8, MaxVisibleRows = 5;

    private readonly MainView _main;
    private readonly IVaultService _vault;
    private readonly ScrollContainer _scroll;
    private readonly Label _status;
    private readonly Button _signOutOthers, _refresh;
    private List<DeviceSession> _sessions = [];
    private bool _busy;

    public DevicesDialog(MainView main) : base(main, "Devices & sessions", 640)
    {
        _main = main;
        _vault = main.Services.Vault;
        Subtitle = "Devices signed in to your account. Signing one out ends its session right away.";
        _scroll = AddBody(new ScrollContainer
        {
            OverflowX = OverflowMode.Clip,
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden,
            ScrollbarThickness = 6,
            ScrollbarRadius = 3,
            ScrollbarThumbColor = Theme.BorderStrong,
            ScrollbarTrackColor = SKColors.Transparent,
            Style = new ElementStyle(),
        });
        _status = AddBody(new Label("Loading…", Theme.FontBase, Theme.TextMuted) { MaxLines = 3 });
        _signOutOthers = AddLeftButton("Sign out all other devices", ButtonVariant.Secondary, SignOutOthers);
        _signOutOthers.Icon = "logout";
        _refresh = AddButton("Refresh", ButtonVariant.Secondary, Reload);
        _refresh.Icon = "refresh";
        AddButton("Close", ButtonVariant.Primary, Accept);
        Reload();
    }

    protected override void Accept() => Close();

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        if (_status.Visible)
        {
            float h = _status.MeasureHeight(width);
            _status.Transform.SetLocalFrame(left, y, width, h);
            y += h + (_sessions.Count > 0 ? 12 : 0);
        }
        float listH = _sessions.Count == 0 ? 0 : Math.Min(_sessions.Count, MaxVisibleRows) * (RowH + RowGap) - RowGap;
        _scroll.Visible = _sessions.Count > 0;
        _scroll.Transform.SetLocalFrame(left, y, width, Math.Max(1, listH));
        float rowW = _sessions.Count > MaxVisibleRows ? width - 10 : width; // room for the scrollbar
        for (int i = 0; i < _scroll.Children.Count; i++)
        {
            if (_scroll.Children[i] is SessionRow row)
                row.Transform.SetLocalFrame(0, row.Index * (RowH + RowGap), rowW, RowH);
        }
        return y + listH - top;
    }

    private async void Reload()
    {
        if (_busy)
            return;
        SetBusy(true);
        try
        {
            IReadOnlyList<DeviceSession> sessions = await _vault.ListSessionsAsync();
            if (IsClosed)
                return;
            _sessions = sessions.OrderByDescending(s => s.Current).ThenByDescending(s => s.LastSeenAt).ToList();
            SetStatus(null);
        }
        catch (VaultException ex)
        {
            if (IsClosed)
                return;
            _sessions = [];
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
        RebuildRows();
    }

    private void RebuildRows()
    {
        foreach (VisualElement child in _scroll.Children.ToList())
        {
            _scroll.RemoveChild(child);
            child.Dispose();
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _sessions.Count; i++)
            _scroll.AddChild(new SessionRow(_sessions[i], i, now, SignOut));
        _signOutOthers.Enabled = _sessions.Any(s => !s.Current);
        _scroll.ScrollY = 0;
        InvalidateLayout();
    }

    private async void SignOut(DeviceSession session)
    {
        if (_busy)
            return;
        if (session.Current)
        {
            if (!await ConfirmDialog.ShowAsync(View, "Sign out of this device?",
                    "Open sessions will be closed. You'll need your password and an authenticator code to sign in again.", "Sign out"))
                return;
            Close();
            _main.SignOut(confirmed: true);
            return;
        }
        await RunAsync(async () =>
        {
            await _vault.RevokeSessionAsync(session.Id);
            View.ShowToast($"Signed out {session.DeviceName}", ToastKind.Success);
        });
    }

    private async void SignOutOthers()
    {
        int others = _sessions.Count(s => !s.Current);
        if (_busy || others == 0 || !await ConfirmDialog.ShowAsync(View, "Sign out all other devices?",
                $"{others} other device{(others == 1 ? "" : "s")} will be signed out and need your password and an authenticator code to sign in again.",
                "Sign out all", danger: true))
            return;
        await RunAsync(async () =>
        {
            int revoked = await _vault.RevokeOtherSessionsAsync();
            View.ShowToast($"Signed out {revoked} other device{(revoked == 1 ? "" : "s")}", ToastKind.Success);
        });
    }

    /// <summary>Runs an account call with the dialog busy, shows a failure as a toast, then reloads the list.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
        }
        catch (VaultException ex)
        {
            View.ShowToast(ex.Message, ToastKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
        if (!IsClosed)
            Reload();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.Enabled = !busy;
        _signOutOthers.Enabled = !busy && _sessions.Any(s => !s.Current);
        foreach (VisualElement child in _scroll.Children)
        {
            if (child is SessionRow row)
                row.SetEnabled(!busy);
        }
        if (busy && _sessions.Count == 0)
            SetStatus("Loading…");
    }

    private void SetStatus(string? message, bool error = false)
    {
        _status.Text = message ?? "";
        _status.Color = error ? Theme.Danger : Theme.TextMuted;
        _status.Visible = message is not null;
        InvalidateLayout();
    }

    /// <summary>One device: name with a "This device" badge, platform, last activity and address, and a sign-out button.</summary>
    private sealed class SessionRow : Control
    {
        private readonly DeviceSession _session;
        private readonly string _details;
        private readonly Button _signOut;

        public SessionRow(DeviceSession session, int index, DateTimeOffset now, Action<DeviceSession> signOut)
        {
            _session = session;
            Index = index;
            string seen = session.Current ? "Active now" : $"Last active {HostFormat.Ago(session.LastSeenAt, now)}";
            _details = string.Join(" · ", new[] { session.Platform, seen, session.LastIp }.Where(s => !string.IsNullOrWhiteSpace(s)));
            _signOut = new Button("Sign out", ButtonVariant.Secondary);
            _signOut.Clicked += () => signOut(_session);
            AddChild(_signOut);
        }

        public int Index { get; }

        public void SetEnabled(bool enabled) => _signOut.Enabled = enabled;

        protected override void LayoutChildren()
        {
            float bw = Math.Max(88, _signOut.PreferredWidth);
            _signOut.Transform.SetLocalFrame(W - bw - 14, (H - 30) / 2f, bw, 30);
        }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.Radius, Theme.SurfaceRaised);
            Gfx.StrokeRound(c, r, Theme.Radius, _session.Current ? Theme.Accent.WithAlpha(110) : Theme.Border);
            Icons.Draw(c, "terminal", 26, H / 2f, 20, _session.Current ? Theme.Accent : Theme.TextSecondary);
            float textW = W - 50 - 130;
            SKFont nameFont = Gfx.Font(Theme.FontMd, Theme.WeightSemibold);
            string name = Gfx.Ellipsize(_session.DeviceName, nameFont, textW - (_session.Current ? 90 : 0));
            Gfx.Text(c, name, 50, 21, nameFont, Theme.TextPrimary);
            if (_session.Current)
            {
                float bx = 50 + Gfx.Measure(name, nameFont) + 8, bw = Gfx.Measure("This device", Theme.FontXs, Theme.WeightSemibold) + 14;
                var badge = SKRect.Create(bx, 12, bw, 18);
                Gfx.FillRound(c, badge, 9, Theme.AccentSoft);
                Gfx.Text(c, "This device", badge.MidX, badge.MidY, Theme.FontXs, Theme.WeightSemibold, Theme.AccentHover, TextAlignment.Center);
            }
            Gfx.Text(c, _details, 50, 41, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, textW);
        }
    }
}
