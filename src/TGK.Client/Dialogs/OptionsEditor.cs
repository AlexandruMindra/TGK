using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blossom.Core.Visual;
using TGK.Client.Controls;
using TGK.Client.Terminal;
using TGK.Client.Views;
using TGK.Core.Models;

namespace TGK.Client.Dialogs;

/// <summary>Which level an <see cref="OptionsEditor"/> edits: it decides what "inherited" means.</summary>
public enum OptionsLevel
{
    /// <summary>The vault's connection defaults: they inherit the built-in defaults.</summary>
    Global,
    Group,
    Host,
}

/// <summary>A validation problem of an options editor: the page and field to show it on.</summary>
public sealed record OptionsError(string Message, FormPage Page, TextField? Field);

/// <summary>
/// The inheritable <see cref="HostOptions"/> on four pages (Connection, Session, Appearance, Agents), shared by the
/// host editor, the group settings and the settings dialog (which shows the appearance part on its Terminal page). An
/// empty field (or "Inherit") inherits; its caption says
/// what it inherits and from where, and a "Reset to …" link clears an overridden value.
/// </summary>
public sealed class OptionsEditor
{
    private const float Gap = 16, RowGap = 16, CaptionGap = 6, HelpH = 16, HelpGap = 6;
    private static readonly string[] TerminalTypes = ["xterm-256color", "xterm", "screen-256color", "tmux-256color", "vt100"];

    private readonly TgkView _view;
    private readonly OptionsLevel _level;
    private readonly Guid? _hostId;
    private readonly Func<EffectiveHostOptions> _resolveInherited;
    private readonly Func<HostOptions, VaultData> _candidate;
    private readonly FormPage _connection, _session, _appearance, _agents;
    private readonly Func<float, float>? _agentsHeader;
    private readonly List<Guid?> _jumpValues = [];
    private Guid? _deletedJump; // this level's own jump host, when that host no longer exists
    private EffectiveHostOptions _inherited;

    private readonly OptionCaption _jumpCaption, _keepAliveCaption, _timeoutCaption, _reconnectCaption;
    private readonly Dropdown _jump;
    private readonly Label _route, _reconnectHelp;
    private readonly TextField _keepAlive, _timeout;
    private readonly SegmentedControl _reconnect;

    private readonly OptionCaption _startupCaption, _termCaption, _envCaption;
    private readonly TextField _startup, _term;
    private readonly Label _startupHelp;
    private readonly EnvEditor _env;
    private bool _startupOverridden;

    private readonly OptionCaption _schemeCaption, _fontCaption, _familyCaption, _legacyCaption;
    private readonly SchemePicker _scheme;
    private readonly FontStepper _font;
    private readonly Dropdown _family;
    private readonly List<string?> _familyValues = [];
    private readonly SegmentedControl _legacy;
    private readonly Label _legacyHelp;

    private readonly OptionCaption _accessCaption, _commandsCaption, _pathsCaption;
    private readonly SegmentedControl _access;
    private readonly Label _accessHelp;
    private readonly RuleListEditor _commands, _paths;

