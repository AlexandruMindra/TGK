using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TGK.Protocol;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s steps), compatible with Google Authenticator.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const int SecretBytes = 20;
    public const string Issuer = "TGK";

    /// <summary>Steps accepted on either side of the current one, to absorb clock drift.</summary>
    public const int Window = 1;

    /// <summary>A new random secret, base32-encoded.</summary>
    public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>True when <paramref name="secret"/> is base32 for 16 to 64 bytes.</summary>
    public static bool IsValidSecret(string? secret) =>
        Base32.TryDecode(secret, out byte[]? key) && key.Length is >= 16 and <= 64;

    public static long GetStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string ComputeCode(string secret, long step) => ComputeCode(Base32.Decode(secret), step);

    /// <summary>
    /// Checks <paramref name="code"/> against the steps within <see cref="Window"/> of <paramref name="now"/>, skipping
    /// steps at or before <paramref name="lastStep"/> so an accepted code can never be replayed. On success
    /// <paramref name="matchedStep"/> is the step to persist as the new last step. Spaces in the code are ignored.
    /// </summary>
    public static bool Verify(string secret, string? code, DateTimeOffset now, long lastStep, out long matchedStep)
    {
        matchedStep = 0;
        code = code?.Replace(" ", "");
        if (code is not { Length: Digits } || code.AsSpan().ContainsAnyExceptInRange('0', '9'))
            return false;
        if (!Base32.TryDecode(secret, out byte[]? key))
            return false;

        byte[] expected = Encoding.ASCII.GetBytes(code);
        long current = GetStep(now);
        for (long step = Math.Max(current - Window, lastStep + 1); step <= current + Window; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeCode(key, step)), expected))
            {
                matchedStep = step;
                return true;
            }
        }
        return false;
    }

    /// <summary>The <c>otpauth://</c> URI to render as an enrollment QR code.</summary>
    public static string BuildUri(string username, string secret) =>
        $"otpauth://totp/{Issuer}:{Uri.EscapeDataString(username)}?secret={Uri.EscapeDataString(secret)}" +
        $"&issuer={Issuer}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";

    private static string ComputeCode(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);
        int offset = hash[^1] & 0x0F;
        int binary = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
