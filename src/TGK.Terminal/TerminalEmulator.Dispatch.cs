using System;
using System.Globalization;
using System.Text;

namespace TGK.Terminal;

// Parser callbacks: printing, C0/C1 controls, ESC, CSI and OSC sequences, modes and device reports.
public sealed partial class TerminalEmulator
{
    private const string PrimaryDeviceAttributes = "\x1b[?62;22c"; // VT220 with ANSI color
    private const string SecondaryDeviceAttributes = "\x1b[>1;10;0c";

    void IVtHandler.Print(int rune)
    {
        if (rune < 0x80)
        {
            var charset = _shiftOut ? _g1 : _g0;
            if (charset != Charset.Ascii)
                rune = Charsets.Map(charset, rune);
        }

        PrintRune(rune);
    }

    void IVtHandler.PrintAscii(ReadOnlySpan<byte> text)
    {
        if (_modes.Insert || (_shiftOut ? _g1 : _g0) != Charset.Ascii)
        {
            foreach (byte b in text)
                ((IVtHandler)this).Print(b);
            return;
        }

        // Same effect as PrintRune per character, a row segment at a time.
        var style = _style;
        while (!text.IsEmpty)
        {
            if (_pendingWrap)
                WrapLine();

            var cells = _buf.Lines[_y].Cells;
            int n = Math.Min(text.Length, _cols - _x);
            SplitWide(cells, _x);
            SplitWide(cells, _x + n);
            for (int i = 0; i < n; i++)
                cells[_x + i] = new Cell(text[i], 1, style);

            _dirty[_y] = true;
            _lastPrinted = text[n - 1];
            _x += n;
            text = text[n..];
            if (_x < _cols)
                break;

            _x = _cols - 1;
            if (_modes.AutoWrap)
                _pendingWrap = true;
            else if (!text.IsEmpty)
            {
                // Without autowrap every further character overwrites the last column; only the final one stays.
                cells[_x] = new Cell(text[^1], 1, style);
                _lastPrinted = text[^1];
                break;
            }
        }
    }

    private void PrintRune(int rune)
    {
        int width = UnicodeWidth.GetWidth(rune);
        if (width == 0)
        {
            Combine(rune);
            return;
        }

        if (_pendingWrap)
            WrapLine();

        if (width == 2 && _x == _cols - 1)
        {
            if (_cols < 2)
                return; // a wide character can never fit
            if (_modes.AutoWrap)
            {
                // It does not fit: leave the last cell empty and continue on the next line.
                EraseCells(_y, _x, _cols);
                WrapLine();
            }
            else
                _x = _cols - 2;
        }

        if (_modes.Insert)
            InsertCells(width);

        var cells = _buf.Lines[_y].Cells;
        SplitWide(cells, _x);
        SplitWide(cells, _x + width);
        cells[_x] = new Cell(rune, (byte)width, _style);
        if (width == 2)
            cells[_x + 1] = new Cell(0, 0, _style);

        _dirty[_y] = true;
        _lastPrinted = rune;
        _x += width;
        if (_x >= _cols)
        {
            _x = _cols - 1;
            _pendingWrap = _modes.AutoWrap;
        }
    }

    private void WrapLine()
    {
        _buf.Lines[_y].Wrapped = true;
        _x = 0;
        Index();
    }

    // Composes a zero-width mark into the previous character when a precomposed form exists; drops it otherwise.
    private void Combine(int mark)
    {
        int x = _pendingWrap ? _x : _x - 1;
        if (x < 0)
            return;

        var cells = _buf.Lines[_y].Cells;
        if (cells[x].IsContinuation && x > 0)
            x--;
        ref var cell = ref cells[x];
        if (cell.Rune == 0)
            return;

        string composed = string.Concat(char.ConvertFromUtf32(cell.Rune), char.ConvertFromUtf32(mark))
            .Normalize(NormalizationForm.FormC);
        if (composed.Length == 1 || (composed.Length == 2 && char.IsSurrogatePair(composed[0], composed[1])))
        {
            cell.Rune = char.ConvertToUtf32(composed, 0);
            _dirty[_y] = true;
        }
    }