    /// <param name="options">The values to edit (not modified; see <see cref="Store"/>).</param>
    /// <param name="inherited">What applies when this level sets nothing (called again by <see cref="RefreshInherited"/>).</param>
    /// <param name="candidate">
    /// The vault as it would be with the given options saved at this level: jump-host routes are checked against it,
    /// since a change of one level can create a loop through hosts, groups and defaults.
    /// </param>
    /// <param name="hostId">The edited host, which is not offered as its own jump host.</param>
    /// <param name="agentsHeader">Lays out the owner's own content at the top of the Agents page; returns its height.</param>
    /// <param name="arrangeAppearance">
    /// Lay out the appearance page; false when the owner places those controls on a page of its own
    /// (see <see cref="ArrangeAppearance"/>).
    /// </param>
    public OptionsEditor(TgkView view, HostOptions options, OptionsLevel level, Func<EffectiveHostOptions> inherited,
        Func<HostOptions, VaultData> candidate, FormPage connection, FormPage session, FormPage appearance, FormPage agents,
        Guid? hostId = null, Func<float, float>? agentsHeader = null, bool arrangeAppearance = true)
    {
        _view = view;
        _level = level;
        _hostId = hostId;
        _resolveInherited = inherited;
        _candidate = candidate;
        _inherited = inherited();
        (_connection, _session, _appearance, _agents) = (connection, session, appearance, agents);
        _agentsHeader = agentsHeader;
        string inheritWord = level == OptionsLevel.Global ? "Default" : "Inherit";

        // ---- Connection ----
        _jumpCaption = connection.Add(new OptionCaption("Jump host (ProxyJump)"));
        _jump = connection.Add(new Dropdown());
        _jump.SelectionChanged += _ => { UpdateJumpCaption(); UpdateRoute(); };
        _jumpCaption.ResetClicked += () => { _jump.SelectedIndex = 0; UpdateJumpCaption(); UpdateRoute(); };
        _route = connection.Add(new Label("", Theme.FontXs, Theme.TextMuted) { MaxLines = 2 });
        _keepAliveCaption = connection.Add(new OptionCaption("Keep-alive (seconds, 0 = off)"));
        _keepAlive = connection.Add(NumberField(_keepAliveCaption, 4));
        _timeoutCaption = connection.Add(new OptionCaption("Connect timeout (seconds)"));
        _timeout = connection.Add(NumberField(_timeoutCaption, 3));
        _reconnectCaption = connection.Add(new OptionCaption("Auto-reconnect"));
        _reconnect = connection.Add(TriState(_reconnectCaption, inheritWord));
        _reconnectHelp = connection.Add(new Label("Reconnects after an unexpected disconnect, waiting longer between attempts (at most 10).",
            Theme.FontXs, Theme.TextMuted) { MaxLines = 2 });
        _legacyCaption = connection.Add(new OptionCaption("Legacy algorithms (old network devices)"));
        _legacy = connection.Add(TriState(_legacyCaption, inheritWord));
        _legacyHelp = connection.Add(new Label("Weakens security: also allows SHA-1 and CBC algorithms.", Theme.FontXs, Theme.Warning) { MaxLines = 2 });

        // ---- Session ----
        _startupCaption = session.Add(new OptionCaption("Startup command"));
        _startup = session.Add(new TextField { Mono = true, MaxLength = 1024 });
        _startup.Changed += _ =>
        {
            _startupOverridden = true;
            _startupCaption.Overridden = true;
            UpdateStartupPlaceholder();
        };
        _startupCaption.ResetClicked += () =>
        {
            _startup.Text = "";
            _startupOverridden = false;
            _startupCaption.Overridden = false;
            UpdateStartupPlaceholder();
        };
        _startupHelp = session.Add(new Label("Typed into the shell once it opens, followed by Enter.", Theme.FontXs, Theme.TextMuted));
        _termCaption = session.Add(new OptionCaption("Terminal type (TERM)"));
        _term = session.Add(new TextField { Mono = true, MaxLength = 64, TrailingIcon = "chevron-down" });
        _term.Changed += text => _termCaption.Overridden = text.Trim().Length > 0;
        _term.TrailingClicked += ShowTerminalTypes;
        _termCaption.ResetClicked += () => { _term.Text = ""; _termCaption.Overridden = false; };
        _envCaption = session.Add(new OptionCaption("Environment variables"));
        _env = session.Add(new EnvEditor("Sent before the shell starts. OpenSSH only accepts names its AcceptEnv setting allows (often LANG, LC_*)."));
        _env.Changed += () =>
        {
            _envCaption.Overridden = _env.Overridden;
            LayoutChanged?.Invoke();
        };
        _envCaption.ResetClicked += _env.Reset;

        // ---- Appearance ----
        _schemeCaption = appearance.Add(new OptionCaption("Color scheme"));
        _scheme = appearance.Add(new SchemePicker(inheritWord));
        _scheme.Changed += () => { _schemeCaption.Overridden = _scheme.Selected is not null; AppearanceChanged?.Invoke(); };
        _schemeCaption.ResetClicked += () => { _scheme.Selected = null; _schemeCaption.Overridden = false; AppearanceChanged?.Invoke(); };
        _familyCaption = appearance.Add(new OptionCaption("Font"));
        _family = appearance.Add(new Dropdown());
        _family.SelectionChanged += _ => { _familyCaption.Overridden = SelectedFamily is not null; AppearanceChanged?.Invoke(); };
        _familyCaption.ResetClicked += () => { _family.SelectedIndex = 0; _familyCaption.Overridden = false; AppearanceChanged?.Invoke(); };
        _fontCaption = appearance.Add(new OptionCaption("Font size (px)"));
        _font = appearance.Add(new FontStepper());
        _font.Changed += () => { _fontCaption.Overridden = _font.Field.Text.Length > 0; AppearanceChanged?.Invoke(); };
        _fontCaption.ResetClicked += () => { _font.Value = null; _fontCaption.Overridden = false; AppearanceChanged?.Invoke(); };

        // ---- Agents ----
        _accessCaption = agents.Add(new OptionCaption("Agent access (MCP)"));
        _access = agents.Add(new SegmentedControl(inheritWord, "Off", "Read only", "Ask", "Full"));
        _access.SelectionChanged += i =>
        {
            _accessCaption.Overridden = i > 0;
            UpdateAccessHelp();
        };
        _accessCaption.ResetClicked += () => { _access.SelectedIndex = 0; _accessCaption.Overridden = false; UpdateAccessHelp(); };
        _accessHelp = agents.Add(new Label("", Theme.FontXs, Theme.TextMuted) { MaxLines = 3 });
        _commandsCaption = agents.Add(new OptionCaption("Allowed commands"));
        _commands = agents.Add(new RuleListEditor("Add command", "e.g. systemctl status *",
            "Run without asking. In Read only, the only commands besides built-in read-only ones (ls, cat, grep, git status…). * matches anything."));
        _commands.Changed += () =>
        {
            _commandsCaption.Overridden = _commands.Overridden;
            LayoutChanged?.Invoke();
        };
        _commandsCaption.ResetClicked += _commands.Reset;
        _pathsCaption = agents.Add(new OptionCaption("Protected paths"));
        _paths = agents.Add(new RuleListEditor("Add path", "e.g. /srv/secrets/**",
            "Always need approval (refused in Read only), like the built-in ones: ~/.ssh, private keys, .env, /etc/shadow… ** matches any depth."));
        _paths.Changed += () =>
        {
            _pathsCaption.Overridden = _paths.Overridden;
            LayoutChanged?.Invoke();
        };
        _pathsCaption.ResetClicked += _paths.Reset;

        connection.Layout = ArrangeConnection;
        session.Layout = ArrangeSession;
        if (arrangeAppearance)
            appearance.Layout = w => ArrangeAppearance(w, 0);
        agents.Layout = ArrangeAgents;
        Load(options);
        RefreshInherited();
    }

