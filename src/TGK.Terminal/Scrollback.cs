using System;

namespace TGK.Terminal;

/// <summary>Bounded ring buffer of lines that scrolled off the top of the main screen.</summary>
internal sealed class Scrollback
{
    private TerminalLine?[] _items = [];
    private int _start; // physical index of the oldest line

    public Scrollback(int limit) => Limit = Math.Max(0, limit);

    public int Limit { get; }

    public int Count { get; private set; }

    /// <summary>Line <paramref name="index"/> counted from the oldest (0) to the newest (Count - 1).</summary>
    public TerminalLine this[int index] => _items[(_start + index) % _items.Length]!;

    /// <summary>
    /// Appends a line. When the limit is reached the oldest line is evicted and returned, so the caller can
    /// recycle it instead of allocating.
    /// </summary>
    public TerminalLine? Push(TerminalLine line)
    {
        if (Limit == 0)
            return null;

        if (Count == _items.Length)
        {
            if (Count == Limit)
            {
                var evicted = _items[_start];
                _items[_start] = line;
                _start = (_start + 1) % _items.Length;
                return evicted;
            }

            Grow();
        }

        _items[(_start + Count) % _items.Length] = line;
        Count++;
        return null;
    }

    public TerminalLine PopNewest()
    {
        Count--;
        int index = (_start + Count) % _items.Length;
        var line = _items[index]!;
        _items[index] = null;
        return line;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    private void Grow()
    {
        var items = new TerminalLine?[Math.Min(Limit, Math.Max(256, _items.Length * 2))];
        for (int i = 0; i < Count; i++)
            items[i] = _items[(_start + i) % _items.Length];
        _items = items;
        _start = 0;
    }
}
