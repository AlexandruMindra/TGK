using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace TGK.Core.Ssh;

/// <summary>
/// Offers a key with one signature algorithm instead of all of them. SSH.NET tries an RSA key as rsa-sha2-512,
/// rsa-sha2-256 and ssh-rsa in turn, and a server counts every rejected offer against MaxAuthTries, so a key
/// the server doesn't accept used three attempts ("Too many authentication failures" on hardened servers).
/// The algorithm is picked from the server's version banner, which is known by the time authentication starts.
/// </summary>
internal sealed partial class SingleSignatureKeySource(IPrivateKeySource key, Func<string?> serverVersion) : IPrivateKeySource
{
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms
    {
        get
        {
            IReadOnlyCollection<HostAlgorithm> all = key.HostKeyAlgorithms;
            if (all.Count <= 1)
                return all;
            string? preferred = PreferredAlgorithm(serverVersion());
            HostAlgorithm? match = preferred is null ? null : all.FirstOrDefault(a => a.Name == preferred);
            return match is null ? all : [match];
        }
    }

    /// <summary>The one RSA signature algorithm the server is known to accept, or null to offer them all.</summary>
    internal static string? PreferredAlgorithm(string? serverVersion)
    {
        if (serverVersion is null)
            return null;
        Match openSsh = OpenSshVersion().Match(serverVersion);
        if (openSsh.Success)
            return Version(openSsh) >= new Version(7, 2) ? "rsa-sha2-512" : "ssh-rsa"; // rsa-sha2-* since OpenSSH 7.2
        Match dropbear = DropbearVersion().Match(serverVersion);
        if (dropbear.Success)
            return Version(dropbear) >= new Version(2020, 79) ? "rsa-sha2-256" : "ssh-rsa"; // no rsa-sha2-512 in Dropbear
        return null;
    }

    private static Version Version(Match m) => new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));

    [GeneratedRegex(@"OpenSSH_(\d+)\.(\d+)")]
    private static partial Regex OpenSshVersion();

    [GeneratedRegex(@"dropbear_(\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DropbearVersion();
}