    /// <summary>The content height of a page changed (e.g. a variable was added): the owner re-lays out the dialog.</summary>
    public event Action? LayoutChanged;

    /// <summary>The color scheme, font or font size changed (e.g. to update a preview).</summary>
    public event Action? AppearanceChanged;

    /// <summary>The color scheme, font family and size as edited here, or inherited where nothing is set.</summary>
    public (string Scheme, string Family, float Size) Appearance =>
        (_scheme.Selected ?? _inherited.ColorScheme.Value, SelectedFamily ?? _inherited.FontFamily.Value,
            _font.Value is { } size && size >= HostOptions.MinFontSize && size <= HostOptions.MaxFontSize ? size : _inherited.FontSize.Value);

    private string? SelectedFamily => _familyValues.Count > 0 ? _familyValues[Math.Clamp(_family.SelectedIndex, 0, _familyValues.Count - 1)] : null;

    private VaultData Vault => _view.Services.Vault.Current;

    private TextField NumberField(OptionCaption caption, int maxLength)
    {
        var field = new TextField { DigitsOnly = true, MaxLength = maxLength, Mono = true };
        field.Changed += text => caption.Overridden = text.Length > 0;
        caption.ResetClicked += () => { field.Text = ""; caption.Overridden = false; };
        return field;
    }

