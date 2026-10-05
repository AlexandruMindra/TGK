using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Terminal;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Main;

/// <summary>
/// A terminal tab connected to one host over SSH. Connects when first shown (so background tabs stay idle), asks for
/// passwords (also for jump hosts) or a host-key decision when needed, offers Retry / Reconnect after failures and
/// disconnects, and reconnects by itself after a lost connection when the host's Auto-reconnect option is on. Those
/// automatic attempts never prompt: one that needs the user stops and leaves a manual Reconnect.
/// </summary>
/// <remarks>
/// SSH output is queued into the <see cref="TerminalView"/> from the session's reader thread; state changes are
/// marshalled to the UI thread. Each connection attempt uses a fresh <see cref="SshSession"/> (sessions are single-use)
/// and starts from a terminal reset to its default modes, keeping the previous session's output as history. The
/// host's effective options (<see cref="EffectiveOptions"/>) decide the request, the font size and the color scheme.
/// </remarks>
public sealed class SessionTabContent : TabContent, IKeyInput
{
    private static readonly int[] ReconnectDelaysSeconds = [2, 5, 10, 30, 60];
    private const int MaxReconnectAttempts = 10;
    private const long StableConnectionMs = 60_000; // a session that lasted this long starts the reconnect count over

    private readonly HostEntry _host;
    private readonly string? _initialInput;
    private readonly SessionOverlay _overlay = new();
    private readonly Dictionary<string, string> _hopPasswords = []; // typed for jump hosts, by user@host:port
    private readonly HashSet<Guid> _reportedTunnelFailures = [];
    private TerminalView _terminal = null!;
    private volatile SshSession? _session; // also read by the session's reader thread
    private CancellationTokenSource? _attempt;
    private int _generation; // identifies the current connection attempt; older attempts ignore their results
    private string? _password; // typed at the prompt; reused when this tab reconnects
    private string _address = "";
    private bool _started, _closing, _initialInputSent;
    private int _reconnectAttempt; // the automatic attempt running or counted down to; kept after it succeeds (see OnSessionState)
    private long _reconnectAt = -1; // UiClock time of the next automatic attempt; -1 when none is scheduled
    private int _reconnectShownSeconds = -1;
    private string _lostMessage = "";
    private long _connectedAtMs;
    private bool _unattended; // the running attempt was started by the reconnect timer, so nobody is waiting for its prompts
    private string? _inputNeeded; // what an unattended attempt would have had to ask for, e.g. "a password"

    /// <param name="host">A saved host (re-read from the vault on every connect) or an unsaved quick-connect entry.</param>
    /// <param name="password">Password to try first instead of the stored credentials (dev and quick-connect use).</param>
    /// <param name="initialInput">Text sent once after the first successful connect (dev use).</param>
    public SessionTabContent(HostEntry host, string? password = null, string? initialInput = null)
    {
        _host = host;
        _password = password;
        _initialInput = initialInput;
        ReceivesKeyboard = true; // clicks on the overlay keep keys here, so Enter can retry
        Style = new ElementStyle { BackColor = Theme.TerminalBg };
        SetTitle(host.DisplayName);
        _overlay.PrimaryClicked += OnOverlayPrimary;
        _overlay.SecondaryClicked += OnOverlaySecondary;
    }

    public override VisualElement? DefaultFocus => _terminal;

    // A saved host by id (it is re-read on reconnect anyway), an unsaved one by address. Passwords are never saved.
    public override WorkspaceTab SaveState() => IsSaved
        ? new WorkspaceTab { HostId = _host.Id }
        : new WorkspaceTab { Host = _host.Host, Port = _host.Port, Username = string.IsNullOrWhiteSpace(_host.Username) ? null : _host.Username };

    public override bool WantsControlKeys => true;

    private bool IsSaved => Host.Services.Vault.Current.FindHost(_host.Id) is not null;

    private HostEntry CurrentHost => Host.Services.Vault.Current.FindHost(_host.Id) ?? _host;

    public override void OnAttached()
    {
        _terminal = new TerminalView(EffectiveTerminal());
        _terminal.Input += OnTerminalInput;
        _terminal.GridResized += OnGridResized;
        _terminal.TitleChanged += title => SetTitle(string.IsNullOrWhiteSpace(title) ? _host.DisplayName : title);
        _terminal.BellRang += RequestAttention;
        _terminal.ExtraMenuItems = TabMenuItems;
        AddChild(_terminal);
        AddChild(_overlay);
        ApplyAppearance();
        UiClock.Tick += OnTick;
        _address = HostFormat.Address(_host, Host.Services.Vault.Current);
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
    }

