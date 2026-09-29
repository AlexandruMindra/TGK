using System.Text;
using TGK.Protocol;
using Xunit;

namespace TGK.Protocol.Tests;

public class Base32Tests
{
    // RFC 4648 section 10, without padding.
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Rfc4648Vectors(string plain, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded)));
    }

    [Theory]
    [InlineData("MZXW6YQ=", "foob")]
    [InlineData("MY======", "f")]
    [InlineData("mzxw6ytboi", "foobar")]
    public void Decode_AcceptsPaddingAndLowerCase(string encoded, string plain) =>
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded)));

    [Theory]
    [InlineData(null)]
    [InlineData("M")]          // impossible length
    [InlineData("MZX")]
    [InlineData("MZXW6Y")]
    [InlineData("MZXW6Y1B")]   // '1' is not in the alphabet
    [InlineData("MZ")]         // non-zero leftover bits
    [InlineData("MZ XW")]
    public void TryDecode_RejectsInvalidInput(string? encoded) => Assert.False(Base32.TryDecode(encoded, out _));
}