    void IVtHandler.Execute(int control)
    {
        switch (control)
        {
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08:
                CursorBackward(1);
                break;
            case 0x09:
                TabForward(1);
                break;
            case 0x0A: // LF, VT and FF
            case 0x0B:
            case 0x0C:
                Index();
                if (_modes.LineFeedNewLine)
                    _x = 0;
                break;
            case 0x0D:
                _x = 0;
                _pendingWrap = false;
                break;
            case 0x0E: // SO
                _shiftOut = true;
                break;
            case 0x0F: // SI
                _shiftOut = false;
                break;
            case 0x84: // IND
                Index();
                break;
            case 0x85: // NEL
                NextLine();
                break;
            case 0x88: // HTS
                _tabStops[_x] = true;
                break;
            case 0x8D: // RI
                ReverseIndex();
                break;
        }
    }

    void IVtHandler.EscDispatch(int intermediates, char final)
    {
        switch (intermediates)
        {
            case 0:
                switch (final)
                {
                    case '7':
                        SaveCursor();
                        break;
                    case '8':
                        RestoreCursor();
                        break;
                    case 'D':
                        Index();
                        break;
                    case 'E':
                        NextLine();
                        break;
                    case 'H':
                        _tabStops[_x] = true;
                        break;
                    case 'M':
                        ReverseIndex();
                        break;
                    case 'c':
                        FullReset();
                        break;
                    case '=':
                        _modes.ApplicationKeypad = true;
                        break;
                    case '>':
                        _modes.ApplicationKeypad = false;
                        break;
                }

                break;
            case '(':
                _g0 = Charsets.FromDesignator(final);
                break;
            case ')':
                _g1 = Charsets.FromDesignator(final);
                break;
            case '#' when final == '8':
                ScreenAlignmentTest();
                break;
        }
    }

    void IVtHandler.CsiDispatch(VtParams p, char final)
    {
        if (p.Intermediates != 0)
        {
            if (p.PrivateMarker != '\0')
                return;
            if (p.Intermediates == ' ' && final == 'q')
                SetCursorStyle(p.Get(0, 0));
            else if (p.Intermediates == '!' && final == 'p')
                SoftReset();
            return;
        }

        switch (p.PrivateMarker)
        {
            case '\0':
                DispatchStandardCsi(p, final);
                break;
            case '?':
                if (final is 'h' or 'l')
                {
                    for (int i = 0; i < p.Count; i++)
                        SetPrivateMode(p[i], final == 'h');
                }
                else if (final == 'n' && p.Get(0, 0) == 6)
                    Reply($"\x1b[?{CursorReportRow()};{_x + 1};1R"); // DECXCPR
                break;
            case '>':
                if (final == 'c' && p.Get(0, 0) == 0)
                    Reply(SecondaryDeviceAttributes);
                else if (final == 'q' && p.Get(0, 0) == 0)
                    Reply("\x1bP>|TGK\x1b\\"); // XTVERSION
                break;
        }
    }

    private void DispatchStandardCsi(VtParams p, char final)
    {
        int n = p.Get(0, 1);
        switch (final)
        {
            case '@':
                InsertCells(n);
                break;
            case 'A':
                CursorUp(n);
                break;
            case 'B':
            case 'e': // VPR
                CursorDown(n);
                break;
            case 'C':
            case 'a': // HPR
                CursorForward(n);
                break;
            case 'D':
                CursorBackward(n);
                break;
            case 'E':
                CursorDown(n);
                _x = 0;
                break;
            case 'F':
                CursorUp(n);
                _x = 0;
                break;
            case 'G':
            case '`': // HPA
                SetCol(n - 1);
                break;
            case 'H':
            case 'f':
                CursorPosition(p.Get(0, 1), p.Get(1, 1));
                break;
            case 'I':
                TabForward(n);
                break;
            case 'J':
                EraseInDisplay(p.Get(0, 0));
                break;
            case 'K':
                EraseInLine(p.Get(0, 0));
                break;
            case 'L':
                InsertLines(n);
                break;
            case 'M':
                DeleteLines(n);
                break;
            case 'P':
                DeleteCells(n);
                break;
            case 'S':
                ScrollUp(_top, _bottom, n, allowScrollback: true);
                break;
            case 'T':
                if (p.Count <= 1) // with more parameters this is xterm's mouse highlight tracking
                    ScrollDown(_top, _bottom, n);
                break;
            case 'X':
                EraseChars(n);
                break;
            case 'Z':
                TabBackward(n);
                break;
            case 'b':
                Repeat(n);
                break;
            case 'c':
                if (p.Get(0, 0) == 0)
                    Reply(PrimaryDeviceAttributes);
                break;
            case 'd':
                SetRow(n - 1);
                break;
            case 'g':
                ClearTabStops(p.Get(0, 0));
                break;
            case 'h':
            case 'l':
                for (int i = 0; i < p.Count; i++)
                    SetAnsiMode(p[i], final == 'h');
                break;
            case 'm':
                SelectGraphicRendition(p);
                break;
            case 'n':
                DeviceStatusReport(p.Get(0, 0));
                break;
            case 'r':
                SetScrollRegion(p.Get(0, 1), p.Get(1, _rows));
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 't':
                if (p.Get(0, 0) == 18)
                    Reply($"\x1b[8;{_rows};{_cols}t");
                break;
        }
    }

