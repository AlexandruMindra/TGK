using System;

namespace TGK.Terminal;

/// <summary>
/// Streaming UTF-8 decoder plus the DEC/ANSI escape sequence state machine described by Paul Williams
/// (vt100.net/emu/dec_ansi_parser), extended with colon sub-parameters and BEL-terminated OSC strings.
/// Decoding happens before the state machine, so C1 controls are recognised as U+0080..U+009F (the
/// xterm UTF-8 convention). Invalid UTF-8 yields U+FFFD. The hot path allocates nothing.
/// </summary>
internal sealed class VtParser
{
    private const int Replacement = 0xFFFD;
    private const int MaxOscLength = 4096;

    private enum State : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,

        // DCS, SOS, PM and APC: swallowed until ST, CAN or SUB.
        IgnoredString,
    }

    private readonly IVtHandler _handler;
    private readonly VtParams _params = new();
    private readonly char[] _osc = new char[MaxOscLength];
    private int _oscLength;
    private State _state;

    private int _utf8Code;
    private int _utf8Remaining;
    private int _utf8Min;

    public VtParser(IVtHandler handler) => _handler = handler;

    public void Reset()
    {
        _state = State.Ground;
        _utf8Remaining = 0;
        _params.Clear();
        _oscLength = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int b = data[i];
            if (_utf8Remaining == 0)
            {
                if (b is >= 0x20 and < 0x7F && _state == State.Ground)
                {
                    // Fast path: hand runs of printable ASCII to the handler in one call.
                    int run = data[i..].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
                    if (run < 0)
                        run = data.Length - i;
                    _handler.PrintAscii(data.Slice(i, run));
                    i += run - 1;
                }
                else if (b < 0x80)
                    Advance(b);
                else if (b is >= 0xC2 and <= 0xDF)
                    StartSequence(b & 0x1F, 1, 0x80);
                else if ((b & 0xF0) == 0xE0)
                    StartSequence(b & 0x0F, 2, 0x800);
                else if (b is >= 0xF0 and <= 0xF4)
                    StartSequence(b & 0x07, 3, 0x10000);
                else
                    Advance(Replacement);
            }
            else if ((b & 0xC0) == 0x80)
            {
                _utf8Code = (_utf8Code << 6) | (b & 0x3F);
                if (--_utf8Remaining == 0)
                {
                    int cp = _utf8Code;
                    bool invalid = cp < _utf8Min || cp > 0x10FFFF || (cp is >= 0xD800 and <= 0xDFFF);
                    Advance(invalid ? Replacement : cp);
                }
            }
            else
            {
                // Truncated sequence: emit one replacement character and reprocess this byte on its own.
                _utf8Remaining = 0;
                Advance(Replacement);
                i--;
            }
        }
    }

    private void StartSequence(int bits, int remaining, int min)
    {
        _utf8Code = bits;
        _utf8Remaining = remaining;
        _utf8Min = min;
    }

    private void Advance(int c)
    {
        // Printable (decoded) characters in the ground state.
        if (_state == State.Ground && c >= 0x20 && c != 0x7F && (c < 0x80 || c >= 0xA0))
        {
            _handler.Print(c);
            return;
        }

        // Transitions valid from any state.
        if (c == 0x1B)
        {
            if (_state == State.OscString)
                DispatchOsc(); // ESC \ (ST) terminates the string; the '\' is then a no-op ESC dispatch.
            EnterEscape();
            return;
        }

        if (c is 0x18 or 0x1A)
        {
            _state = State.Ground; // CAN / SUB abort any sequence
            return;
        }

        if (c is >= 0x80 and <= 0x9F)
        {
            HandleC1(c);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (c < 0x20)
                    _handler.Execute(c);
                break; // DEL is ignored

            case State.Escape:
                if (c < 0x20)
                    _handler.Execute(c);
                else if (c <= 0x2F)
                {
                    _params.AddIntermediate(c);
                    _state = State.EscapeIntermediate;
                }
                else if (c == '[')
                    EnterCsi();
                else if (c == ']')
                    EnterOsc();
                else if (c is 'P' or 'X' or '^' or '_')
                    _state = State.IgnoredString;
                else if (c <= 0x7E)
                    DispatchEsc(c);
                else if (c != 0x7F)
                    AbortAndPrint(c);
                break;

            case State.EscapeIntermediate:
                if (c < 0x20)
                    _handler.Execute(c);
                else if (c <= 0x2F)
                    _params.AddIntermediate(c);
                else if (c <= 0x7E)
                    DispatchEsc(c);
                else if (c != 0x7F)
                    AbortAndPrint(c);
                break;

            case State.CsiEntry:
            case State.CsiParam:
                if (c < 0x20)
                    _handler.Execute(c);
                else if (c is >= '0' and <= '9')
                {
                    _params.AddDigit(c - '0');
                    _state = State.CsiParam;
                }
                else if (c is ';' or ':')
                {
                    _params.NextParam(c == ':');
                    _state = State.CsiParam;
                }
                else if (c is >= 0x3C and <= 0x3F)
                {
                    // A private marker is only valid as the first byte.
                    if (_state == State.CsiEntry)
                    {
                        _params.PrivateMarker = (char)c;
                        _state = State.CsiParam;
                    }
                    else
                        _state = State.CsiIgnore;
                }
                else if (c <= 0x2F)
                {
                    _params.AddIntermediate(c);
                    _state = State.CsiIntermediate;
                }
                else if (c <= 0x7E)
                    DispatchCsi(c);
                else if (c != 0x7F)
                    AbortAndPrint(c);
                break;

            case State.CsiIntermediate:
                if (c < 0x20)
                    _handler.Execute(c);
                else if (c <= 0x2F)
                    _params.AddIntermediate(c);
                else if (c <= 0x3F)
                    _state = State.CsiIgnore;
                else if (c <= 0x7E)
                    DispatchCsi(c);
                else if (c != 0x7F)
                    AbortAndPrint(c);
                break;

            case State.CsiIgnore:
                if (c < 0x20)
                    _handler.Execute(c);
                else if (c is >= 0x40 and <= 0x7E)
                    _state = State.Ground;
                break;

            case State.OscString:
                if (c == 0x07)
                {
                    DispatchOsc(); // xterm accepts BEL as terminator
                    _state = State.Ground;
                }
                else if (c >= 0x20)
                    PutOsc(c);
                break;

            case State.IgnoredString:
                break;
        }
    }

    private void HandleC1(int c)
    {
        if (_state == State.OscString && c == 0x9C)
            DispatchOsc();

        switch (c)
        {
            case 0x90: // DCS
            case 0x98: // SOS
            case 0x9E: // PM
            case 0x9F: // APC
                _state = State.IgnoredString;
                break;
            case 0x9B:
                EnterCsi();
                break;
            case 0x9D:
                EnterOsc();
                break;
            case 0x9C: // ST
                _state = State.Ground;
                break;
            default:
                _state = State.Ground;
                _handler.Execute(c);
                break;
        }
    }

    // A non-ASCII character inside an escape or control sequence: drop the sequence, keep the character.
    private void AbortAndPrint(int c)
    {
        _state = State.Ground;
        _handler.Print(c);
    }

    private void EnterEscape()
    {
        _params.Clear();
        _state = State.Escape;
    }

    private void EnterCsi()
    {
        _params.Clear();
        _state = State.CsiEntry;
    }

    private void EnterOsc()
    {
        _oscLength = 0;
        _state = State.OscString;
    }

    private void DispatchEsc(int final)
    {
        _state = State.Ground;
        if (_params.Intermediates >= 0)
            _handler.EscDispatch(_params.Intermediates, (char)final);
    }

    private void DispatchCsi(int final)
    {
        _state = State.Ground;
        if (_params.Intermediates >= 0)
            _handler.CsiDispatch(_params, (char)final);
    }

    private void PutOsc(int c)
    {
        int needed = c > 0xFFFF ? 2 : 1;
        if (_oscLength + needed > MaxOscLength)
            return; // overlong strings are truncated
        if (needed == 2)
        {
            int v = c - 0x10000;
            _osc[_oscLength++] = (char)(0xD800 + (v >> 10));
            _osc[_oscLength++] = (char)(0xDC00 + (v & 0x3FF));
        }
        else
            _osc[_oscLength++] = (char)c;
    }

    private void DispatchOsc()
    {
        _handler.OscDispatch(_osc.AsSpan(0, _oscLength));
        _oscLength = 0;
    }
}