    public override void OnShown()
    {
        if (_started)
            return;
        _started = true;
        // After this input event: the tab is laid out by then, so the first request carries the real grid size.
        UiThread.Post(() => Connect());
    }

    public override void OnClosing()
    {
        _closing = true;
        UiClock.Tick -= OnTick;
        CancelAttempt();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>The vault changed: the host's terminal settings may have (the next connect re-reads everything else).</summary>
    public void OnVaultChanged() => ApplyAppearance();

    private TerminalSettings EffectiveTerminal() => EffectiveOptions.Terminal(EffectiveOptions.Resolve(Host.Services.Vault.Current, CurrentHost));

    // Font, cursor and color scheme come from the host's effective options (synced in the vault).
    private void ApplyAppearance()
    {
        EffectiveHostOptions options = EffectiveOptions.Resolve(Host.Services.Vault.Current, CurrentHost);
        _terminal.ApplySettings(EffectiveOptions.Terminal(options));
        ColorScheme scheme = ColorScheme.Find(options.ColorScheme.Value);
        _terminal.Scheme = scheme;
        Style.BackColor = scheme.Background;
    }

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        if (_overlay.IsBanner)
        {
            // The terminal gives up the banner's rows, so its last lines (often the reason the session ended) stay
            // visible above it. The session is gone, so the smaller size is never sent anywhere.
            float termH = Math.Max(0, h - SessionOverlay.BannerHeight);
            _terminal.Transform.SetLocalFrame(0, 0, w, termH);
            _overlay.Transform.SetLocalFrame(0, termH, w, h - termH);
        }
        else
        {
            _terminal.Transform.SetLocalFrame(0, 0, w, h);
            _overlay.Transform.SetLocalFrame(0, 0, w, h);
        }
        _terminal.FitToSize();
    }

    // Keys reach the tab when the terminal has no live session (it lets them bubble) or when the overlay has focus.
    public bool OnKey(KeyStroke key)
    {
        if (key.IsEnter && key.Modifiers == KeyModifiers.None && !key.IsRepeat
            && _overlay.Mode is SessionOverlay.OverlayMode.Failed or SessionOverlay.OverlayMode.Ended or SessionOverlay.OverlayMode.Reconnecting)
        {
            OnOverlayPrimary();
            return true;
        }
        if (key.Is(Key.Escape) && _overlay.Mode is SessionOverlay.OverlayMode.Connecting or SessionOverlay.OverlayMode.Reconnecting)
        {
            OnOverlaySecondary();
            return true;
        }
        return false;
    }

    public void OnText(string text) { }

    /// <param name="automatic">An auto-reconnect attempt: it keeps the banner and schedules the next one if it fails.</param>
    /// <param name="unattended">
    /// Started by the reconnect timer rather than the user: it must not prompt, because the user may be typing in
    /// another tab and the prompt would take those keys (and the Enter meant for that tab) as this host's secret.
    /// </param>
    private async void Connect(bool automatic = false, bool unattended = false)
    {
        if (_closing)
            return;
        if (!automatic)
            _reconnectAttempt = 0;
        _unattended = unattended;
        _inputNeeded = null;
        _reconnectAt = -1;
        CancelAttempt();
        _session?.Dispose();
        _session = null;
        SetTunnels([]);
        int generation = ++_generation;
        _attempt = new CancellationTokenSource();
        CancellationToken ct = _attempt.Token;

        // Saved hosts are re-read so edits (e.g. from "Edit host…" after a failure) apply to the retry.
        var vault = Host.Services.Vault;
        HostEntry host = CurrentHost;
        _address = HostFormat.Address(host, vault.Current);
        ApplyAppearance();
        _terminal.InputEnabled = false;
        _terminal.ResetSession(); // e.g. a dropped vim or tmux left the alternate screen and mouse reporting on
        ShowConnecting("Opening connection…");
        ForceLayoutSubtree(); // the terminal takes back the rows of an "ended" banner before its size is sent

        SshConnectRequest request;
        try
        {
            request = SshConnectRequest.ForHost(vault.Current, host); // with its effective options, jump hosts and tunnels
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                string? user = await AskAsync(generation, "a username", guard => new PromptDialog(Host, "Username required", _address, null,
                    "Username", "Continue", guardEnter: guard).ShowAsync(ct));
                if (generation != _generation || _closing)
                    return;
                if (string.IsNullOrWhiteSpace(user))
                {
                    ShowNotConnected("No username was entered.");
                    return;
                }
                request = request with { Username = user.Trim() };
                _address = $"{request.Username}@{_address}";
            }

            // Each jump host signs in with its own credentials; ask for what the vault does not have.
            for (int i = 0; i < request.JumpChain.Count; i++)
            {
                SshConnectRequest? hop = await CompleteHopAsync(generation, request.JumpChain[i], null, ct);
                if (generation != _generation || _closing)
                    return;
                if (hop is null)
                {
                    ShowNotConnected($"No password was entered for jump host {HopName(request.JumpChain[i])}.");
                    return;
                }
                request = WithHop(request, i, hop);
            }

            bool usesKey = !string.IsNullOrWhiteSpace(request.PrivateKey);
            string? password = usesKey ? null : _password ?? request.Password;
            string? error = null;
            while (true)
            {
                if (!usesKey)
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        password = await AskAsync(generation, "a password", guard => PasswordPromptDialog.ShowAsync(Host, _address, error, guard, ct));
                        if (generation != _generation || _closing)
                            return;
                        if (password is null)
                        {
                            ShowNotConnected("No password was entered.");
                            return;
                        }
                        _password = password; // remembered for reconnects of this tab only
                    }
                    request = request with { Password = password };
                }

