using System;
using System.Collections.Generic;
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
/// a password or a host-key decision when needed, and offers Retry / Reconnect after failures and disconnects.
/// </summary>
/// <remarks>
/// SSH output is queued into the <see cref="TerminalView"/> from the session's reader thread; state changes are
/// marshalled to the UI thread. Each connection attempt uses a fresh <see cref="SshSession"/> (sessions are single-use)
/// and starts from a terminal reset to its default modes, keeping the previous session's output as history.
/// </remarks>
public sealed class SessionTabContent : TabContent, IKeyInput
{
    private readonly HostEntry _host;
    private readonly string? _initialInput;
    private readonly SessionOverlay _overlay = new();
    private TerminalView _terminal = null!;
    private volatile SshSession? _session; // also read by the session's reader thread
    private CancellationTokenSource? _attempt;
    private int _generation; // identifies the current connection attempt; older attempts ignore their results
    private string? _password; // typed at the prompt; reused when this tab reconnects
    private string _address = "";
    private bool _started, _closing, _initialInputSent;

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
        _overlay.PrimaryClicked += Connect;
        _overlay.SecondaryClicked += OnOverlaySecondary;
    }

    public override VisualElement? DefaultFocus => _terminal;

    public override bool WantsControlKeys => true;

    private bool IsSaved => Host.Services.Vault.Current.FindHost(_host.Id) is not null;

    public override void OnAttached()
    {
        _terminal = new TerminalView(Host.Services.Prefs.Terminal);
        _terminal.Input += OnTerminalInput;
        _terminal.GridResized += OnGridResized;
        _terminal.TitleChanged += title => SetTitle(string.IsNullOrWhiteSpace(title) ? _host.DisplayName : title);
        _terminal.BellRang += RequestAttention;
        _terminal.ExtraMenuItems = TabMenuItems;
        AddChild(_terminal);
        AddChild(_overlay);
        Host.Services.PrefsChanged += OnPrefsChanged;
        _address = HostFormat.Address(_host, Host.Services.Vault.Current);
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
    }

    public override void OnActivated()
    {
        if (_started)
            return;
        _started = true;
        // After this input event: the tab is laid out by then, so the first request carries the real grid size.
        UiThread.Post(Connect);
    }

    public override void OnClosing()
    {
        _closing = true;
        Host.Services.PrefsChanged -= OnPrefsChanged;
        CancelAttempt();
        _session?.Dispose();
        _session = null;
    }

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        if (_overlay.Mode == SessionOverlay.OverlayMode.Ended)
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
            && _overlay.Mode is SessionOverlay.OverlayMode.Failed or SessionOverlay.OverlayMode.Ended)
        {
            Connect();
            return true;
        }
        if (key.Is(Key.Escape) && _overlay.Mode == SessionOverlay.OverlayMode.Connecting)
        {
            CancelAttempt();
            return true;
        }
        return false;
    }

    public void OnText(string text) { }

    private async void Connect()
    {
        if (_closing)
            return;
        CancelAttempt();
        _session?.Dispose();
        _session = null;
        int generation = ++_generation;
        _attempt = new CancellationTokenSource();
        CancellationToken ct = _attempt.Token;

        // Saved hosts are re-read so edits (e.g. from "Edit host…" after a failure) apply to the retry.
        var vault = Host.Services.Vault;
        HostEntry host = vault.Current.FindHost(_host.Id) ?? _host;
        Identity? identity = vault.Current.FindIdentity(host.IdentityId);
        _address = HostFormat.Address(host, vault.Current);
        _terminal.InputEnabled = false;
        _terminal.ResetSession(); // e.g. a dropped vim or tmux left the alternate screen and mouse reporting on
        ShowConnecting("Opening connection…");
        ForceLayoutSubtree(); // the terminal takes back the rows of an "ended" banner before its size is sent

        SshConnectRequest request = SshConnectRequest.ForHost(host, identity);
        try
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                string? user = await AskAsync(() => new PromptDialog(Host, "Username required", _address, null, "Username", "Continue")
                    .ShowAsync(ct));
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

            bool usesKey = !string.IsNullOrWhiteSpace(request.PrivateKey);
            string? password = usesKey ? null : _password ?? request.Password;
            string? error = null;
            while (true)
            {
                if (!usesKey)
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        password = await AskAsync(() => PasswordPromptDialog.ShowAsync(Host, _address, error, ct));
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

                SshSession session = CreateSession();
                _session = session;
                try
                {
                    // The grid may have changed while a prompt was open; later changes are sent as window-change.
                    await session.ConnectAsync(request with { Cols = _terminal.Cols, Rows = _terminal.Rows }, ct);
                    break;
                }
                catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && !usesKey && !ct.IsCancellationRequested)
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
            if (generation == _generation)
                ShowFailed(ex.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation && !_closing)
                ShowNotConnected("The connection attempt was cancelled.");
            return;
        }

        if (generation == _generation && !_closing)
            OnConnected(host);
    }

    private SshSession CreateSession()
    {
        var verifier = new KnownHostsVerifier(Host.Services.Vault, (info, ct) =>
            UiThread.InvokeAsync(() => AskAsync(() => HostKeyDialog.ShowAsync(Host, info, ct))));
        var session = new SshSession(verifier, (prompt, ct) =>
            UiThread.InvokeAsync(() => AskAsync(() => SignInPromptDialog.ShowAsync(Host, _address, prompt, ct))));
        session.DataReceived += (buffer, count) =>
        {
            // A replaced session's reader may still deliver a last chunk: it must not reach the new session's screen.
            if (session == _session)
                _terminal.Write(buffer.AsSpan(0, count));
        };
        session.StateChanged += (state, message) => UiThread.Post(() => OnSessionState(session, state, message));
        return session;
    }

    private void OnTerminalInput(byte[] bytes)
    {
        if (_session is { } session && !session.Send(bytes) && session.State == SessionState.Connected)
            Host.ShowToast("The server is not accepting input right now, so the last input was dropped.", ToastKind.Error);
    }

    // Prompts belong to this tab: bring it to the front first so the user knows which connection is asking.
    private Task<T> AskAsync<T>(Func<Task<T>> show)
    {
        if (Host.ActiveTab != this)
            Host.ActivateTab(this);
        return show();
    }

    private void OnConnected(HostEntry host)
    {
        _session?.Resize(_terminal.Cols, _terminal.Rows, 0, 0); // in case the grid changed while connecting
        _overlay.Hide();
        _terminal.InputEnabled = true;
        SetStatus(TabStatus.Connected, $"{_address} · Connected");
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
                bool lost = state == SessionState.Failed;
                SetStatus(lost ? TabStatus.Failed : TabStatus.Closed, $"{_address} · {(lost ? "Connection lost" : "Closed")}");
                _overlay.ShowEnded(lost ? $"{message} Press Enter or click Reconnect."
                    : "Session closed — press Enter or click Reconnect", lost);
                InvalidateLayout();
                break;
        }
    }

    private void ShowConnecting(string detail)
    {
        SetStatus(TabStatus.Connecting, $"{_address} · Connecting…");
        _overlay.ShowConnecting($"Connecting to {_address}", detail);
        InvalidateLayout();
    }

    private void ShowFailed(string message)
    {
        SetStatus(TabStatus.Failed, $"{_address} · Failed");
        _overlay.ShowFailed("Couldn't connect", message, IsSaved ? "Edit host…" : null);
        InvalidateLayout();
    }

    private void ShowNotConnected(string message)
    {
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
        _overlay.ShowFailed("Not connected", message, IsSaved ? "Edit host…" : null, error: false);
        InvalidateLayout();
    }

    private void OnOverlaySecondary()
    {
        if (_overlay.Mode == SessionOverlay.OverlayMode.Connecting)
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

    // Tab actions in the terminal's context menu; they also show the terminal-safe shortcuts.
    private IEnumerable<MenuItem> TabMenuItems()
    {
        if (_terminal.InputEnabled)
            yield return new MenuItem { Text = "Disconnect", Icon = "logout", Action = () => _session?.Disconnect() };
        yield return new MenuItem { Text = "New tab", Icon = "plus", Hint = "Ctrl+Shift+T", Action = Host.NewTab };
        yield return new MenuItem { Text = "Close tab", Icon = "x", Hint = "Ctrl+Shift+W", Action = () => Host.CloseTab(this) };
    }

    private void OnGridResized(int cols, int rows)
    {
        SetSizeText($"{cols}×{rows}");
        _session?.Resize(cols, rows, 0, 0);
    }

    private void OnPrefsChanged() => _terminal.ApplySettings(Host.Services.Prefs.Terminal);
}
