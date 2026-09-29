using System;
using System.Text;
using TGK.Protocol;
using Xunit;

namespace TGK.Protocol.Tests;

public class TotpTests
{
    private static readonly string RfcSecret = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    // RFC 6238 appendix B (SHA1); 6-digit codes are the last 6 digits of the 8-digit vectors.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Rfc6238Vectors(long unixTime, string code)
    {
        long step = Totp.GetStep(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Assert.Equal(code, Totp.ComputeCode(RfcSecret, step));
        Assert.True(Totp.Verify(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(unixTime), 0, out long matched));
        Assert.Equal(step, matched);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-2, false)]
    [InlineData(2, false)]
    public void Verify_AcceptsOneStepOfDrift(int offset, bool accepted)
    {
        string secret = Totp.GenerateSecret();
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 10, TimeSpan.Zero);
        long step = Totp.GetStep(now) + offset;

        Assert.Equal(accepted, Totp.Verify(secret, Totp.ComputeCode(secret, step), now, 0, out long matched));
        Assert.Equal(accepted ? step : 0, matched);
    }

    [Fact]
    public void Verify_RejectsReplayOfAcceptedStep()
    {
        string secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        string code = Totp.ComputeCode(secret, Totp.GetStep(now));

        Assert.True(Totp.Verify(secret, code, now, 0, out long matched));
        Assert.False(Totp.Verify(secret, code, now, matched, out _));
        Assert.False(Totp.Verify(secret, code, now.AddSeconds(Totp.PeriodSeconds), matched, out _));

        string next = Totp.ComputeCode(secret, matched + 1);
        Assert.True(Totp.Verify(secret, next, now, matched, out long matchedNext));
        Assert.Equal(matched + 1, matchedNext);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("１２３４５６")] // full-width digits
    public void Verify_RejectsMalformedCodes(string? code) =>
        Assert.False(Totp.Verify(Totp.GenerateSecret(), code, DateTimeOffset.UtcNow, 0, out _));

    [Fact]
    public void Verify_IgnoresSpacesInCode()
    {
        string secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        string code = Totp.ComputeCode(secret, Totp.GetStep(now));
        Assert.True(Totp.Verify(secret, code[..3] + " " + code[3..], now, 0, out _));
    }

    [Fact]
    public void Verify_RejectsInvalidSecret() =>
        Assert.False(Totp.Verify("not base32!", "123456", DateTimeOffset.UtcNow, 0, out _));

    [Fact]
    public void GenerateSecret_Is20RandomBytes()
    {
        string secret = Totp.GenerateSecret();
        Assert.Equal(32, secret.Length);
        Assert.Equal(20, Base32.Decode(secret).Length);
        Assert.True(Totp.IsValidSecret(secret));
        Assert.NotEqual(secret, Totp.GenerateSecret());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("MZXW6YTBOI", false)]              // 6 bytes
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", true)]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJ!", false)]
    public void IsValidSecret(string? secret, bool valid) => Assert.Equal(valid, Totp.IsValidSecret(secret));

    [Fact]
    public void BuildUri_EscapesLabel() =>
        Assert.Equal(
            "otpauth://totp/TGK:a.b%20c?secret=JBSWY3DPEHPK3PXP&issuer=TGK&algorithm=SHA1&digits=6&period=30",
            Totp.BuildUri("a.b c", "JBSWY3DPEHPK3PXP"));
}