    void IVtHandler.OscDispatch(ReadOnlySpan<char> data)
    {
        int semicolon = data.IndexOf(';');
        if (semicolon <= 0 ||
            !int.TryParse(data[..semicolon], NumberStyles.None, CultureInfo.InvariantCulture, out int command))
            return;

        // OSC 1 (icon name) and everything else (colors, clipboard, hyperlinks, ...) is ignored.
        if (command is 0 or 2)
            SetTitle(data[(semicolon + 1)..].ToString());
    }

    private void SetAnsiMode(int mode, bool on)
    {
        if (mode == 4)
            _modes.Insert = on;
        else if (mode == 20)
            _modes.LineFeedNewLine = on;
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                _modes.ApplicationCursorKeys = on;
                break;
            case 6:
                _modes.Origin = on;
                CursorPosition(1, 1);
                break;
            case 7:
                _modes.AutoWrap = on;
                if (!on)
                    _pendingWrap = false;
                break;
            case 9:
                _modes.MouseTracking = on ? MouseTrackingMode.X10 : MouseTrackingMode.None;
                break;
            case 12:
                CursorBlink = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 47:
                SwitchScreen(on);
                break;
            case 1047:
                if (!on && _buf == _alt)
                    ClearBuffer(_alt);
                SwitchScreen(on);
                break;
            case 1048:
                if (on)
                    SaveCursor();
                else
                    RestoreCursor();
                break;
            case 1049:
                if (on)
                {
                    if (_buf == _main)
                    {
                        SaveCursor();
                        SwitchScreen(true);
                    }

                    ClearBuffer(_alt);
                }
                else if (_buf == _alt)
                {
                    SwitchScreen(false);
                    RestoreCursor();
                }

                break;
            case 1000:
                _modes.MouseTracking = on ? MouseTrackingMode.Normal : MouseTrackingMode.None;
                break;
            case 1002:
                _modes.MouseTracking = on ? MouseTrackingMode.ButtonEvent : MouseTrackingMode.None;
                break;
            case 1003:
                _modes.MouseTracking = on ? MouseTrackingMode.AnyEvent : MouseTrackingMode.None;
                break;
            case 1004:
                _modes.FocusEvents = on;
                break;
            case 1006:
                _modes.MouseSgr = on;
                break;
            case 2004:
                _modes.BracketedPaste = on;
                break;
        }
    }

    // DECSCUSR
    private void SetCursorStyle(int style)
    {
        (CursorShape, CursorBlink) = style switch
        {
            0 or 1 => (CursorShape.Block, true),
            2 => (CursorShape.Block, false),
            3 => (CursorShape.Underline, true),
            4 => (CursorShape.Underline, false),
            5 => (CursorShape.Bar, true),
            6 => (CursorShape.Bar, false),
            _ => (CursorShape, CursorBlink),
        };
    }

    private void DeviceStatusReport(int request)
    {
        if (request == 5)
            Reply("\x1b[0n");
        else if (request == 6)
            Reply($"\x1b[{CursorReportRow()};{_x + 1}R");
    }

    // 1-based cursor row as reported by CPR, relative to the scroll region in origin mode.
    private int CursorReportRow() => _y + 1 - (_modes.Origin ? _top : 0);
}
