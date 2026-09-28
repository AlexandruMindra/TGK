using System.Text;
using Xunit;

namespace TGK.Terminal.Tests;

public class InputTests
{
    private static readonly TerminalModes Normal = TerminalModes.Initial;
    private static readonly TerminalModes AppCursor = new() { ApplicationCursorKeys = true };

    private static string Key(TermKey key, KeyMods mods = KeyMods.None, TerminalModes? modes = null, int rune = 0) =>
        Encoding.UTF8.GetString(TerminalInput.EncodeKey(key, mods, modes ?? Normal, rune));

    [Theory]
    [InlineData(TermKey.Up, "\u001b[A", "\u001bOA")]
    [InlineData(TermKey.Down, "\u001b[B", "\u001bOB")]
    [InlineData(TermKey.Right, "\u001b[C", "\u001bOC")]
    [InlineData(TermKey.Left, "\u001b[D", "\u001bOD")]
    [InlineData(TermKey.Home, "\u001b[H", "\u001bOH")]
    [InlineData(TermKey.End, "\u001b[F", "\u001bOF")]
    public void CursorKeys_NormalAndApplicationMode(TermKey key, string normal, string application)
    {
        Assert.Equal(normal, Key(key));
        Assert.Equal(application, Key(key, modes: AppCursor));
    }

    [Theory]
    [InlineData(TermKey.Up, KeyMods.Shift, "\u001b[1;2A")]
    [InlineData(TermKey.Up, KeyMods.Alt, "\u001b[1;3A")]
    [InlineData(TermKey.Right, KeyMods.Ctrl, "\u001b[1;5C")]
    [InlineData(TermKey.Left, KeyMods.Ctrl | KeyMods.Shift, "\u001b[1;6D")]
    [InlineData(TermKey.Down, KeyMods.Ctrl | KeyMods.Alt | KeyMods.Shift, "\u001b[1;8B")]
    [InlineData(TermKey.Home, KeyMods.Ctrl, "\u001b[1;5H")]
    [InlineData(TermKey.Delete, KeyMods.Ctrl, "\u001b[3;5~")]
    [InlineData(TermKey.PageUp, KeyMods.Shift, "\u001b[5;2~")]
    [InlineData(TermKey.F1, KeyMods.Ctrl, "\u001b[1;5P")]
    [InlineData(TermKey.F5, KeyMods.Shift, "\u001b[15;2~")]
    [InlineData(TermKey.F12, KeyMods.Alt, "\u001b[24;3~")]
    public void ModifiedKeys_UseXtermModifierParameter(TermKey key, KeyMods mods, string expected)
    {
        Assert.Equal(expected, Key(key, mods));
        Assert.Equal(expected, Key(key, mods, AppCursor)); // modifiers override application mode
    }

    [Theory]
    [InlineData(TermKey.Insert, "\u001b[2~")]
    [InlineData(TermKey.Delete, "\u001b[3~")]
    [InlineData(TermKey.PageUp, "\u001b[5~")]
    [InlineData(TermKey.PageDown, "\u001b[6~")]
    [InlineData(TermKey.F1, "\u001bOP")]
    [InlineData(TermKey.F2, "\u001bOQ")]
    [InlineData(TermKey.F3, "\u001bOR")]
    [InlineData(TermKey.F4, "\u001bOS")]
    [InlineData(TermKey.F5, "\u001b[15~")]
    [InlineData(TermKey.F6, "\u001b[17~")]
    [InlineData(TermKey.F7, "\u001b[18~")]
    [InlineData(TermKey.F8, "\u001b[19~")]
    [InlineData(TermKey.F9, "\u001b[20~")]
    [InlineData(TermKey.F10, "\u001b[21~")]
    [InlineData(TermKey.F11, "\u001b[23~")]
    [InlineData(TermKey.F12, "\u001b[24~")]
    [InlineData(TermKey.Escape, "\u001b")]
    [InlineData(TermKey.Tab, "\t")]
    [InlineData(TermKey.Enter, "\r")]
    [InlineData(TermKey.Backspace, "\u007f")]
    public void UnmodifiedKeys(TermKey key, string expected)
    {
        Assert.Equal(expected, Key(key));
    }

    [Fact]
    public void SpecialKeyModifiers()
    {
        Assert.Equal("\u0008", Key(TermKey.Backspace, KeyMods.Ctrl));
        Assert.Equal("\u001b\u007f", Key(TermKey.Backspace, KeyMods.Alt));
        Assert.Equal("\u001b[Z", Key(TermKey.Tab, KeyMods.Shift));
        Assert.Equal("\u001b\t", Key(TermKey.Tab, KeyMods.Alt));
        Assert.Equal("\u001b\r", Key(TermKey.Enter, KeyMods.Alt));
        Assert.Equal("\u001b\u001b", Key(TermKey.Escape, KeyMods.Alt));
        Assert.Equal("\r\n", Key(TermKey.Enter, modes: new TerminalModes { LineFeedNewLine = true }));
    }

