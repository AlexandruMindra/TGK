using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Ssh;

/// <summary>The server's host key as presented during key exchange.</summary>
/// <param name="KeyType">Host key algorithm, e.g. <c>ssh-ed25519</c> or <c>rsa-sha2-512</c>.</param>
/// <param name="FingerprintSha256">OpenSSH-style fingerprint: <c>SHA256:</c> + unpadded base64.</param>
/// <param name="KnownFingerprint">The previously trusted fingerprint when the key CHANGED; null for an unknown host.</param>
public sealed record HostKeyInfo(string Host, int Port, string KeyType, string FingerprintSha256, string? KnownFingerprint = null)
{
    public bool IsChanged => KnownFingerprint is not null;
}

/// <summary>Decides whether to trust a server's host key.</summary>
public interface IHostKeyVerifier
{
    /// <summary>
    /// Called on an SSH worker thread (never the UI thread) while the connection waits; returning false aborts it.
    /// <paramref name="ct"/> is cancelled when the connection attempt is abandoned, so any open prompt should close.
    /// </summary>
    Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct);
}
