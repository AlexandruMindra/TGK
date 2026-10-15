using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Terminal;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Button = TGK.Client.Controls.Button;

namespace TGK.Client.Main;

/// <summary>
/// Runs one command on several saved hosts at once: a group's, or any checked in its list. At most
/// <see cref="MultiHostRun.DefaultMaxParallel"/> hosts run at a time, each over its own connection (jump hosts,
/// credentials and host-key checks as for a terminal), optionally as root through sudo, without a terminal and with stdin
/// closed. The list shows how each host is doing; the read-only terminal on the right shows each host's output once it
/// is done, all hosts one after the other, or one host alone when it is clicked. Not saved by "reopen my tabs".
/// </summary>
public sealed class RunCommandTabContent : TabContent
{
    private static readonly (string Text, TimeSpan Limit)[] Limits =
    [
        ("1 minute", TimeSpan.FromMinutes(1)),
        ("5 minutes", TimeSpan.FromMinutes(5)),
        ("30 minutes", TimeSpan.FromMinutes(30)),
        ("2 hours", TimeSpan.FromHours(2)),
    ];

    private const int DefaultLimit = 1, MaxBlockLines = 200, MaxHistory = 50;
    private const float Pad = 16, FieldH = 38, RunW = 96, OptionsH = 30, CaptionH = 34, ListMinW = 220, ListMaxW = 340;
    private const string ClearScreen = "\u001b[0m\u001b[?25l\u001b[H\u001b[2J\u001b[3J"; // and no cursor: nothing is typed there

    private readonly IReadOnlyList<Guid> _initial;
    private readonly Backdrop _toolbar = new(bottom: true), _sideBar = new(right: true), _outputBar = new(bottom: true);
    private readonly TextField _command;
    private readonly Button _runButton;
    private readonly Checkbox _sudo;
    private readonly Label _limitCaption;
    private readonly Dropdown _limit;
    private readonly Label _summary;
    private readonly Label _hostsCaption;
    private readonly ScrollContainer _scroll;
    private readonly RunHostList _list;
    private readonly Label _outputCaption;
    private readonly Button _showAll;
    // TerminalView.Write blocks while the terminal's backlog is full, and the backlog is parsed on the UI thread: the
    // output is written from a worker.
    private readonly Channel<string> _feed = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<string> _history = [];
    private Dictionary<Guid, string> _addresses = [];
    private TerminalView _terminal = null!;
    private RunConnector _connector = null!;
    private MultiHostRun? _run; // the current run, or the last one
    private CancellationTokenSource? _stop; // while _run is going
    private DateTimeOffset _startedAt, _endedAt;
    private bool _sudoRun, _closing;
    private int _historyIndex = -1;
    private string _draft = ""; // what was in the field before going back in the history
    private long _lastPaintSecond;

