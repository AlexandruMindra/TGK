using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public sealed class SingleSignatureKeySourceTests
{
    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", "rsa-sha2-512")]
    [InlineData("SSH-2.0-OpenSSH_7.2", "rsa-sha2-512")]
    [InlineData("SSH-2.0-OpenSSH_6.6.1p1 Ubuntu-2ubuntu2", "ssh-rsa")]
    [InlineData("SSH-2.0-dropbear_2022.83", "rsa-sha2-256")]
    [InlineData("SSH-2.0-dropbear_2019.78", "ssh-rsa")]
    [InlineData("SSH-2.0-AsyncSSH_2.21.0", null)]
    [InlineData(null, null)]
    public void PicksOneAlgorithmForKnownServers(string? banner, string? expected) =>
        Assert.Equal(expected, SingleSignatureKeySource.PreferredAlgorithm(banner));
}