    [Theory]
    [InlineData('a', KeyMods.None, "a")]
    [InlineData('A', KeyMods.Shift, "A")]
    [InlineData('c', KeyMods.Ctrl, "\u0003")]
    [InlineData('C', KeyMods.Ctrl | KeyMods.Shift, "\u0003")]
    [InlineData('@', KeyMods.Ctrl, "\u0000")]
    [InlineData(' ', KeyMods.Ctrl, "\u0000")]
    [InlineData('[', KeyMods.Ctrl, "\u001b")]
    [InlineData('\\', KeyMods.Ctrl, "\u001c")]
    [InlineData(']', KeyMods.Ctrl, "\u001d")]
    [InlineData('^', KeyMods.Ctrl, "\u001e")]
    [InlineData('_', KeyMods.Ctrl, "\u001f")]
    [InlineData('/', KeyMods.Ctrl, "\u001f")]
    [InlineData('?', KeyMods.Ctrl, "\u007f")]
    [InlineData('{', KeyMods.Ctrl | KeyMods.Shift, "\u001b")] // shifted keys map like xterm's Ctrl+{ |}
    [InlineData('|', KeyMods.Ctrl | KeyMods.Shift, "\u001c")]
    [InlineData('}', KeyMods.Ctrl | KeyMods.Shift, "\u001d")]
    [InlineData('>', KeyMods.Alt | KeyMods.Shift, "\u001b>")] // readline's end-of-history (M->)
    [InlineData('x', KeyMods.Alt, "\u001bx")]
    [InlineData('x', KeyMods.Ctrl | KeyMods.Alt, "\u001b\u0018")]
    [InlineData('.', KeyMods.Ctrl, ".")] // no control mapping
    [InlineData('é', KeyMods.None, "é")]
    [InlineData('ü', KeyMods.Alt, "\u001bü")]
    public void Characters(char c, KeyMods mods, string expected)
    {
        Assert.Equal(expected, Key(TermKey.Char, mods, rune: c));
        Assert.Equal(expected, Encoding.UTF8.GetString(TerminalInput.EncodeChar(c, mods)));
    }

    [Fact]
    public void Characters_OutsideBmp()
    {
        Assert.Equal("😀", Key(TermKey.Char, rune: 0x1F600));
        Assert.Empty(TerminalInput.EncodeChar(0xD800, KeyMods.None)); // lone surrogate
    }