    private static SegmentedControl TriState(OptionCaption caption, string inheritWord)
    {
        var control = new SegmentedControl(inheritWord, "On", "Off");
        control.SelectionChanged += i => caption.Overridden = i > 0;
        caption.ResetClicked += () => { control.SelectedIndex = 0; caption.Overridden = false; };
        return control;
    }

    private static bool? TriValue(SegmentedControl control) => control.SelectedIndex switch { 1 => true, 2 => false, _ => null };

    private static int TriIndex(bool? value) => value switch { true => 1, false => 2, null => 0 };

    // ---- load / store ----

    private void Load(HostOptions o)
    {
        _deletedJump = o.JumpHostId is { } jump && jump != HostOptions.NoJumpHost && Vault.FindHost(jump) is null ? jump : null;
        BuildJumpOptions();
        _jump.SelectedIndex = Math.Max(0, _jumpValues.IndexOf(o.JumpHostId));
        _keepAlive.Text = o.KeepAliveSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
        _timeout.Text = o.ConnectTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
        _reconnect.SelectedIndex = TriIndex(o.AutoReconnect);
        _startup.Text = o.StartupCommand ?? "";
        _startupOverridden = o.StartupCommand is not null;
        _term.Text = o.TerminalType ?? "";
        _env.Load(o.Environment);
        _scheme.Selected = o.ColorScheme;
        _font.Value = o.FontSize;
        BuildFamilyOptions(o.FontFamily);
        _legacy.SelectedIndex = TriIndex(o.LegacyAlgorithms);
        _access.SelectedIndex = o.AgentAccess is { } access ? (int)access + 1 : 0;
        _commands.Load(o.AgentCommands);
        _paths.Load(o.AgentProtectedPaths);

        UpdateJumpCaption();
        _keepAliveCaption.Overridden = o.KeepAliveSeconds is not null;
        _timeoutCaption.Overridden = o.ConnectTimeoutSeconds is not null;
        _reconnectCaption.Overridden = o.AutoReconnect is not null;
        _startupCaption.Overridden = _startupOverridden;
        _termCaption.Overridden = o.TerminalType is not null;
        _envCaption.Overridden = o.Environment is not null;
        _schemeCaption.Overridden = o.ColorScheme is not null;
        _fontCaption.Overridden = o.FontSize is not null;
        _familyCaption.Overridden = o.FontFamily is not null;
        _legacyCaption.Overridden = o.LegacyAlgorithms is not null;
        _accessCaption.Overridden = o.AgentAccess is not null;
        _commandsCaption.Overridden = o.AgentCommands is not null;
        _pathsCaption.Overridden = o.AgentProtectedPaths is not null;
    }

