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

        var terminal = MacKeys.Translate(copy, terminalTarget: true)!.Value;
        var field = MacKeys.Translate(copy, terminalTarget: false)!.Value;

        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl | KeyModifiers.Shift), terminal.ShortcutFirst);
        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl), terminal.ShortcutSecond);
        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl | KeyModifiers.Shift), terminal.ForTarget); // copy, not ^C
        Assert.Equal(new KeyStroke(Key.C, KeyModifiers.Ctrl), field.ForTarget);
    }

    [Theory]
    [InlineData(Key.D)] // ^D would end the remote shell
    [InlineData(Key.Z)]
    [InlineData(Key.K)]
    public void Other_command_keys_never_reach_a_terminal(Key key)
    {
        Assert.Null(MacKeys.Translate(new KeyStroke(key, KeyModifiers.Super), terminalTarget: true)!.Value.ForTarget);
        Assert.Null(MacKeys.Translate(new KeyStroke(Key.C, KeyModifiers.Super | KeyModifiers.Shift), terminalTarget: true)!.Value.ForTarget);
        Assert.Equal(new KeyStroke(key, KeyModifiers.Ctrl), MacKeys.Translate(new KeyStroke(key, KeyModifiers.Super), terminalTarget: false)!.Value.ForTarget);
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
