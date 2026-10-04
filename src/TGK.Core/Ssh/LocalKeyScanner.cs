using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TGK.Core.Ssh;

/// <summary>A private key file found on this device.</summary>
/// <param name="KeyType">OpenSSH algorithm name (<c>ssh-ed25519</c>, <c>ssh-rsa</c>, ...), or null when only the
/// file format is known (an encrypted PEM key without its <c>.pub</c>).</param>
/// <param name="Fingerprint"><c>SHA256:...</c> of the public key, or null when it cannot be read without the passphrase.</param>
public sealed record LocalKey(string Path, string? KeyType, string? Fingerprint, bool Encrypted, string? Comment);

/// <summary>
/// Finds the user's SSH private keys without walking the disk: only where keys are kept — <c>~/.ssh</c> (one level of
/// subfolders), the <c>IdentityFile</c>s of <c>~/.ssh/config</c>, the keys saved PuTTY and WinSCP sessions point to — and,
/// on request, the top level of Downloads, Desktop and Documents (where keys from cloud providers usually land).
/// Files are recognized by their header; nothing is decrypted.
/// </summary>
public static class LocalKeyScanner
{
    private const int MinKeyBytes = 100, MaxKeyBytes = 64 * 1024, MaxFilesPerFolder = 500;

    private static readonly HashSet<string> Supported =
        ["ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521"];