    /// <summary>Writes the edited values into <paramref name="target"/>; returns the first problem instead (target untouched).</summary>
    public OptionsError? Store(HostOptions target)
    {
        if (!TryNumber(_keepAlive, 0, HostOptions.MaxKeepAliveSeconds, out int? keepAlive))
            return new($"The keep-alive interval must be between 0 (off) and {HostOptions.MaxKeepAliveSeconds} seconds.", _connection, _keepAlive);
        if (!TryNumber(_timeout, 1, HostOptions.MaxConnectTimeoutSeconds, out int? timeout))
            return new($"The connect timeout must be between 1 and {HostOptions.MaxConnectTimeoutSeconds} seconds.", _connection, _timeout);
        string term = _term.Text.Trim();
        if (term.Any(char.IsWhiteSpace))
            return new("The terminal type must be a single word, e.g. xterm-256color.", _session, _term);
        List<EnvVar>? environment = _env.Collect(out string? envError, out TextField? envField);
        if (envError is not null)
            return new(envError, _session, envField);
        if (!TryNumber(_font.Field, (int)HostOptions.MinFontSize, (int)HostOptions.MaxFontSize, out int? fontSize))
            return new($"The font size must be between {HostOptions.MinFontSize} and {HostOptions.MaxFontSize}.", _appearance, _font.Field);

        var result = new HostOptions
        {
            JumpHostId = _jumpValues[Math.Max(0, _jump.SelectedIndex)],
            KeepAliveSeconds = keepAlive,
            ConnectTimeoutSeconds = timeout,
            AutoReconnect = TriValue(_reconnect),
            StartupCommand = _startupOverridden ? _startup.Text : null,
            Environment = environment,
            TerminalType = term.Length > 0 ? term : null,
            FontSize = fontSize,
            FontFamily = SelectedFamily,
            ColorScheme = _scheme.Selected,
            LegacyAlgorithms = TriValue(_legacy),
            AgentAccess = _access.SelectedIndex > 0 ? (AgentAccess)(_access.SelectedIndex - 1) : null,
            AgentCommands = _commands.Collect(),
            AgentProtectedPaths = _paths.Collect(),
        };
        if (result.Validate() is { } problem)
            return new(problem, problem.Contains("agent", StringComparison.OrdinalIgnoreCase) || problem.Contains("command", StringComparison.Ordinal)
                || problem.Contains("path", StringComparison.Ordinal) ? _agents : _connection, null);
        if (EffectiveOptions.NewJumpChainProblem(Vault, _candidate(result)) is { } route)
            return new(route, _connection, null);
        target.JumpHostId = result.JumpHostId;
        target.KeepAliveSeconds = result.KeepAliveSeconds;
        target.ConnectTimeoutSeconds = result.ConnectTimeoutSeconds;
        target.AutoReconnect = result.AutoReconnect;
        target.StartupCommand = result.StartupCommand;
        target.Environment = result.Environment;
        target.TerminalType = result.TerminalType;
        target.FontSize = result.FontSize;
        target.FontFamily = result.FontFamily;
        target.ColorScheme = result.ColorScheme;
        target.LegacyAlgorithms = result.LegacyAlgorithms;
        target.AgentAccess = result.AgentAccess;
        target.AgentCommands = result.AgentCommands;
        target.AgentProtectedPaths = result.AgentProtectedPaths;
        return null;
    }

    private static bool TryNumber(TextField field, int min, int max, out int? value)
    {
        value = null;
        string text = field.Text.Trim();
        if (text.Length == 0)
            return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            return false;
        value = n;
        return true;
    }

    // ---- inherited values ----

