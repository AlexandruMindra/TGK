namespace TGK.Client.Input;

/// <summary>Marks an element that sends Ctrl combinations to a program (a terminal): on macOS ⌘ reaches it as Ctrl+Shift.</summary>
public interface ISendsControlKeys
{
}

/// <summary>
/// macOS: the ⌘ key does what Ctrl does elsewhere. A ⌘ stroke is offered to the shortcuts as Ctrl+Shift (the variant
/// that always works, also over a terminal: ⌘T, ⌘W, ⌘E) and then as Ctrl (⌘, and ⌘1..9); if none takes it, it goes to
/// the focused element as Ctrl (text fields: ⌘C, ⌘V, ⌘A) or, for a terminal, as Ctrl+Shift (copy and paste), so ⌘ never
/// turns into a control character for the remote program.
/// </summary>
public static class MacKeys
{
    /// <summary>The strokes to try for a ⌘ stroke, in order: shortcuts first, then the one for the focused element; null for other strokes.</summary>
    public static (KeyStroke ShortcutFirst, KeyStroke ShortcutSecond, KeyStroke ForFocused)? Translate(KeyStroke stroke, bool terminalFocused)
    {
        if (!stroke.Super || stroke.Ctrl)
            return null;
        KeyStroke ctrl = stroke with { Modifiers = (stroke.Modifiers & ~KeyModifiers.Super) | KeyModifiers.Ctrl };
        KeyStroke ctrlShift = ctrl with { Modifiers = ctrl.Modifiers | KeyModifiers.Shift };
        return (ctrlShift, ctrl, terminalFocused ? ctrlShift : ctrl);
    }
}