    /// <param name="home">The user's home folder (defaults to the current user's).</param>
    /// <param name="includeUserFolders">Also look at the top level of Downloads, Desktop and Documents.</param>
    public static List<LocalKey> Scan(string? home = null, bool includeUserFolders = false, CancellationToken ct = default)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<LocalKey>();
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string path)
        {
            ct.ThrowIfCancellationRequested();
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (!seenPaths.Add(full) || Probe(full) is not { } key)
                return;
            if (key.Fingerprint is null || seenFingerprints.Add(key.Fingerprint))
                found.Add(key);
        }

        string sshDir = System.IO.Path.Combine(home, ".ssh");
        foreach (string file in FilesIn(sshDir).OrderBy(DefaultKeyRank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            Consider(file);
        foreach (string dir in SubfoldersOf(sshDir))
            foreach (string file in FilesIn(dir))
                Consider(file);
        foreach (string file in ConfiguredKeyPaths(home))
            Consider(file);
        if (includeUserFolders)
            foreach (string dir in UserFolders(home))
                foreach (string file in FilesIn(dir))
                    Consider(file);
        return found;
    }

    /// <summary>Reads <paramref name="path"/> if it looks like a private key; null otherwise (or when unsupported).</summary>
    public static LocalKey? Probe(string path)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            // The size check also skips sockets and FIFOs (ssh's ControlPath) without opening them.
            if (!info.Exists || info.Length < MinKeyBytes || info.Length > MaxKeyBytes || path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                return null;
            using (FileStream stream = info.OpenRead())
            {
                Span<byte> head = stackalloc byte[64];
                int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (!LooksLikeKey(Encoding.ASCII.GetString(head[..n])))
                    return null;
            }
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        LocalKey? key = text.TrimStart().StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal)
            ? PuttyKey(path, text)
            : text.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal)
                ? OpenSshKey(path, text)
                : PemKey(path, text);
        if (key is null || (key.KeyType is { } type && !Supported.Contains(type)))
            return null;
        // The .pub next to the key names it (its comment) and fills in what an encrypted key hides.
        if (ReadPublicKeyFile(path + ".pub") is { } pub && (key.Fingerprint is null || key.Fingerprint == pub.Fingerprint))
            key = key with { KeyType = pub.Type, Fingerprint = pub.Fingerprint, Comment = key.Comment ?? pub.Comment };
        return key;
    }

    private static bool LooksLikeKey(string head)
    {
        head = head.TrimStart();
        return head.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal)
            || (head.StartsWith("-----BEGIN ", StringComparison.Ordinal) && head.Contains("PRIVATE KEY-----", StringComparison.Ordinal));
    }

    // ---- formats ----

    /// <summary>The public key and cipher name are stored in the clear in <c>openssh-key-v1</c>, even for encrypted keys.</summary>
    private static LocalKey? OpenSshKey(string path, string text)
    {
        if (Armored(text, "OPENSSH PRIVATE KEY") is not { } body || !body.AsSpan().StartsWith("openssh-key-v1\0"u8))
            return null;
        var reader = new SshReader(body, "openssh-key-v1\0".Length);
        string? cipher = reader.Text();
        if (cipher is null || reader.Bytes() is null || reader.Bytes() is null || reader.UInt32() is not >= 1)
            return null;
        if (reader.Bytes() is not { } blob || BlobType(blob) is not { } type)
            return null;
        return new LocalKey(path, type, KeyInspector.Fingerprint(blob), cipher != "none", null);
    }

    /// <summary>PuTTY's <c>.ppk</c>: the public key is a plain base64 block, the private one may be encrypted.</summary>
    private static LocalKey? PuttyKey(string path, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string? Header(string name) =>
            lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal)) is { } line ? line[(name.Length + 1)..].Trim() : null;

        bool encrypted = Header("Encryption") is { } enc && enc != "none";
        int at = Array.FindIndex(lines, l => l.StartsWith("Public-Lines:", StringComparison.Ordinal));
        if (at < 0 || !int.TryParse(lines[at]["Public-Lines:".Length..].Trim(), out int count) || count <= 0 || at + count >= lines.Length)
            return null;
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(string.Concat(lines.Skip(at + 1).Take(count).Select(l => l.Trim())));
        }
        catch (FormatException)
        {
            return null;
        }
        return BlobType(blob) is { } type ? new LocalKey(path, type, KeyInspector.Fingerprint(blob), encrypted, Header("Comment")) : null;
    }

    /// <summary>PEM / PKCS#8: unencrypted keys are parsed; encrypted ones can only be named by their <c>.pub</c>.</summary>
    private static LocalKey? PemKey(string path, string text)
    {
        bool encrypted = text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal);
        if (encrypted)
        {
            string? type = text.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal) ? "ssh-rsa"
                : text.Contains("BEGIN DSA PRIVATE KEY", StringComparison.Ordinal) ? "ssh-dss"
                : null;
            return new LocalKey(path, type, null, true, null);
        }
        return KeyInspector.TryInspect(text, null, out string keyType, out string fingerprint, out _)
            ? new LocalKey(path, keyType, fingerprint, false, null)
            : null;
    }

    private static (string Type, string Fingerprint, string? Comment)? ReadPublicKeyFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxKeyBytes)
                return null;
            string[] parts = File.ReadAllText(path).Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return null;
            byte[] blob = Convert.FromBase64String(parts[1]);
            if (BlobType(blob) != parts[0])
                return null;
            return (parts[0], KeyInspector.Fingerprint(blob), parts.Length > 2 ? parts[2].Trim() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? Armored(string text, string label)
    {
        string begin = $"-----BEGIN {label}-----", end = $"-----END {label}-----";
        int start = text.IndexOf(begin, StringComparison.Ordinal), stop = text.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < start)
            return null;
        try
        {
            return Convert.FromBase64String(text[(start + begin.Length)..stop]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The algorithm name at the start of a public key blob.</summary>
    private static string? BlobType(byte[] blob) => new SshReader(blob, 0).Text();

    // ---- where to look ----

    /// <summary>The usual key names first, newest algorithms first, like <c>ssh</c> tries them.</summary>
    private static int DefaultKeyRank(string path) => System.IO.Path.GetFileName(path) switch
    {
        "id_ed25519" => 0,
        "id_ecdsa" => 1,
        "id_rsa" => 2,
        _ => 3,
    };

    private static IEnumerable<string> FilesIn(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Take(MaxFilesPerFolder).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Subfolders, not following links (a link could lead anywhere, or loop).</summary>
    private static IEnumerable<string> SubfoldersOf(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return [];
            return new DirectoryInfo(dir).EnumerateDirectories()
                .Where(d => d.LinkTarget is null)
                .Select(d => d.FullName)
                .Take(50)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Keys referenced by the SSH config and by saved PuTTY / WinSCP sessions.</summary>
    internal static IEnumerable<string> ConfiguredKeyPaths(string home)
    {
        var paths = new List<string>();
        paths.AddRange(SshConfigIdentityFiles(home, System.IO.Path.Combine(home, ".ssh", "config")));
        // PuTTY on Linux/macOS keeps sessions as files: "PublicKeyFile=/path/key.ppk".
        foreach (string session in FilesIn(System.IO.Path.Combine(home, ".putty", "sessions")))
        {
            try
            {
                foreach (string line in File.ReadLines(session))
                    if (line.StartsWith("PublicKeyFile=", StringComparison.Ordinal) && line["PublicKeyFile=".Length..].Trim() is { Length: > 0 } key)
                        paths.Add(ExpandHome(key, home));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (OperatingSystem.IsWindows())
            paths.AddRange(WindowsSessionKeys());
        return paths;
    }

    /// <summary><c>IdentityFile</c> entries of an OpenSSH config (with <c>~</c>, <c>%d</c> and <c>%u</c> expanded).</summary>
    internal static IEnumerable<string> SshConfigIdentityFiles(string home, string configPath)
    {
        string[] lines;
        try
        {
            if (!File.Exists(configPath) || new FileInfo(configPath).Length > 1024 * 1024)
                return [];
            lines = File.ReadAllLines(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var paths = new List<string>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (!line.StartsWith("IdentityFile", StringComparison.OrdinalIgnoreCase))
                continue;
            string value = line["IdentityFile".Length..].TrimStart();
            if (value.StartsWith('='))
                value = value[1..];
            value = value.Trim().Trim('"');
            if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                continue;
            value = value.Replace("%d", home).Replace("%u", Environment.UserName).Replace("%%", "%");
            if (value.Contains('%')) // host-specific tokens (%h, %r, ...) cannot be resolved without a host
                continue;
            paths.Add(ExpandHome(value, home));
        }
        return paths;
    }

    private static string ExpandHome(string path, string home)
    {
        if (path == "~")
            return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return System.IO.Path.Combine(home, path[2..]);
        return System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(home, path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsSessionKeys()
    {
        var paths = new List<string>();
        void Read(string sessionsKey, bool urlEncoded)
        {
            try
            {
                using Microsoft.Win32.RegistryKey? sessions = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sessionsKey);
                if (sessions is null)
                    return;
                foreach (string name in sessions.GetSubKeyNames().Take(500))
                {
                    using Microsoft.Win32.RegistryKey? session = sessions.OpenSubKey(name);
                    if (session?.GetValue("PublicKeyFile") is string { Length: > 0 } value)
                        paths.Add(urlEncoded ? Uri.UnescapeDataString(value) : value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        Read(@"Software\SimonTatham\PuTTY\Sessions", urlEncoded: false);
        Read(@"Software\Martin Prikryl\WinSCP 2\Sessions", urlEncoded: true);
        return paths;
    }

    /// <summary>Downloads, Desktop and Documents, honoring localized XDG folder names on Linux.</summary>
    internal static IEnumerable<string> UserFolders(string home)
    {
        var folders = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            folders.Add(System.IO.Path.Combine(home, "Downloads"));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }
        else
        {
            Dictionary<string, string> xdg = XdgUserDirs(home);
            foreach ((string name, string fallback) in new[] { ("XDG_DOWNLOAD_DIR", "Downloads"), ("XDG_DESKTOP_DIR", "Desktop"), ("XDG_DOCUMENTS_DIR", "Documents") })
                folders.Add(xdg.TryGetValue(name, out string? dir) ? dir : System.IO.Path.Combine(home, fallback));
        }
        // XDG maps an unset folder to the home folder itself: never scan that.
        string homeFull = System.IO.Path.GetFullPath(home).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return folders.Where(f => f.Length > 0)
            .Select(f => System.IO.Path.GetFullPath(f).TrimEnd(System.IO.Path.DirectorySeparatorChar))
            .Where(f => f != homeFull)
            .Distinct();
    }

    private static Dictionary<string, string> XdgUserDirs(string home)
    {
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : System.IO.Path.Combine(home, ".config");
        try
        {
            string file = System.IO.Path.Combine(config, "user-dirs.dirs");
            if (!File.Exists(file))
                return dirs;
            foreach (string raw in File.ReadLines(file))
            {
                // XDG_DOWNLOAD_DIR="$HOME/Descărcări"
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith('#') || eq <= 0)
                    continue;
                string value = line[(eq + 1)..].Trim().Trim('"').Replace("$HOME", home);
                if (value.Length > 0)
                    dirs[line[..eq]] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return dirs;
    }

    /// <summary>Bounds-checked reader for SSH wire encoding.</summary>
    private sealed class SshReader(byte[] data, int position)
    {
        private int _position = position;

        public uint? UInt32()
        {
            if (_position + 4 > data.Length)
                return null;
            uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public byte[]? Bytes()
        {
            if (UInt32() is not { } length || length > data.Length - _position)
                return null;
            byte[] bytes = data.AsSpan(_position, (int)length).ToArray();
            _position += (int)length;
            return bytes;
        }

        public string? Text() => Bytes() is { } bytes ? Encoding.ASCII.GetString(bytes) : null;
    }
}
