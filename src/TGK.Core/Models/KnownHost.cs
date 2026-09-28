using System;

namespace TGK.Core.Models;

/// <summary>A trusted server host key (the vault's equivalent of an OpenSSH known_hosts line).</summary>
public sealed class KnownHost
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string KeyType { get; set; } = "";

    /// <summary>OpenSSH-style fingerprint: <c>SHA256:</c> followed by unpadded base64.</summary>
    public string FingerprintSha256 { get; set; } = "";

    public DateTimeOffset AddedAt { get; set; }

    public bool Matches(string host, int port) =>
        Port == port && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);

    public KnownHost Clone() => (KnownHost)MemberwiseClone();
}
