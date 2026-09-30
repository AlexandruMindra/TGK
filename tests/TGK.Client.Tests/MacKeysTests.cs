using Silk.NET.Input;
using TGK.Client.Input;
using Xunit;

namespace TGK.Client.Tests;

public class MacKeysTests
{
    [Fact]
    public void Command_is_tried_as_ctrl_shift_then_ctrl_and_reaches_terminals_as_ctrl_shift()
    {
        var copy = new KeyStroke(Key.C, KeyModifiers.Super);

        var terminal = MacKeys.Translate(copy, terminalFocused: true)!.Value;
        var field = MacKeys.Translate(copy, terminalFocused: false)!.Value;

        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl | KeyModifiers.Shift), terminal.ShortcutFirst);
        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl), terminal.ShortcutSecond);
        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl | KeyModifiers.Shift), terminal.ForFocused); // copy, not ^C
        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl), field.ForFocused);
    }

    [Fact]
    public void Shift_and_repeat_are_kept_and_other_strokes_are_left_alone()
    {
        var stroke = new KeyStroke(Key.Tab, KeyModifiers.Super | KeyModifiers.Shift, IsRepeat: true);
        Assert.Equal(new KeyStroke(Key.Tab, KeyModifiers.Ctrl | KeyModifiers.Shift, true), MacKeys.Translate(stroke, false)!.Value.ShortcutSecond);

        Assert.Null(MacKeys.Translate(new KeyStroke(Key.C, KeyModifiers.Ctrl), false));
        Assert.Null(MacKeys.Translate(new KeyStroke(Key.C, KeyModifiers.Ctrl | KeyModifiers.Super), false));
        Assert.Null(MacKeys.Translate(new KeyStroke(Key.A, KeyModifiers.None), true));
    }
}
