using System;

namespace TGK.Terminal;

/// <summary>
/// The parameters, private marker and intermediates of a CSI sequence. Reused by the parser for every
/// sequence, so handlers must not keep a reference to it.
/// </summary>
internal sealed class VtParams
{
    public const int MaxParams = 32;
    public const int MaxValue = 65535;

    private readonly int[] _values = new int[MaxParams];
    private uint _subMask; // bit i set: parameter i was introduced by ':' (a sub-parameter of the previous one)
    private bool _overflow;

    /// <summary>Number of parameters, including empty ones (which read as 0).</summary>
    public int Count { get; private set; }

    /// <summary>'?', '>', '&lt;', '=' or '\0'.</summary>
    public char PrivateMarker { get; set; }

    /// <summary>Intermediate bytes packed big-endian (a single intermediate equals its char code); -1 if too many.</summary>
    public int Intermediates { get; set; }

    public int this[int index] => _values[index];

    /// <summary>Returns parameter <paramref name="index"/>, or <paramref name="defaultValue"/> when it is missing or 0.</summary>
    public int Get(int index, int defaultValue)
    {
        if (index >= Count)
            return defaultValue;
        int v = _values[index];
        return v == 0 ? defaultValue : v;
    }

    /// <summary>True when parameter <paramref name="index"/> was separated from its predecessor by a colon.</summary>
    public bool IsSubParam(int index) => (_subMask & (1u << index)) != 0;

    public void Clear()
    {
        Count = 0;
        _subMask = 0;
        _overflow = false;
        PrivateMarker = '\0';
        Intermediates = 0;
    }

    public void AddDigit(int digit)
    {
        if (Count == 0)
        {
            Count = 1;
            _values[0] = 0;
        }

        if (_overflow)
            return;
        ref int v = ref _values[Count - 1];
        v = Math.Min((v * 10) + digit, MaxValue);
    }

    /// <summary>Handles ';' (<paramref name="colon"/> false) or ':' by starting the next parameter.</summary>
    public void NextParam(bool colon)
    {
        if (Count == 0)
        {
            Count = 1;
            _values[0] = 0;
        }

        if (Count == MaxParams)
        {
            _overflow = true; // further parameters are dropped
            return;
        }

        _values[Count] = 0;
        if (colon)
            _subMask |= 1u << Count;
        Count++;
    }

    public void AddIntermediate(int c) =>
        Intermediates = Intermediates is < 0 or > 0xFFFF ? -1 : (Intermediates << 8) | c;
}