    /// <param name="hosts">The hosts checked at first (e.g. a group's).</param>
    public RunCommandTabContent(IEnumerable<Guid> hosts)
    {
        _initial = hosts.ToList();
        SetTitle("Run");
        _command = new TextField("Command to run on the checked hosts, e.g. uptime")
        {
            LeadingIcon = "terminal",
            Mono = true,
            ShowClearButton = true,
            FontSize = Theme.FontMd,
        };
        _command.Submitted += Run;
        _command.Changed += _ => _command.HasError = false;
        _command.KeyPreview = OnCommandKey;
        _runButton = new Button("Run", ButtonVariant.Primary, "bolt") { FontSize = Theme.FontMd };
        _runButton.Clicked += () =>
        {
            if (_stop is not null)
                Stop();
            else
                Run();
        };
        _sudo = new Checkbox("As root (sudo)");
        _limitCaption = new Label("Stop after", Theme.FontBase, Theme.TextSecondary);
        _limit = new Dropdown { Options = Limits.Select(l => l.Text).ToList(), SelectedIndex = DefaultLimit };
        _summary = new Label("", Theme.FontSm, Theme.TextMuted) { Align = TextAlignment.Right };
        _hostsCaption = new Label("HOSTS", Theme.FontXs, Theme.TextSecondary, Theme.WeightSemibold);
        _list = new RunHostList();
        _list.CheckedChanged += OnCheckedChanged;
        _list.FocusChanged += _ => ShowOutput();
        _scroll = new ScrollContainer
        {
            OverflowX = OverflowMode.Clip,
            ScrollbarVisibilityX = ScrollbarVisibility.Hidden,
            ScrollbarThickness = 6,
            ScrollbarRadius = 3,
            ScrollbarThumbColor = Theme.BorderStrong,
            ScrollbarTrackColor = SKColors.Transparent,
            Style = new ElementStyle(),
        };
        _scroll.AddChild(_list);
        _list.HeightChanged += InvalidateLayout;
        _outputCaption = new Label("OUTPUT", Theme.FontXs, Theme.TextSecondary, Theme.WeightSemibold);
        _showAll = new Button("All hosts", ButtonVariant.Ghost, "layout-rows") { Visible = false };
        _showAll.Clicked += () =>
        {
            _list.Focused = null;
            ShowOutput();
        };
        foreach (VisualElement element in new VisualElement[] { _toolbar, _sideBar, _outputBar, _command, _runButton, _sudo, _limitCaption, _limit, _summary,
                     _hostsCaption, _scroll, _outputCaption, _showAll })
            AddChild(element);
    }

    public override VisualElement? DefaultFocus => _command;

    /// <summary>The command field (for the dev scene).</summary>
    internal TextField CommandField => _command;

    public override void OnAttached()
    {
        _connector = new RunConnector(Host);
        _terminal = new TerminalView(EffectiveOptions.Terminal(Options())) { InputEnabled = false };
        AddChild(_terminal);
        ApplyAppearance();
        _list.Check(_initial, Host.Services.Vault.Current);
        _ = Task.Run(FeedAsync);
        ShowOutput();
        OnCheckedChanged();
        UiClock.Tick += OnTick;
    }

    public override void OnClosing()
    {
        _closing = true;
        UiClock.Tick -= OnTick;
        _stop?.Cancel(); // disposed once the run has wound down (OnRunOver)
        _connector.Dispose();
        _feed.Writer.TryComplete();
    }

    /// <summary>The vault changed: hosts may have been added, renamed or deleted, and the terminal settings changed.</summary>
    public void OnVaultChanged()
    {
        _list.Rebuild(Host.Services.Vault.Current);
        ApplyAppearance();
        OnCheckedChanged();
    }

    // Not a host's: the defaults for all hosts (Settings → Terminal) apply to the output.
    private EffectiveHostOptions Options() => EffectiveOptions.Resolve(null, null, Host.Services.Vault.Current.Defaults);

    private void ApplyAppearance()
    {
        EffectiveHostOptions options = Options();
        _terminal.ApplySettings(EffectiveOptions.Terminal(options));
        _terminal.Scheme = ColorScheme.Find(options.ColorScheme.Value);
    }

    // ---- running ----

    private void Run()
    {
        if (_stop is not null)
            return;
        string command = _command.Text.Trim();
        IReadOnlyList<HostEntry> hosts = _list.CheckedHosts;
        if (command.Length == 0)
        {
            _command.HasError = true;
            _command.Focus();
            SetSummary("Type the command to run.", Theme.Danger);
            return;
        }
        if (hosts.Count == 0)
        {
            SetSummary("Check the hosts to run it on.", Theme.Danger);
            return;
        }
        Remember(command);
        VaultData vault = Host.Services.Vault.Current;
        bool sudo = _sudo.Checked, shareable = hosts.Count > 1;
        TimeSpan limit = Limits[Math.Max(0, _limit.SelectedIndex)].Limit;
        var run = new MultiHostRun(command, hosts);
        var stop = new CancellationTokenSource();
        _run = run;
        _stop = stop;
        _sudoRun = sudo;
        _startedAt = DateTimeOffset.UtcNow;
        _addresses = hosts.ToDictionary(h => h.Id, h => HostFormat.Address(h, vault));
        _list.ClearRuns();
        foreach (HostRun host in run.Hosts)
            _list.SetRun(host.Host.Id, host);
        _list.Locked = true;
        _sudo.Enabled = _limit.Enabled = false;
        _runButton.Text = "Stop";
        _runButton.Icon = "x";
        _runButton.Variant = ButtonVariant.Secondary;
        run.Changed += (_, host) => UiThread.Post(() => OnHostChanged(run, host));
        if (_list.Focused is { } focused && !hosts.Any(h => h.Id == focused))
            _list.Focused = null;
        ShowOutput();
        UpdateState();
        run.RunAsync((host, progress, ct) => _connector.RunAsync(vault, host, command, sudo, limit, shareable, progress, ct), stop.Token)
            .ContinueWith(_ => UiThread.Post(() => OnRunOver(run, stop)), TaskScheduler.Default);
    }

