using TGK.Protocol;
using Xunit;

namespace TGK.Protocol.Tests;

public class UsernameRulesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("John.Doe-2_x")]
    [InlineData("12345678901234567890123456789012")]
    public void AcceptsValidNames(string name) => Assert.Null(UsernameRules.Validate(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData("a b")]
    [InlineData("user@host")]
    [InlineData("ünï")]
    [InlineData("abc/")]
    public void RejectsInvalidNames(string? name) => Assert.NotNull(UsernameRules.Validate(name));
}
