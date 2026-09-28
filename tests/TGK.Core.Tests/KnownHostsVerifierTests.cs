using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public sealed class KnownHostsVerifierTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly List<HostKeyInfo> _prompts = [];
    private MockVaultService _vault = null!;

    public void Dispose() => _dir.Dispose();

    private async Task<KnownHostsVerifier> CreateAsync(bool userAccepts)
    {
        _vault = new MockVaultService(_dir.Path, TimeSpan.Zero);
        Assert.True((await _vault.LoginAsync("https://tgk.test", "alice", "pw", TestContext.Current.CancellationToken)).Success);
        return new KnownHostsVerifier(_vault, info =>
        {
            _prompts.Add(info);
            return Task.FromResult(userAccepts);
        });
    }

    private static HostKeyInfo Key(string fingerprint, string host = "srv.example.com", int port = 22) =>
        new(host, port, "ssh-ed25519", fingerprint);

    [Fact]
    public async Task KnownMatchingKey_IsAcceptedWithoutPrompt()
    {
        KnownHostsVerifier verifier = await CreateAsync(userAccepts: false);
        await _vault.AddKnownHostAsync(new KnownHost { Host = "srv.example.com", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:aaa" });

        Assert.True(await verifier.VerifyAsync(Key("SHA256:aaa", host: "SRV.example.com"), TestContext.Current.CancellationToken));
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task UnknownKey_Accepted_IsPromptedAndStored()
    {
        KnownHostsVerifier verifier = await CreateAsync(userAccepts: true);

        Assert.True(await verifier.VerifyAsync(Key("SHA256:new"), TestContext.Current.CancellationToken));

        HostKeyInfo prompt = Assert.Single(_prompts);
        Assert.False(prompt.IsChanged);
        Assert.Null(prompt.KnownFingerprint);
        KnownHost stored = _vault.Current.FindKnownHost("srv.example.com", 22)!;
        Assert.Equal("SHA256:new", stored.FingerprintSha256);
        Assert.Equal("ssh-ed25519", stored.KeyType);

        // Second connection: now known, no prompt.
        Assert.True(await verifier.VerifyAsync(Key("SHA256:new"), TestContext.Current.CancellationToken));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task UnknownKey_Rejected_IsNotStored()
    {
        KnownHostsVerifier verifier = await CreateAsync(userAccepts: false);

        Assert.False(await verifier.VerifyAsync(Key("SHA256:new"), TestContext.Current.CancellationToken));

        Assert.Single(_prompts);
        Assert.Empty(_vault.Current.KnownHosts);
    }

    [Fact]
    public async Task ChangedKey_PromptsWithKnownFingerprint_AndRejectKeepsOldKey()
    {
        KnownHostsVerifier verifier = await CreateAsync(userAccepts: false);
        await _vault.AddKnownHostAsync(new KnownHost { Host = "srv.example.com", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:old" });

        Assert.False(await verifier.VerifyAsync(Key("SHA256:evil"), TestContext.Current.CancellationToken));

        HostKeyInfo prompt = Assert.Single(_prompts);
        Assert.True(prompt.IsChanged);
        Assert.Equal("SHA256:old", prompt.KnownFingerprint);
        Assert.Equal("SHA256:evil", prompt.FingerprintSha256);
        Assert.Equal("SHA256:old", _vault.Current.FindKnownHost("srv.example.com", 22)!.FingerprintSha256);
    }

    [Fact]
    public async Task ChangedKey_Accepted_ReplacesStoredKey()
    {
        KnownHostsVerifier verifier = await CreateAsync(userAccepts: true);
        await _vault.AddKnownHostAsync(new KnownHost { Host = "srv.example.com", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:old" });

        Assert.True(await verifier.VerifyAsync(Key("SHA256:rotated"), TestContext.Current.CancellationToken));

        KnownHost stored = Assert.Single(_vault.Current.KnownHosts);
        Assert.Equal("SHA256:rotated", stored.FingerprintSha256);
    }

    [Fact]
    public async Task DifferentPort_IsADifferentHost()
    {
        KnownHostsVerifier verifier = await CreateAsync(userAccepts: true);
        await _vault.AddKnownHostAsync(new KnownHost { Host = "srv.example.com", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:aaa" });

        Assert.True(await verifier.VerifyAsync(Key("SHA256:bbb", port: 2222), TestContext.Current.CancellationToken));

        Assert.False(Assert.Single(_prompts).IsChanged);
        Assert.Equal(2, _vault.Current.KnownHosts.Count);
    }

    [Fact]
    public async Task CancelledPrompt_Throws()
    {
        _vault = new MockVaultService(_dir.Path, TimeSpan.Zero);
        Assert.True((await _vault.LoginAsync("https://tgk.test", "alice", "pw", TestContext.Current.CancellationToken)).Success);
        var neverAnswered = new TaskCompletionSource<bool>();
        var verifier = new KnownHostsVerifier(_vault, (_, _) => neverAnswered.Task);
        using var cts = new CancellationTokenSource();

        Task<bool> verify = verifier.VerifyAsync(Key("SHA256:x"), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verify);
        Assert.Empty(_vault.Current.KnownHosts);
    }
}
