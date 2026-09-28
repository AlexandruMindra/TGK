using System;

namespace TGK.Core.Models;

public enum AuthKind
{
    Password,
    PrivateKey,
}

/// <summary>
/// Reusable credentials that hosts can reference. Secrets live only in memory on the client;
/// the vault service owns their storage.
/// </summary>
public sealed class Identity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Username { get; set; }
    public AuthKind AuthKind { get; set; } = AuthKind.Password;
    public string? Password { get; set; }

    /// <summary>Private key text (OpenSSH, PEM/PKCS#8 or PuTTY format).</summary>
    public string? PrivateKey { get; set; }

    public string? Passphrase { get; set; }

    /// <summary>Cached display info for the key, e.g. <c>ssh-ed25519</c> (see <c>KeyInspector</c>).</summary>
    public string? KeyType { get; set; }

    /// <summary>Cached public key fingerprint, <c>SHA256:...</c> as printed by OpenSSH.</summary>
    public string? Fingerprint { get; set; }

    public Identity Clone() => (Identity)MemberwiseClone();
}