    /// <summary>Re-reads what this level inherits (e.g. after the host's group changed) and updates hints and placeholders.</summary>
    public void RefreshInherited()
    {
        _inherited = _resolveInherited();
        EffectiveHostOptions i = _inherited;

        BuildJumpOptions();
        UpdateJumpCaption();
        Set(_keepAliveCaption, Seconds(i.KeepAliveSeconds.Value), i.KeepAliveSeconds.Source);
        _keepAlive.Placeholder = i.KeepAliveSeconds.Value == 0 ? "0 (off)" : i.KeepAliveSeconds.Value.ToString(CultureInfo.InvariantCulture);
        Set(_timeoutCaption, Seconds(i.ConnectTimeoutSeconds.Value), i.ConnectTimeoutSeconds.Source);
        _timeout.Placeholder = i.ConnectTimeoutSeconds.Value.ToString(CultureInfo.InvariantCulture);
        Set(_reconnectCaption, OnOff(i.AutoReconnect.Value), i.AutoReconnect.Source);

        Set(_startupCaption, i.StartupCommand.Value.Length == 0 ? "none" : i.StartupCommand.Value, i.StartupCommand.Source);
        UpdateStartupPlaceholder();
        Set(_termCaption, i.TerminalType.Value, i.TerminalType.Source);
        _term.Placeholder = i.TerminalType.Value;
        int envCount = i.Environment.Value.Count;
        Set(_envCaption, envCount == 0 ? "none" : envCount == 1 ? "1 variable" : $"{envCount} variables", i.Environment.Source);
        _env.SetInherited(i.Environment.Value);

        Set(_schemeCaption, i.ColorScheme.Value, i.ColorScheme.Source);
        _scheme.SetInherited(ColorScheme.Find(i.ColorScheme.Value));
        string size = MathF.Round(i.FontSize.Value).ToString(CultureInfo.InvariantCulture) + " px";
        Set(_fontCaption, size, i.FontSize.Source);
        _font.SetInherited(i.FontSize.Value);
        Set(_familyCaption, i.FontFamily.Value, i.FontFamily.Source);
        BuildFamilyOptions(SelectedFamily);
        Set(_legacyCaption, OnOff(i.LegacyAlgorithms.Value), i.LegacyAlgorithms.Source);
        Set(_accessCaption, AccessName(i.AgentAccess.Value), i.AgentAccess.Source);
        int commands = i.AgentCommands.Value.Count, paths = i.AgentProtectedPaths.Value.Count;
        Set(_commandsCaption, commands == 0 ? "none" : commands == 1 ? "1 command" : $"{commands} commands", i.AgentCommands.Source);
        _commands.SetInherited(i.AgentCommands.Value);
        Set(_pathsCaption, paths == 0 ? "only the built-in ones" : paths == 1 ? "1 path" : $"{paths} paths", i.AgentProtectedPaths.Source);
        _paths.SetInherited(i.AgentProtectedPaths.Value);
        UpdateAccessHelp();
        UpdateRoute();
    }

    private static string AccessName(AgentAccess access) => access switch
    {
        AgentAccess.ReadOnly => "read only",
        AgentAccess.Ask => "ask",
        AgentAccess.Full => "full",
        _ => "off",
    };

    private void UpdateAccessHelp()
    {
        AgentAccess access = _access.SelectedIndex > 0 ? (AgentAccess)(_access.SelectedIndex - 1) : _inherited.AgentAccess.Value;
        string target = _level == OptionsLevel.Host ? "this host" : "these hosts";
        _accessHelp.Text = access switch
        {
            AgentAccess.ReadOnly => $"Agents can read files and run read-only commands on {target}, never change anything.",
            AgentAccess.Ask => $"Agents can read freely; you approve every change and command that is not read-only or allowed below.",
            AgentAccess.Full => $"Agents can run commands and change files on {target} without asking, except protected paths. For hosts you can rebuild.",
            _ => $"Agents (Claude Code and other MCP clients connected through TGK) can't see or use {target}.",
        };
        _accessHelp.Color = access == AgentAccess.Full ? Theme.Warning : Theme.TextMuted;
    }

    private void Set(OptionCaption caption, string value, OptionSource source) => caption.SetInherited(value, source switch
    {
        OptionSource.Group => $"from group {_inherited.GroupName}",
        OptionSource.Global => "from connection defaults",
        _ => null,
    });

    private static string Seconds(int seconds) => seconds == 0 ? "off" : $"{seconds} s";

    private static string OnOff(bool value) => value ? "on" : "off";

    private void UpdateStartupPlaceholder()
    {
        string inherited = _inherited.StartupCommand.Value;
        _startup.Placeholder = _startupOverridden ? "None (the inherited command is not sent)"
            : inherited.Length > 0 ? inherited
            : "None, e.g. tmux new -A -s main";
    }

    // ---- font family ----

