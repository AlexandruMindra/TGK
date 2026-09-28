using System;

namespace TGK.Terminal;

/// <summary>Actions emitted by <see cref="VtParser"/>. DCS, SOS, PM and APC strings are consumed by the parser.</summary>
internal interface IVtHandler
{
    /// <summary>A graphic character (already UTF-8 decoded).</summary>
    void Print(int rune);

    /// <summary>A run of printable ASCII (0x20-0x7E); equivalent to <see cref="Print"/> for each byte.</summary>
    void PrintAscii(ReadOnlySpan<byte> text);

    /// <summary>A C0 control or a C1 control that does not start a string or control sequence.</summary>
    void Execute(int control);

    void EscDispatch(int intermediates, char final);

    void CsiDispatch(VtParams parameters, char final);

    /// <summary>A complete OSC string (without the introducer and terminator).</summary>
    void OscDispatch(ReadOnlySpan<char> data);
}
