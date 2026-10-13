using System;
using System.Collections.Generic;
using Blossom;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Terminal;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Pty;

namespace TGK.Client.Main;

/// <summary>
/// A terminal tab running a shell of this computer (<see cref="LocalShells"/>: the user's shell on Linux and macOS,
/// PowerShell, Command Prompt, WSL or Git Bash on Windows; Settings → Terminal picks it, per device). It starts when
/// first shown, in the home folder, with the terminal settings of the vault's defaults. When the shell exits normally
/// the tab closes, as in other terminals; otherwise (or right after it started) it stays, offering a restart.
/// </summary>
/// <remarks>
/// The shell's output is queued into the <see cref="TerminalView"/> from the PTY's reader thread; its exit is
/// marshalled to the UI thread. Agents (MCP) never see these tabs: they only reach the vault's hosts, over their own
/// connections.
/// </remarks>
public sealed class LocalTerminalTabContent : TabContent, IKeyInput
{
    private const long QuickExitMs = 2_000; // a shell that ends this soon after starting stays to show why

    private readonly SessionOverlay _overlay = new();
    private TerminalView _terminal = null!;
    private volatile PtyProcess? _pty; // also read by the PTY's reader thread
    private LocalShell? _shell;
    private long _startedAtMs;
    private bool _started, _closing;

    public LocalTerminalTabContent()
    {
        ReceivesKeyboard = true; // clicks on the overlay keep keys here, so Enter can restart
        Style = new ElementStyle { BackColor = Theme.TerminalBg };
        SetTitle("Terminal");
        _overlay.PrimaryClicked += Start;
        _overlay.SecondaryClicked += () => Host.ShowSettings(SettingsDialog.TerminalTab);
    }

    public override VisualElement? DefaultFocus => _terminal;

    public override WorkspaceTab SaveState() => new() { Kind = WorkspaceTab.LocalTerminalKind };

    public override bool WantsControlKeys => true;

    private string Place => _shell is null ? "This computer" : $"This computer · {_shell.Name}";

    public override void OnAttached()
    {
        _terminal = new TerminalView(EffectiveOptions.Terminal(Options()));
        _terminal.Input += bytes => _pty?.Write(bytes);
        _terminal.GridResized += OnGridResized;
        _terminal.TitleChanged += title => SetTitle(string.IsNullOrWhiteSpace(title) ? _shell?.Name ?? "Terminal" : title);
        _terminal.BellRang += RequestAttention;
        _terminal.ExtraMenuItems = TabMenuItems;
        AddChild(_terminal);
        AddChild(_overlay);
        ApplyAppearance();
        SetStatus(TabStatus.Closed, Place);
    }

    public override void OnShown()
    {
        if (_started)
            return;
        _started = true;
        // After this input event: the tab is laid out by then, so the shell starts with the real grid size.
        UiThread.Post(Start);
    }

    public override void OnClosing()
    {
        _closing = true;
        StopShell();
    }

    /// <summary>The vault changed: the default terminal settings may have.</summary>
    public void OnVaultChanged() => ApplyAppearance();

    // Local terminals belong to no host: the defaults for all hosts (Settings → Terminal) apply.
    private EffectiveHostOptions Options() => EffectiveOptions.Resolve(null, null, Host.Services.Vault.Current.Defaults);

    private void ApplyAppearance()
    {
        EffectiveHostOptions options = Options();
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

    // Keys reach the tab when the shell is not running (the terminal lets them bubble) or the overlay has focus.
    public bool OnKey(KeyStroke key)
    {
        if (key.IsEnter && key.Modifiers == KeyModifiers.None && !key.IsRepeat
            && _overlay.Mode is SessionOverlay.OverlayMode.Failed or SessionOverlay.OverlayMode.Ended)
        {
            Start();
            return true;
        }
        return false;
    }

    public void OnText(string text) { }

    /// <summary>Starts the shell chosen for this device (again, after it exited).</summary>
    private void Start()
    {
        if (_closing)
            return;
        StopShell();
        _shell = LocalShells.Find(Host.Services.Prefs.LocalShell);
        _terminal.InputEnabled = false;
        _terminal.ResetSession();
        _overlay.Hide();
        ForceLayoutSubtree(); // the terminal takes back the rows of an "exited" banner before its size is used
        if (_shell is null)
        {
            ShowFailed("No shell was found on this computer.");
            return;
        }
        SetTitle(_shell.Name);
        PtyProcess pty = PtyProcess.Create();
        pty.DataReceived += (buffer, count) =>
        {
            // A replaced shell may still deliver a last chunk: it must not reach the new one's screen.
            if (pty == _pty)
                _terminal.Write(buffer.AsSpan(0, count));
        };
        pty.Exited += code => UiThread.Post(() => OnExited(pty, code));
        _pty = pty;
        try
        {
            pty.Start(_shell.StartInfo(_terminal.Cols, _terminal.Rows, AppInfo.VersionText));
        }
        catch (PtyException ex)
        {
            _pty = null;
            pty.Dispose();
            Log.Warning($"Local terminal: {ex.Message}");
            ShowFailed(ex.Message);
            return;
        }
        _startedAtMs = UiClock.NowMs;
        _terminal.InputEnabled = true;
        SetStatus(TabStatus.Connected, Place);
        if (Host.ActiveTab == this && !Host.HasModal)
            Host.SetActiveKeyboardElement(_terminal);
        InvalidateLayout();
    }

    private void OnExited(PtyProcess pty, int code)
    {
        if (pty != _pty || _closing)
            return;
        _terminal.InputEnabled = false;
        if (code == 0 && UiClock.NowMs - _startedAtMs >= QuickExitMs)
        {
            Host.CloseTab(this); // "exit", as in any terminal
            return;
        }
        SetStatus(TabStatus.Closed, $"{Place} · Exited");
        _overlay.ShowEnded($"The shell exited{(code == 0 ? "" : $" with code {code}")}. Press Enter or click Restart.", code != 0, "Restart");
        InvalidateLayout();
    }

    private void ShowFailed(string message)
    {
        SetStatus(TabStatus.Failed, $"{Place} · Failed");
        _overlay.ShowFailed("Couldn't start a local terminal", message, "Choose shell…");
        InvalidateLayout();
    }

    private void StopShell()
    {
        PtyProcess? pty = _pty;
        _pty = null;
        pty?.Dispose();
    }

    private void OnGridResized(int cols, int rows)
    {
        SetSizeText($"{cols}×{rows}");
        _pty?.Resize(cols, rows);
    }

    // Tab and split-view actions in the terminal's context menu.
    private IEnumerable<MenuItem> TabMenuItems()
    {
        if (!_terminal.InputEnabled)
            yield return new MenuItem { Text = "Restart shell", Icon = "refresh", Action = Start };
        yield return new MenuItem { Text = "New local terminal", Icon = "terminal", Hint = MainView.LocalTerminalHint, Action = Host.OpenLocalTerminal };
        yield return new MenuItem { Text = "New tab", Icon = "plus", Hint = "Ctrl+Shift+T", Action = Host.NewTab };
        yield return MenuItem.Separator;
        foreach (MenuItem item in Host.PaneMenuItems(this))
            yield return item;
        yield return MenuItem.Separator;
        yield return new MenuItem { Text = "Close tab", Icon = "x", Hint = "Ctrl+Shift+W", Action = () => Host.CloseTab(this) };
    }
}