    // "Default"/"Inherit · family", the bundled font, the monospace fonts installed here and, when this level's own
    // font is not installed on this device, that one (kept, so saving does not change what other devices use).
    private void BuildFamilyOptions(string? selected)
    {
        _familyValues.Clear();
        var names = new List<string>();
        string inherited = _inherited.FontFamily.Value;
        _familyValues.Add(null);
        names.Add(_level == OptionsLevel.Global ? $"Default · {FamilyName(inherited)}" : $"Inherit · {FamilyName(inherited)}");
        foreach (string family in TerminalFonts.Available)
        {
            _familyValues.Add(family);
            names.Add(FamilyName(family));
        }
        if (selected is not null && !_familyValues.Exists(f => f is not null && string.Equals(f, selected, StringComparison.OrdinalIgnoreCase)))
        {
            _familyValues.Add(selected);
            // Installed but not listed (its name does not look like a terminal font), or chosen on another device.
            names.Add(TerminalFonts.IsAvailable(selected) ? selected : $"{selected} (not installed on this device)");
        }
        _family.Options = names;
        _family.SelectedIndex = selected is null ? 0
            : Math.Max(0, _familyValues.FindIndex(f => f is not null && string.Equals(f, selected, StringComparison.OrdinalIgnoreCase)));
    }

    private static string FamilyName(string family) =>
        TerminalFonts.IsBuiltIn(family) ? $"{family} (built in)"
        : TerminalFonts.IsAvailable(family) ? family
        : $"{family} (not installed here)";

    // ---- jump host ----

    private void BuildJumpOptions()
    {
        Guid? selected = _jumpValues.Count > 0 && _jump.SelectedIndex >= 0 ? _jumpValues[_jump.SelectedIndex] : null;
        _jumpValues.Clear();
        var names = new List<string>();
        if (_level != OptionsLevel.Global)
        {
            _jumpValues.Add(null);
            names.Add($"Inherit · {JumpName(_inherited.JumpHostId.Value)}");
        }
        _jumpValues.Add(_level == OptionsLevel.Global ? null : HostOptions.NoJumpHost);
        names.Add("None (connect directly)");
        if (_deletedJump is not null)
        {
            // Kept (not shown as "Inherit"), so saving does not silently change the route: choosing is up to the user.
            _jumpValues.Add(_deletedJump);
            names.Add("Deleted host (choose another one)");
        }
        foreach (HostEntry host in Vault.Hosts.Where(h => h.Id != _hostId).OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            _jumpValues.Add(host.Id);
            names.Add($"{host.DisplayName}  ·  {host.Host}");
        }
        _jump.Options = names;
        _jump.SelectedIndex = Math.Max(0, _jumpValues.IndexOf(selected));
    }

    private string JumpName(Guid? id) => id is { } jump ? Vault.FindHost(jump)?.DisplayName ?? "a deleted host" : "none (direct)";

    private Guid? SelectedJump => _jumpValues[Math.Max(0, _jump.SelectedIndex)];

    private void UpdateJumpCaption()
    {
        Set(_jumpCaption, JumpName(_inherited.JumpHostId.Value), _inherited.JumpHostId.Source);
        _jumpCaption.Overridden = SelectedJump is not null;
    }

    /// <summary>
    /// "Route: bastion → gateway → this host", or why it can't be used: a loop or too many jump hosts that the chosen
    /// value would create for any host (saving is refused then, see <see cref="Store"/>), or a deleted jump host.
    /// </summary>
    private void UpdateRoute()
    {
        Guid? jumpId = SelectedJump is { } own ? (own == HostOptions.NoJumpHost ? null : own) : _inherited.JumpHostId.Value;
        VaultData candidate = _candidate(new HostOptions { JumpHostId = SelectedJump }); // only the jump host matters here
        string target = _level == OptionsLevel.Host ? "this host" : "each host";
        string? problem = EffectiveOptions.NewJumpChainProblem(Vault, candidate);
        string text = "";
        if (problem is null)
        {
            if (jumpId is null)
            {
                text = $"Route: connects directly to {target}.";
            }
            else if (candidate.FindHost(jumpId.Value) is not { } jump)
            {
                problem = "The jump host no longer exists. Choose another one.";
            }
            else
            {
                // A host's own route (also shows a loop through it saved earlier); otherwise the jump host's route and it.
                IReadOnlyList<HostEntry> hops = _hostId is { } id && candidate.FindHost(id) is { } self
                    ? EffectiveOptions.ResolveJumpChain(candidate, self, out problem)
                    : [.. EffectiveOptions.ResolveJumpChain(candidate, jump, out problem), jump];
                if (problem is null && hops.Count > EffectiveOptions.MaxJumpHosts)
                    problem = $"Too many jump hosts: a connection can go through at most {EffectiveOptions.MaxJumpHosts}.";
                text = $"Route: {string.Join(" → ", hops.Select(h => h.DisplayName))} → {target}";
            }
        }
        _route.Text = problem ?? text;
        _route.Color = problem is null ? Theme.TextMuted : Theme.Warning;
    }