    private void Stop()
    {
        if (_stop is not { IsCancellationRequested: false } stop)
            return;
        stop.Cancel();
        SetSummary("Stopping…", Theme.Warning);
    }

    private void OnHostChanged(MultiHostRun run, HostRun host)
    {
        if (_closing || run != _run)
            return;
        _list.SetRun(host.Host.Id, host);
        if (_list.Focused == host.Host.Id)
            ShowOutput();
        else if (_list.Focused is null && host.IsOver)
            Feed(Block(host, MaxBlockLines));
        UpdateState();
    }

    private void OnRunOver(MultiHostRun run, CancellationTokenSource stop)
    {
        stop.Dispose();
        if (_closing || run != _run)
            return;
        _stop = null;
        _endedAt = DateTimeOffset.UtcNow;
        _list.Locked = false;
        _sudo.Enabled = _limit.Enabled = true;
        _runButton.Text = "Run";
        _runButton.Icon = "bolt";
        _runButton.Variant = ButtonVariant.Primary;
        if (_list.Focused is null)
            Feed(Footer(run));
        UpdateState();
        RequestAttention();
    }

    // The summary beside the options, the status bar and the tab's dot.
    private void UpdateState()
    {
        IReadOnlyList<HostEntry> checkedHosts = _list.CheckedHosts;
        if (_run is not { } run)
        {
            SetSummary(checkedHosts.Count == 0 ? "Check the hosts to run the command on." : $"Runs on {Hosts(checkedHosts.Count)}, {MultiHostRun.DefaultMaxParallel} at a time.", Theme.TextMuted);
            SetStatus(TabStatus.None, $"Run a command · {Hosts(checkedHosts.Count)} checked");
            return;
        }
        IReadOnlyList<HostRun> hosts = run.Hosts;
        string outcome = MultiHostRun.Summarize(hosts);
        if (_stop is not null)
        {
            int done = hosts.Count(h => h.IsOver);
            if (!_stop.IsCancellationRequested)
                SetSummary($"Running · {done} of {hosts.Count} done", Theme.Warning);
            SetStatus(TabStatus.Connecting, $"{run.Command} · {outcome}");
            return;
        }
        bool allGood = hosts.All(h => h.State == HostRunState.Succeeded);
        bool anyBad = hosts.Any(h => h.State is HostRunState.Failed or HostRunState.Error);
        SetSummary($"Done in {CommandOutput.Duration(_endedAt - _startedAt)} · {outcome}", allGood ? Theme.Success : anyBad ? Theme.Danger : Theme.TextMuted);
        SetStatus(allGood ? TabStatus.Connected : anyBad ? TabStatus.Failed : TabStatus.Closed, $"{run.Command} · {outcome}");
    }

    private void SetSummary(string text, SKColor color)
    {
        _summary.Text = text;
        _summary.Color = color;
    }

    private void OnCheckedChanged()
    {
        IReadOnlyList<HostEntry> hosts = _list.CheckedHosts;
        _hostsCaption.Text = $"HOSTS · {hosts.Count} CHECKED";
        SetTitle($"Run · {Subject(hosts)}");
        if (_stop is null)
            UpdateState();
    }

