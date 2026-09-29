using System.Collections.Generic;
using Renci.SshNet;

namespace TGK.Core.Ssh;

/// <summary>
/// Weak algorithms a connection only offers with "Legacy algorithms" turned on. The lists mirror what current OpenSSH
/// clients no longer offer by default (SHA-1 key exchange and host key signatures, CBC and older ciphers, MD5/RIPEMD
/// MACs), so a server a stock <c>ssh</c> reaches is reached without them. hmac-sha1 stays: OpenSSH still offers it.
/// Names SSH.NET does not implement are listed too, so they stay excluded if a later version adds them.
/// </summary>
public static class SshAlgorithms
{
    public static IReadOnlyList<string> LegacyKeyExchange { get; } =
        ["diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1"];

    /// <summary>ssh-rsa here is the SHA-1 signature of an RSA host key; RSA keys still work as rsa-sha2-256/512.</summary>
    public static IReadOnlyList<string> LegacyHostKeys { get; } =
        ["ssh-rsa", "ssh-rsa-cert-v01@openssh.com", "ssh-dss", "ssh-dss-cert-v01@openssh.com"];

    public static IReadOnlyList<string> LegacyCiphers { get; } =
        ["3des-cbc", "aes128-cbc", "aes192-cbc", "aes256-cbc", "blowfish-cbc", "cast128-cbc", "arcfour", "arcfour128", "arcfour256", "rijndael-cbc@lysator.liu.se"];

    public static IReadOnlyList<string> LegacyMacs { get; } =
        ["hmac-md5", "hmac-md5-96", "hmac-md5-etm@openssh.com", "hmac-md5-96-etm@openssh.com", "hmac-sha1-96", "hmac-sha1-96-etm@openssh.com",
         "hmac-ripemd160", "hmac-ripemd160@openssh.com", "hmac-ripemd160-etm@openssh.com"];

    /// <summary>Removes the legacy algorithms from what <paramref name="info"/> offers.</summary>
    internal static void RemoveLegacy(ConnectionInfo info)
    {
        foreach (string name in LegacyKeyExchange)
            info.KeyExchangeAlgorithms.Remove(name);
        foreach (string name in LegacyHostKeys)
            info.HostKeyAlgorithms.Remove(name);
        foreach (string name in LegacyCiphers)
            info.Encryptions.Remove(name);
        foreach (string name in LegacyMacs)
            info.HmacAlgorithms.Remove(name);
    }
}