    private void ShowTerminalTypes()
    {
        string current = _term.Text.Trim();
        List<MenuItem> items = TerminalTypes.Select(type => new MenuItem
        {
            Text = type,
            IsChecked = type == current,
            Action = () =>
            {
                _term.Text = type;
                _termCaption.Overridden = true;
            },
        }).ToList();
        var at = _term.Transform.Computed;
        _view.Menu.Show(items, at.X, at.Y + at.Height + 4, at.Width, at.Height);
    }

    // ---- layout (content-local; each returns the page height) ----

    private static float Place(OptionCaption caption, VisualElement control, float x, float y, float w, float h = Theme.FieldHeight)
    {
        caption.Transform.SetLocalFrame(x, y, w, OptionCaption.Height);
        control.Transform.SetLocalFrame(x, y + OptionCaption.Height + CaptionGap, w, h);
        return y + OptionCaption.Height + CaptionGap + h;
    }

    private static float Help(Label label, float x, float y, float w)
    {
        float h = Math.Max(HelpH, label.MeasureHeight(w) - 3);
        label.Transform.SetLocalFrame(x, y + HelpGap, w, h);
        return y + HelpGap + h;
    }

    private float ArrangeConnection(float w)
    {
        float col = MathF.Floor((w - Gap) / 2f);
        float y = Place(_jumpCaption, _jump, 0, 0, w);
        y = Help(_route, 0, y, w) + RowGap;
        Place(_keepAliveCaption, _keepAlive, 0, y, col);
        y = Place(_timeoutCaption, _timeout, col + Gap, y, w - col - Gap) + RowGap;
        float right = w - col - Gap;
        float left = Help(_reconnectHelp, 0, Place(_reconnectCaption, _reconnect, 0, y, col), col);
        float legacy = Help(_legacyHelp, col + Gap, Place(_legacyCaption, _legacy, col + Gap, y, right), right);
        return Math.Max(left, legacy);
    }

    private float ArrangeSession(float w)
    {
        float col = MathF.Floor((w - Gap) / 2f);
        float y = Place(_startupCaption, _startup, 0, 0, w);
        y = Help(_startupHelp, 0, y, w) + RowGap;
        y = Place(_termCaption, _term, 0, y, col) + RowGap;
        float envH = _env.MeasureHeight();
        y = Place(_envCaption, _env, 0, y, w, envH);
        _env.Arrange(w);
        return y;
    }

    /// <summary>Places the color scheme, font and font size from <paramref name="top"/> down; returns the bottom.</summary>
    public float ArrangeAppearance(float w, float top)
    {
        float col = MathF.Floor((w - Gap) / 2f);
        float y = Place(_schemeCaption, _scheme, 0, top, w, SchemePicker.MeasureHeight()) + RowGap;
        Place(_familyCaption, _family, 0, y, col);
        return Place(_fontCaption, _font, col + Gap, y, w - col - Gap);
    }

    private float ArrangeAgents(float w)
    {
        float y = _agentsHeader?.Invoke(w) ?? 0;
        y = Place(_accessCaption, _access, 0, y, w);
        y = Help(_accessHelp, 0, y, w) + RowGap;
        float commandsH = _commands.MeasureHeight(w);
        y = Place(_commandsCaption, _commands, 0, y, w, commandsH) + RowGap;
        _commands.Arrange(w);
        float pathsH = _paths.MeasureHeight(w);
        y = Place(_pathsCaption, _paths, 0, y, w, pathsH);
        _paths.Arrange(w);
        return y;
    }
}
