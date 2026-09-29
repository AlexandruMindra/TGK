using System;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Core.Ssh;

/// <summary>
/// Trust-on-first-use host key checking against the vault's known hosts: a known, matching key is accepted
/// silently; an unknown or changed key is shown to the user, and an accepted key is stored in the vault.
/// </summary>
public sealed class KnownHostsVerifier : IHostKeyVerifier
{
    private readonly IVaultService _vault;
    private readonly Func<HostKeyInfo, CancellationToken, Task<bool>> _promptUser;

    /// <param name="promptUser">
    /// Asks the user whether to trust the key (<see cref="HostKeyInfo.IsChanged"/> tells a changed key from a new one).
    /// Invoked on a worker thread: marshal to the UI thread before showing anything.
    /// </param>
    public KnownHostsVerifier(IVaultService vault, Func<HostKeyInfo, Task<bool>> promptUser)
        : this(vault, (info, _) => promptUser(info))
    {
        ArgumentNullException.ThrowIfNull(promptUser);
    }

    /// <param name="promptUser">As above; the token is cancelled when the connection attempt is abandoned so the prompt can close itself.</param>
    public KnownHostsVerifier(IVaultService vault, Func<HostKeyInfo, CancellationToken, Task<bool>> promptUser)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _promptUser = promptUser ?? throw new ArgumentNullException(nameof(promptUser));
    }

    public async Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        KnownHost? known = _vault.Current.FindKnownHost(info.Host, info.Port);
        if (known is not null && string.Equals(known.FingerprintSha256, info.FingerprintSha256, StringComparison.Ordinal))
            return true;

        bool accepted = await _promptUser(info with { KnownFingerprint = known?.FingerprintSha256 }, ct)
            .WaitAsync(ct).ConfigureAwait(false);
        if (!accepted)
            return false;

        // Stored without waiting for the server: the push can take long (or fail and be retried), and this runs
        // inside the SSH handshake.
        Task stored;
        try
        {
            stored = _vault.AddKnownHostAsync(new KnownHost
            {
                Host = info.Host,
                Port = info.Port,
                KeyType = info.KeyType,
                FingerprintSha256 = info.FingerprintSha256,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (InvalidOperationException)
        {
            return true; // not logged in: the key is trusted for this connection only
        }
        _ = stored.ContinueWith(t => CoreLog.Warn($"Could not store the host key of {info.Host}: {t.Exception!.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return true;
    }
}
