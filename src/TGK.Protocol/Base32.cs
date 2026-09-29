using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace TGK.Protocol;

/// <summary>RFC 4648 base32. Encodes upper-case without padding; decoding ignores case and trailing padding.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    public static byte[] Decode(string text) =>
        TryDecode(text, out byte[]? bytes) ? bytes : throw new FormatException("Invalid base32 string.");

    public static bool TryDecode(string? text, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (text is null)
            return false;
        ReadOnlySpan<char> chars = text.AsSpan().TrimEnd('=');
        if (chars.Length % 8 is 1 or 3 or 6)
            return false;

        var result = new byte[chars.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (char c in chars)
        {
            int value = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= 'a' and <= 'z' => c - 'a',
                >= '2' and <= '7' => c - '2' + 26,
                _ => -1,
            };
            if (value < 0)
                return false;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }
        if (buffer != 0) // non-zero leftover bits: not a canonical encoding
            return false;
        bytes = result;
        return true;
    }
}
