using TGK.Client.Terminal;
using Xunit;

namespace TGK.Client.Tests;

public sealed class TerminalFontsTests
{
    // Only these are opened to check that they are monospaced: opening every installed family was too slow.
    [Theory]
    [InlineData("JetBrains Mono", true)]
    [InlineData("Fira Code", true)]
    [InlineData("MesloLGS NF", true)]
    [InlineData("Hack Nerd Font", true)]
    [InlineData("Monaspace Neon", true)]
    [InlineData("Lucida Console", true)]
    [InlineData("Unifont", true)]
    [InlineData("Noto Sans", false)]
    [InlineData("Noto Serif Devanagari", false)]
    [InlineData("Arial", false)]
    public void Candidates_AreTerminalFontsByName(string family, bool expected) =>
        Assert.Equal(expected, TerminalFonts.IsCandidate(family));

    [Fact]
    public void BundledFont_IsAlwaysAvailable_AndFirst()
    {
        Assert.Equal(TerminalFonts.Bundled, TerminalFonts.Available[0]);
        Assert.Equal(TerminalFonts.Retro, TerminalFonts.Available[1]);
        Assert.True(TerminalFonts.IsAvailable(null));
        Assert.True(TerminalFonts.IsAvailable(TerminalFonts.Bundled));
        Assert.False(TerminalFonts.IsAvailable("No Such Font 123"));
    }
}