    [Fact]
    public void EncodeText_IsUtf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("héllo 中"), TerminalInput.EncodeText("héllo 中"));
    }

    [Fact]
    public void EncodePaste_NormalizesNewlines()
    {
        var bytes = TerminalInput.EncodePaste("a\r\nb\nc\rd", Normal);
        Assert.Equal("a\rb\rc\rd", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void EncodePaste_BracketedWrapsAndStripsMarkers()
    {
        var modes = new TerminalModes { BracketedPaste = true };
        var bytes = TerminalInput.EncodePaste("ls\u001b[201~; rm -rf x\u001b[20\u001b[201~1~\n", modes);
        Assert.Equal("\u001b[200~ls; rm -rf x\r\u001b[201~", Encoding.UTF8.GetString(bytes));
    }

    private static string? Mouse(MouseButton button, int col, int row, MouseEventKind kind, KeyMods mods, TerminalModes modes)
    {
        var bytes = TerminalInput.EncodeMouse(button, col, row, kind, mods, modes);
        return bytes is null ? null : Encoding.Latin1.GetString(bytes);
    }

    [Fact]
    public void Mouse_Sgr()
    {
        var m = new TerminalModes { MouseTracking = MouseTrackingMode.Normal, MouseSgr = true };
        Assert.Equal("\u001b[<0;1;1M", Mouse(MouseButton.Left, 0, 0, MouseEventKind.Press, KeyMods.None, m));
        Assert.Equal("\u001b[<0;5;3m", Mouse(MouseButton.Left, 4, 2, MouseEventKind.Release, KeyMods.None, m));
        Assert.Equal("\u001b[<2;300;400M", Mouse(MouseButton.Right, 299, 399, MouseEventKind.Press, KeyMods.None, m));
        Assert.Equal("\u001b[<17;1;1M", Mouse(MouseButton.Middle, 0, 0, MouseEventKind.Press, KeyMods.Ctrl, m));
        Assert.Equal("\u001b[<28;1;1M", Mouse(MouseButton.Left, 0, 0, MouseEventKind.Press, KeyMods.Ctrl | KeyMods.Alt | KeyMods.Shift, m));
        Assert.Equal("\u001b[<64;2;2M", Mouse(MouseButton.WheelUp, 1, 1, MouseEventKind.Press, KeyMods.None, m));
        Assert.Equal("\u001b[<65;2;2M", Mouse(MouseButton.WheelDown, 1, 1, MouseEventKind.Press, KeyMods.None, m));
        Assert.Null(Mouse(MouseButton.WheelDown, 1, 1, MouseEventKind.Release, KeyMods.None, m));
        Assert.Null(Mouse(MouseButton.Left, 1, 1, MouseEventKind.Motion, KeyMods.None, m)); // no motion in mode 1000
    }

    [Fact]
    public void Mouse_MotionDependsOnTrackingMode()
    {
        var button = new TerminalModes { MouseTracking = MouseTrackingMode.ButtonEvent, MouseSgr = true };
        Assert.Equal("\u001b[<32;3;4M", Mouse(MouseButton.Left, 2, 3, MouseEventKind.Motion, KeyMods.None, button));
        Assert.Null(Mouse(MouseButton.None, 2, 3, MouseEventKind.Motion, KeyMods.None, button));

        var any = new TerminalModes { MouseTracking = MouseTrackingMode.AnyEvent, MouseSgr = true };
        Assert.Equal("\u001b[<35;3;4M", Mouse(MouseButton.None, 2, 3, MouseEventKind.Motion, KeyMods.None, any));
    }

    [Fact]
    public void Mouse_LegacyEncoding()
    {
        var m = new TerminalModes { MouseTracking = MouseTrackingMode.Normal };
        Assert.Equal("\u001b[M !!", Mouse(MouseButton.Left, 0, 0, MouseEventKind.Press, KeyMods.None, m));
        Assert.Equal("\u001b[M#*+", Mouse(MouseButton.Left, 9, 10, MouseEventKind.Release, KeyMods.None, m));
        Assert.Equal("\u001b[M`!!", Mouse(MouseButton.WheelUp, 0, 0, MouseEventKind.Press, KeyMods.None, m));
        Assert.Equal("\u001b[M0!!", Mouse(MouseButton.Left, 0, 0, MouseEventKind.Press, KeyMods.Ctrl, m));
        Assert.Equal("\u001b[M ÿÿ", Mouse(MouseButton.Left, 222, 222, MouseEventKind.Press, KeyMods.None, m));
        Assert.Null(Mouse(MouseButton.Left, 223, 0, MouseEventKind.Press, KeyMods.None, m)); // not encodable
    }

    [Fact]
    public void Mouse_X10ReportsPressesOnlyWithoutModifiers()
    {
        var m = new TerminalModes { MouseTracking = MouseTrackingMode.X10 };
        Assert.Equal("\u001b[M\"!!", Mouse(MouseButton.Right, 0, 0, MouseEventKind.Press, KeyMods.Ctrl, m));
        Assert.Null(Mouse(MouseButton.Right, 0, 0, MouseEventKind.Release, KeyMods.None, m));
        Assert.Null(Mouse(MouseButton.Right, 0, 0, MouseEventKind.Motion, KeyMods.None, m));
    }

    [Fact]
    public void Mouse_TrackingDisabled_ReturnsNull()
    {
        Assert.Null(TerminalInput.EncodeMouse(MouseButton.Left, 0, 0, MouseEventKind.Press, KeyMods.None, Normal));
    }

    [Fact]
    public void Focus()
    {
        Assert.Equal("\u001b[I", Encoding.ASCII.GetString(TerminalInput.EncodeFocus(true)));
        Assert.Equal("\u001b[O", Encoding.ASCII.GetString(TerminalInput.EncodeFocus(false)));
        Assert.Null(TerminalInput.EncodeFocus(true, Normal));
        Assert.Equal("\u001b[I", Encoding.ASCII.GetString(TerminalInput.EncodeFocus(true, new TerminalModes { FocusEvents = true })!));
    }

    [Fact]
    public void ModesFromEmulator_DriveEncoding()
    {
        var t = new TerminalEmulator(10, 2);
        t.Feed("\u001b[?1h\u001b[?1002;1006h\u001b[?2004h");
        Assert.Equal("\u001bOA", Encoding.ASCII.GetString(TerminalInput.EncodeKey(TermKey.Up, KeyMods.None, t.Modes)));
        Assert.NotNull(TerminalInput.EncodeMouse(MouseButton.Left, 0, 0, MouseEventKind.Motion, KeyMods.None, t.Modes));
        Assert.StartsWith("\u001b[200~", Encoding.UTF8.GetString(TerminalInput.EncodePaste("x", t.Modes)));
    }
}