    // What the checked hosts are: a group (all of its hosts and no other), the one host, or how many.
    private string Subject(IReadOnlyList<HostEntry> hosts)
    {
        VaultData vault = Host.Services.Vault.Current;
        var ids = hosts.Select(h => h.Id).ToHashSet();
        if (hosts.Count > 1 && vault.Groups.FirstOrDefault(g => ids.SetEquals(vault.Hosts.Where(h => h.GroupId == g.Id).Select(h => h.Id))) is { } group)
            return group.Name;
        return hosts.Count == 1 ? hosts[0].DisplayName : Hosts(hosts.Count);
    }

    private static string Hosts(int n) => n == 1 ? "1 host" : $"{n} hosts";

    private void OnTick()
    {
        // Running hosts show their time in seconds.
        long second = UiClock.NowMs / 1000;
        if (_stop is null || second == _lastPaintSecond)
            return;
        _lastPaintSecond = second;
        _list.InvalidatePaint();
    }

    // ---- output ----

    /// <summary>Writes the output anew: all hosts that are done, in the order they finished, or the clicked host alone.</summary>
    private void ShowOutput()
    {
        if (_closing)
            return;
        var sb = new StringBuilder(ClearScreen);
        HostEntry? focusedHost = _list.Focused is { } id ? Host.Services.Vault.Current.FindHost(id) : null;
        Guid? focused = focusedHost?.Id; // a host deleted meanwhile: all hosts
        _outputCaption.Text = focusedHost is null ? "OUTPUT · ALL HOSTS" : $"OUTPUT · {focusedHost.DisplayName.ToUpperInvariant()}";
        _showAll.Visible = focusedHost is not null;
        InvalidateLayout();
        if (_run is not { } run)
        {
            sb.Append("\u001b[2mThe command runs on every checked host, ").Append(MultiHostRun.DefaultMaxParallel)
                .Append(" at a time, without a terminal (a command waiting for input gets end-of-file). ")
                .Append("Each host's output appears here when it is done; click a host in the list to see its output alone.\u001b[0m\r\n");
            if (focusedHost is not null)
                sb.Append("\r\n").Append(CommandOutput.Clean(focusedHost.DisplayName, false)).Append(" has not run anything in this tab yet.\r\n");
        }
        else if (focused is { } one)
        {
            HostRun? host = run.Hosts.FirstOrDefault(h => h.Host.Id == one);
            sb.Append(CommandOutput.RunHeader(run.Command, 1, _sudoRun, _startedAt));
            sb.Append(host is null
                ? $"\u001b[2m{CommandOutput.Clean(focusedHost?.DisplayName ?? "This host", false)} was not checked for this run.\u001b[0m\r\n"
                : Block(host, int.MaxValue));
        }
        else
        {
            IReadOnlyList<HostRun> hosts = run.Hosts;
            sb.Append(CommandOutput.RunHeader(run.Command, hosts.Count, _sudoRun, _startedAt));
            foreach (int index in run.Finished)
                sb.Append(Block(hosts[index], MaxBlockLines));
            if (_stop is null)
                sb.Append(Footer(run));
        }
        _terminal.ResetSession(); // drops output still queued: it is all written again
        Feed(sb.ToString());
    }

    private string Block(HostRun host, int maxLines) =>
        CommandOutput.HostBlock(host, _addresses.GetValueOrDefault(host.Host.Id) ?? host.Host.Host, maxLines);

    private string Footer(MultiHostRun run) =>
        $"\u001b[2m── Done in {CommandOutput.Duration(_endedAt - _startedAt)}: {MultiHostRun.Summarize(run.Hosts)}.\u001b[0m\r\n";

    private void Feed(string text) => _feed.Writer.TryWrite(text);

    private async Task FeedAsync()
    {
        await foreach (string text in _feed.Reader.ReadAllAsync().ConfigureAwait(false))
            _terminal.Write(Encoding.UTF8.GetBytes(text));
    }

    // ---- the command field ----

    private void Remember(string command)
    {
        _history.Remove(command);
        _history.Add(command);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
        _historyIndex = -1;
    }