                SshSession session = CreateSession(generation);
                _session = session;
                try
                {
                    // The grid may have changed while a prompt was open; later changes are sent as window-change.
                    await session.ConnectAsync(request with { Cols = _terminal.Cols, Rows = _terminal.Rows }, ct);
                    break;
                }
                catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && !ct.IsCancellationRequested
                    && ex.JumpHostIndex is int hopIndex && hopIndex < request.JumpChain.Count
                    && string.IsNullOrWhiteSpace(request.JumpChain[hopIndex].PrivateKey))
                {
                    // A jump host rejected its password: ask for that one again.
                    session.Dispose();
                    ShowConnecting("Authentication failed");
                    SshConnectRequest? hop = await CompleteHopAsync(generation, request.JumpChain[hopIndex], ex.Message, ct);
                    if (generation != _generation || _closing)
                        return;
                    if (hop is null)
                    {
                        ShowNotConnected($"No password was entered for jump host {HopName(request.JumpChain[hopIndex])}.");
                        return;
                    }
                    request = WithHop(request, hopIndex, hop);
                }
                catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && ex.JumpHostIndex is null && !usesKey && !ct.IsCancellationRequested)
                {
                    // Wrong password: ask again, with the reason, until it works or the user cancels.
                    session.Dispose();
                    password = _password = null;
                    error = ex.Message;
                    ShowConnecting("Authentication failed");
                }
            }
        }
        catch (SshSessionException ex)
        {
            if (generation != _generation || _closing)
                return;
            if (_inputNeeded is { } need)
                StopReconnecting(need); // e.g. the host key changed: a prompt was needed and declined
            else if (_reconnectAttempt > 0 && IsTransient(ex.Kind) && _reconnectAttempt < MaxReconnectAttempts)
            {
                _lostMessage = ex.Message;
                ScheduleReconnect();
            }
            else
            {
                ShowFailed(_reconnectAttempt >= MaxReconnectAttempts ? $"Gave up reconnecting after {MaxReconnectAttempts} attempts. {ex.Message}" : ex.Message);
            }
            return;
        }
        catch (OperationCanceledException)
        {
            if (generation != _generation || _closing)
                return;
            if (_inputNeeded is { } need)
                StopReconnecting(need);
            else
                ShowNotConnected("The connection attempt was cancelled.");
            return;
        }

        if (generation == _generation && !_closing)
            OnConnected(host);
    }

    // Failures worth retrying automatically: the server or network may be back soon. Credentials, host keys and
    // configuration problems need the user.
    private static bool IsTransient(SshErrorKind kind) => kind is SshErrorKind.DnsFailure or SshErrorKind.ConnectionRefused
        or SshErrorKind.HostUnreachable or SshErrorKind.Timeout or SshErrorKind.ConnectionLost or SshErrorKind.Other;

    /// <summary>
    /// Fills in a jump host's username and password when the vault has none (or <paramref name="error"/> says the
    /// stored or typed one was rejected); null when the user cancelled. Typed passwords are kept for this tab.
    /// </summary>
    private async Task<SshConnectRequest?> CompleteHopAsync(int generation, SshConnectRequest hop, string? error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hop.Username))
        {
            string? user = await AskAsync(generation, $"a username for jump host {HopName(hop)}", guard => new PromptDialog(Host, "Username required",
                $"Jump host {HopName(hop)}", null, "Username", "Continue", guardEnter: guard).ShowAsync(ct));
            if (string.IsNullOrWhiteSpace(user))
                return null;
            hop = hop with { Username = user.Trim() };
        }
        if (!string.IsNullOrWhiteSpace(hop.PrivateKey))
            return hop;
        string key = $"{hop.Username}@{hop.Host}:{hop.Port}";
        if (error is null && !string.IsNullOrEmpty(hop.Password))
            return hop;
        if (error is null && _hopPasswords.TryGetValue(key, out string? remembered))
            return hop with { Password = remembered };
        _hopPasswords.Remove(key);
        string target = $"{hop.Username}@{(hop.Port == 22 ? hop.Host : $"{hop.Host}:{hop.Port}")} (jump host)";
        string? password = await AskAsync(generation, $"the password of jump host {HopName(hop)}",
            guard => PasswordPromptDialog.ShowAsync(Host, target, error, guard, ct));
        if (password is null)
            return null;
        _hopPasswords[key] = password;
        return hop with { Password = password };
    }

    private static string HopName(SshConnectRequest hop) => string.IsNullOrWhiteSpace(hop.Name) ? hop.Host : hop.Name.Trim();

    private static SshConnectRequest WithHop(SshConnectRequest request, int index, SshConnectRequest hop) =>
        request with { JumpChain = request.JumpChain.Select((h, i) => i == index ? hop : h).ToList() };

    private SshSession CreateSession(int generation)
    {
        var verifier = new KnownHostsVerifier(Host.Services.Vault, (info, ct) =>
            UiThread.InvokeAsync(() => AskAsync(generation, $"a decision about the host key of {info.Host}", _ => HostKeyDialog.ShowAsync(Host, info, ct))));
        var session = new SshSession(verifier, (prompt, ct) =>
            UiThread.InvokeAsync(() => AskAsync(generation, "an answer to a sign-in prompt", guard => SignInPromptDialog.ShowAsync(Host,
                $"{prompt.Username}@{(prompt.Port == 22 ? prompt.Host : $"{prompt.Host}:{prompt.Port}")}", prompt, guard, ct))));
        session.DataReceived += (buffer, count) =>
        {
            // A replaced session's reader may still deliver a last chunk: it must not reach the new session's screen.
            if (session == _session)
                _terminal.Write(buffer.AsSpan(0, count));
        };
        session.StateChanged += (state, message) => UiThread.Post(() => OnSessionState(session, state, message));
        session.TunnelsChanged += () => UiThread.Post(() => OnTunnelsChanged(session));
        return session;
    }

    private void OnTerminalInput(byte[] bytes)
    {
        if (_session is { } session && !session.Send(bytes) && session.State == SessionState.Connected)
            Host.ShowToast("The server is not accepting input right now, so the last input was dropped.", ToastKind.Error);
    }

    /// <summary>
    /// Shows a prompt of attempt <paramref name="generation"/>. Prompts belong to this tab: it comes to the front first
    /// so the user knows which connection is asking, and when it was in the background the prompt's Enter waits until
    /// the keys meant for the other tab have stopped (<c>show</c>'s argument). An unattended attempt never prompts: it
    /// records <paramref name="need"/> and gives up (cancelled), and the tab then offers a manual Reconnect.
    /// </summary>
    private Task<T> AskAsync<T>(int generation, string need, Func<bool, Task<T>> show)
    {
        if (generation != _generation)
            throw new OperationCanceledException("The connection attempt was replaced.");
        if (_unattended)
        {
            _inputNeeded ??= need;
            throw new OperationCanceledException("An automatic reconnect does not prompt.");
        }
        bool background = Host.ActiveTab != this;
        if (background)
            Host.ActivateTab(this);
        return show(background);
    }

    private void OnConnected(HostEntry host)
    {
        if (_reconnectAttempt > 0)
            Host.ShowToast($"Reconnected to {host.DisplayName}.", ToastKind.Success);
        _connectedAtMs = UiClock.NowMs; // the reconnect count starts over once the session has lasted (see OnSessionState)
        _session?.Resize(_terminal.Cols, _terminal.Rows, 0, 0); // in case the grid changed while connecting
        _overlay.Hide();
        _terminal.InputEnabled = true;
        SetStatus(TabStatus.Connected, $"{_address} · Connected");
        if (_session is { } connected)
            OnTunnelsChanged(connected);
        if (Host.ActiveTab == this && !Host.HasModal)
            Host.SetActiveKeyboardElement(_terminal);
        InvalidateLayout();
        if (IsSaved)
            Host.RunVault(() => Host.Services.Vault.TouchHostAsync(host.Id));
        if (_initialInput is not null && !_initialInputSent)
        {
            _initialInputSent = true;
            _session?.SendText(_initialInput);
        }
        // The shell may have exited before this continuation ran; its Closed notification was ignored then.
        if (_session is { State: SessionState.Closed or SessionState.Failed } ended)
            OnSessionState(ended, ended.State, ended.LastError ?? "Connection closed.");
    }

    private void OnSessionState(SshSession session, SessionState state, string? message)
    {
        if (session != _session || _closing)
            return;
        switch (state)
        {
            case SessionState.Connecting or SessionState.Authenticating when message is not null:
                ShowConnecting(message);
                break;
            case SessionState.Closed or SessionState.Failed when _terminal.InputEnabled:
                // An established session ended: keep the output readable and offer to reconnect.
                _terminal.InputEnabled = false;
                SetTunnels([]);
                bool lost = state == SessionState.Failed;
                if (lost && EffectiveOptions.Resolve(Host.Services.Vault.Current, CurrentHost).AutoReconnect.Value)
                {
                    _lostMessage = message ?? "Connection lost.";
                    // A session lost soon after it opened counts as one more failed attempt, so a host that accepts and
                    // then drops every connection backs off and gives up like one that can't be reached.
                    if (UiClock.NowMs - _connectedAtMs >= StableConnectionMs)
                        _reconnectAttempt = 0;
                    if (_reconnectAttempt < MaxReconnectAttempts)
                    {
                        ScheduleReconnect();
                        break;
                    }
                    _reconnectAttempt = 0;
                    message = $"Gave up reconnecting after {MaxReconnectAttempts} attempts. {_lostMessage}";
                }
                SetStatus(lost ? TabStatus.Failed : TabStatus.Closed, $"{_address} · {(lost ? "Connection lost" : "Closed")}");
                _overlay.ShowEnded(lost ? $"{message} Press Enter or click Reconnect."
                    : "Session closed — press Enter or click Reconnect", lost);
                InvalidateLayout();
                break;
        }
    }

    // ---- auto-reconnect ----

    private void ScheduleReconnect()
    {
        int delay = ReconnectDelaysSeconds[Math.Min(_reconnectAttempt, ReconnectDelaysSeconds.Length - 1)];
        _reconnectAttempt++;
        _reconnectAt = UiClock.NowMs + delay * 1000L;
        _reconnectShownSeconds = -1;
        SetStatus(TabStatus.Failed, $"{_address} · Connection lost");
        UpdateCountdown();
    }

    private void OnTick()
    {
        if (_reconnectAt < 0)
            return;
        if (UiClock.NowMs >= _reconnectAt)
            Connect(automatic: true, unattended: true);
        else
            UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        int seconds = (int)Math.Ceiling((_reconnectAt - UiClock.NowMs) / 1000.0);
        if (seconds == _reconnectShownSeconds)
            return;
        _reconnectShownSeconds = seconds;
        string attempt = _reconnectAttempt > 1 ? $" (attempt {_reconnectAttempt} of {MaxReconnectAttempts})" : "";
        _overlay.ShowReconnecting($"Connection lost — reconnecting in {seconds}s…{attempt}", waiting: true);
        InvalidateLayout();
    }

    /// <summary>
    /// Stops reconnecting automatically; the banner then offers a manual Reconnect. <paramref name="need"/> is what an
    /// unattended attempt stopped for (the tab is flagged, since it may be in the background).
    /// </summary>
    private void StopReconnecting(string? need = null)
    {
        _reconnectAt = -1;
        _reconnectAttempt = 0;
        _generation++; // a running attempt's result is ignored
        CancelAttempt();
        _session?.Dispose();
        _session = null;
        SetStatus(TabStatus.Failed, $"{_address} · Connection lost");
        _overlay.ShowEnded(need is null ? $"{_lostMessage} Press Enter or click Reconnect."
            : $"Reconnecting needs {need}: press Enter or click Reconnect. {_lostMessage}", true);
        if (need is not null)
            RequestAttention();
        InvalidateLayout();
    }

    private void ShowConnecting(string detail)
    {
        SetStatus(TabStatus.Connecting, $"{_address} · {(_reconnectAttempt > 0 ? "Reconnecting…" : "Connecting…")}");
        if (_reconnectAttempt > 0)
            _overlay.ShowReconnecting($"Connection lost — reconnecting (attempt {_reconnectAttempt} of {MaxReconnectAttempts})… {detail}", waiting: false);
        else
            _overlay.ShowConnecting($"Connecting to {_address}", detail);
        InvalidateLayout();
    }

    private void ShowFailed(string message)
    {
        _reconnectAttempt = 0;
        SetStatus(TabStatus.Failed, $"{_address} · Failed");
        _overlay.ShowFailed("Couldn't connect", message, IsSaved ? "Edit host…" : null);
        InvalidateLayout();
    }

    private void ShowNotConnected(string message)
    {
        _reconnectAttempt = 0;
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
        _overlay.ShowFailed("Not connected", message, IsSaved ? "Edit host…" : null, error: false);
        InvalidateLayout();
    }

    private void OnOverlayPrimary()
    {
        if (_overlay.Mode == SessionOverlay.OverlayMode.Reconnecting)
        {
            if (_reconnectAt >= 0)
                Connect(automatic: true); // "Reconnect now"
            return;
        }
        Connect();
    }

    private void OnOverlaySecondary()
    {
        if (_overlay.Mode == SessionOverlay.OverlayMode.Reconnecting)
            StopReconnecting();
        else if (_overlay.Mode == SessionOverlay.OverlayMode.Connecting)
            CancelAttempt();
        else if (Host.Services.Vault.Current.FindHost(_host.Id) is { } saved)
            Host.EditHost(saved);
    }

    // Cancels the running attempt (closing its dialogs). Its task then reports "cancelled" unless a newer attempt started.
    private void CancelAttempt()
    {
        _attempt?.Cancel();
        _attempt = null;
    }

    // ---- tunnels ----

    private void OnTunnelsChanged(SshSession session)
    {
        if (session != _session || _closing || session.State != SessionState.Connected)
            return;
        IReadOnlyList<TunnelStatus> tunnels = session.Tunnels;
        SetTunnels(tunnels);
        // Each failure is reported once per tab (reconnects start the same tunnels again).
        List<TunnelStatus> failed = tunnels.Where(t => t.State == TunnelState.Failed && _reportedTunnelFailures.Add(t.Forward.Id)).ToList();
        if (failed.Count == 1)
            Host.ShowToast($"Tunnel {failed[0].Forward} failed: {failed[0].Error}", ToastKind.Error);
        else if (failed.Count > 1)
            Host.ShowToast($"{failed.Count} tunnels failed to start. Click the tunnel chip in the status bar for details.", ToastKind.Error);
    }

    // Tab and split-view actions in the terminal's context menu; they also show the terminal-safe shortcuts.
    private IEnumerable<MenuItem> TabMenuItems()
    {
        if (_terminal.InputEnabled)
            yield return new MenuItem { Text = "Disconnect", Icon = "logout", Action = () => _session?.Disconnect() };
        yield return new MenuItem { Text = "Browse files", Icon = "folder", Action = BrowseFiles };
        yield return new MenuItem { Text = "New tab", Icon = "plus", Hint = "Ctrl+Shift+T", Action = Host.NewTab };
        yield return MenuItem.Separator;
        foreach (MenuItem item in Host.PaneMenuItems(this))
            yield return item;
        yield return MenuItem.Separator;
        yield return new MenuItem { Text = "Close tab", Icon = "x", Hint = "Ctrl+Shift+W", Action = () => Host.CloseTab(this) };
    }

    /// <summary>Opens this host's files (SFTP) in a new tab, trying the password typed here first.</summary>
    public void BrowseFiles() => Host.OpenFiles(CurrentHost, null, _password);

    private void OnGridResized(int cols, int rows)
    {
        SetSizeText($"{cols}×{rows}");
        _session?.Resize(cols, rows, 0, 0);
    }
}
