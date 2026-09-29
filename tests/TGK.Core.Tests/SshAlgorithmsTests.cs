using System.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public class SshAlgorithmsTests
{
    private static ConnectionInfo NewInfo() => new("h", 22, "u", new PasswordAuthenticationMethod("u", "p"));

    [Fact]
    public void RemoveLegacy_DropsExactlyTheLegacyLists()
    {
        ConnectionInfo full = NewInfo(), info = NewInfo();
        SshAlgorithms.RemoveLegacy(info);

        Assert.Equal(full.KeyExchangeAlgorithms.Keys.Except(SshAlgorithms.LegacyKeyExchange), info.KeyExchangeAlgorithms.Keys);
        Assert.Equal(full.HostKeyAlgorithms.Keys.Except(SshAlgorithms.LegacyHostKeys), info.HostKeyAlgorithms.Keys);
        Assert.Equal(full.Encryptions.Keys.Except(SshAlgorithms.LegacyCiphers), info.Encryptions.Keys);
        Assert.Equal(full.HmacAlgorithms.Keys.Except(SshAlgorithms.LegacyMacs), info.HmacAlgorithms.Keys);
        // What SSH.NET 2026.0.0 offers that is legacy (the rest of the lists are names it doesn't implement).
        Assert.Contains("diffie-hellman-group1-sha1", full.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("diffie-hellman-group1-sha1", info.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("diffie-hellman-group14-sha1", info.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("ssh-rsa", info.HostKeyAlgorithms.Keys);
        Assert.DoesNotContain("3des-cbc", info.Encryptions.Keys);
        Assert.DoesNotContain("aes256-cbc", info.Encryptions.Keys);
    }

    [Fact]
    public void RemoveLegacy_KeepsModernAlgorithms()
    {
        ConnectionInfo info = NewInfo();
        SshAlgorithms.RemoveLegacy(info);

        Assert.Contains("mlkem768x25519-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("curve25519-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("diffie-hellman-group14-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("ssh-ed25519", info.HostKeyAlgorithms.Keys);
        Assert.Contains("rsa-sha2-512", info.HostKeyAlgorithms.Keys);
        Assert.Contains("ecdsa-sha2-nistp256", info.HostKeyAlgorithms.Keys);
        Assert.Contains("chacha20-poly1305@openssh.com", info.Encryptions.Keys);
        Assert.Contains("aes256-gcm@openssh.com", info.Encryptions.Keys);
        Assert.Contains("aes128-ctr", info.Encryptions.Keys);
        Assert.Contains("hmac-sha2-256-etm@openssh.com", info.HmacAlgorithms.Keys);
        Assert.Contains("hmac-sha1", info.HmacAlgorithms.Keys); // still offered by OpenSSH
    }

    [Theory]
    [InlineData(false, "Legacy algorithms")]
    [InlineData(true, "no algorithm in common")]
    public void NegotiationFailure_IsAlgorithmMismatch_SuggestingLegacyWhenOff(bool legacy, string expected)
    {
        var request = new SshConnectRequest { Host = "old-router", Username = "admin", Password = "pw", LegacyAlgorithms = legacy };

        SshSessionException error = SshErrors.Map(new SshConnectionException("No matching key exchange algorithm (server offers diffie-hellman-group1-sha1)"), request);

        Assert.Equal(SshErrorKind.AlgorithmMismatch, error.Kind);
        Assert.Contains(expected, error.Message);
        Assert.Contains("key exchange algorithm (server offers diffie-hellman-group1-sha1)", error.Message);
    }
}
