namespace TGK.Client.Input;

/// <summary>Marks an element that sends Ctrl combinations to a program (a terminal): on macOS ⌘ reaches it as Ctrl+Shift.</summary>
public interface ISendsControlKeys
{
}

/// <summary>
/// macOS: the ⌘ key does what Ctrl does elsewhere. A ⌘ stroke is offered to the shortcuts as Ctrl+Shift (the variant
/// that always works, also over a terminal: ⌘T, ⌘W, ⌘E) and then as Ctrl (⌘, and ⌘1..9). If none takes it, it goes to
/// a text field as Ctrl (⌘C, ⌘V, ⌘A, ⌘X); a terminal only gets ⌘C / ⌘V, as its Ctrl+Shift copy and paste, and any
/// other ⌘ stroke is dropped, so ⌘ never turns into a control character for the remote program (⌘D is not ^D).
/// </summary>
public static class MacKeys
{
    /// <summary>
    /// The strokes to try for a ⌘ stroke, in order: shortcuts first, then the one for the element that gets the keys
    /// (null: drop it). Null for strokes without ⌘ (or with Ctrl as well).
    /// </summary>
    public static (KeyStroke ShortcutFirst, KeyStroke ShortcutSecond, KeyStroke? ForTarget)? Translate(KeyStroke stroke, bool terminalTarget)
    {
        if (!stroke.Super || stroke.Ctrl)
            return null;
        KeyStroke ctrl = stroke with { Modifiers = (stroke.Modifiers & ~KeyModifiers.Super) | KeyModifiers.Ctrl };
        KeyStroke ctrlShift = ctrl with { Modifiers = ctrl.Modifiers | KeyModifiers.Shift };
        KeyStroke? target = !terminalTarget ? ctrl
            : stroke.Modifiers == KeyModifiers.Super && stroke.Key is Silk.NET.Input.Key.C or Silk.NET.Input.Key.V ? ctrlShift
            : null;
        return (ctrlShift, ctrl, target);
    }
}