    // Up and Down go through the commands run in this tab, as in a shell.
    private bool OnCommandKey(KeyStroke k)
    {
        if (k.Modifiers != KeyModifiers.None || _history.Count == 0 || k.Key is not (Key.Up or Key.Down))
            return false;
        if (_historyIndex < 0)
        {
            if (k.Key == Key.Down)
                return true;
            _draft = _command.Text;
            _historyIndex = _history.Count;
        }
        _historyIndex += k.Key == Key.Up ? -1 : 1;
        if (_historyIndex < 0)
            _historyIndex = 0;
        if (_historyIndex >= _history.Count)
        {
            _historyIndex = -1;
            _command.Text = _draft;
        }
        else
        {
            _command.Text = _history[_historyIndex];
        }
        _command.SelectAll();
        return true;
    }

    // ---- layout ----

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        float y = Pad;
        _command.Transform.SetLocalFrame(Pad, y, Math.Max(80, w - 2 * Pad - RunW - 8), FieldH);
        _runButton.Transform.SetLocalFrame(w - Pad - RunW, y, RunW, FieldH);
        y += FieldH + 8;
        float x = Pad;
        float sudoW = _sudo.PreferredWidth;
        _sudo.Transform.SetLocalFrame(x, y, sudoW, OptionsH);
        x += sudoW + 24;
        float captionW = _limitCaption.MeasureWidth();
        _limitCaption.Transform.SetLocalFrame(x, y, captionW, OptionsH);
        x += captionW + 8;
        _limit.Transform.SetLocalFrame(x, y + 1, 120, OptionsH - 2);
        x += 120 + 16;
        _summary.Transform.SetLocalFrame(x, y, Math.Max(0, w - Pad - x), OptionsH);
        _summary.Visible = w - Pad - x > 60;
        y += OptionsH + 10;
        _toolbar.Transform.SetLocalFrame(0, 0, w, y);

        float bodyH = Math.Max(0, h - y);
        float listW = MathF.Round(Math.Clamp(w * 0.3f, Math.Min(ListMinW, w * 0.45f), ListMaxW));
        _sideBar.Transform.SetLocalFrame(0, y, listW, bodyH);
        _hostsCaption.Transform.SetLocalFrame(Pad, y + 8, listW - 2 * Pad, CaptionH - 16);
        float listTop = y + CaptionH;
        float listH = Math.Max(0, bodyH - CaptionH);
        _scroll.Transform.SetLocalFrame(0, listTop, listW - 1, listH);
        _list.Transform.SetLocalFrame(0, 0, listW - 1, Math.Max(listH, _list.ContentHeight));
        _scroll.InvalidateLayout();

        float outX = listW, outW = Math.Max(0, w - listW);
        _outputBar.Transform.SetLocalFrame(outX, y, outW, CaptionH);
        float allW = _showAll.PreferredWidth;
        _outputCaption.Transform.SetLocalFrame(outX + Pad, y + 8, Math.Max(0, outW - 2 * Pad - (_showAll.Visible ? allW + 8 : 0)), CaptionH - 16);
        _showAll.Transform.SetLocalFrame(outX + outW - 8 - allW, y + 3, allW, CaptionH - 6);
        _terminal.Transform.SetLocalFrame(outX, y + CaptionH, outW, Math.Max(0, bodyH - CaptionH));
        _terminal.FitToSize();
    }

    /// <summary>The background of a bar (or the host column) with a border on one side.</summary>
    private sealed class Backdrop : Control
    {
        private readonly bool _bottom, _right;

        public Backdrop(bool bottom = false, bool right = false)
        {
            (_bottom, _right) = (bottom, right);
            Style = new ElementStyle { BackColor = right ? Theme.Sidebar : Theme.Surface };
        }

        protected override void Paint(SKCanvas c)
        {
            if (_bottom)
                Gfx.Line(c, 0, H - 0.5f, W, H - 0.5f, Theme.Border);
            if (_right)
                Gfx.Line(c, W - 0.5f, 0, W - 0.5f, H, Theme.Border);
        }
    }
}
