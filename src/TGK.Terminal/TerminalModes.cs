namespace TGK.Terminal;

/// <summary>Which mouse events the application asked to receive (DECSET 9/1000/1002/1003).</summary>
public enum MouseTrackingMode
{
    None,

    /// <summary>DECSET 9: button presses only, no modifiers.</summary>
    X10,

    /// <summary>DECSET 1000: presses and releases.</summary>
    Normal,

    /// <summary>DECSET 1002: presses, releases and motion while a button is held.</summary>
    ButtonEvent,

    /// <summary>DECSET 1003: presses, releases and all motion.</summary>
    AnyEvent,
}

/// <summary>Cursor shape requested via DECSCUSR.</summary>
public enum CursorShape
{
    Block,
    Underline,
    Bar,
}

/// <summary>
/// Snapshot of the terminal modes that affect input encoding and rendering.
/// <see cref="TerminalEmulator.Modes"/> returns a copy; the struct can also be built by hand for
/// <see cref="TerminalInput"/>.
/// </summary>
public struct TerminalModes
{
    /// <summary>The modes of a freshly reset terminal.</summary>
    public static TerminalModes Initial => new() { AutoWrap = true };

    /// <summary>DECCKM (DECSET 1): cursor keys send SS3 sequences.</summary>
    public bool ApplicationCursorKeys { get; set; }

    /// <summary>DECKPAM / DECKPNM.</summary>
    public bool ApplicationKeypad { get; set; }

    /// <summary>DECSET 2004.</summary>
    public bool BracketedPaste { get; set; }

    public MouseTrackingMode MouseTracking { get; set; }

    /// <summary>DECSET 1006: SGR mouse encoding.</summary>
    public bool MouseSgr { get; set; }

    /// <summary>DECSET 47/1047/1049.</summary>
    public bool AlternateScreen { get; set; }

    /// <summary>DECAWM (DECSET 7).</summary>
    public bool AutoWrap { get; set; }

    /// <summary>DECOM (DECSET 6): cursor addressing relative to the scroll region.</summary>
    public bool Origin { get; set; }

    /// <summary>IRM (SM 4).</summary>
    public bool Insert { get; set; }

    /// <summary>LNM (SM 20): LF also returns the carriage; Enter sends CR LF.</summary>
    public bool LineFeedNewLine { get; set; }

    /// <summary>DECSET 1004.</summary>
    public bool FocusEvents { get; set; }
}
