using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Agents;
using TGK.Core.Ssh;

namespace TGK.Core.Files;

/// <summary>
/// How host A reaches host B for a direct copy: B's address as A sees it, B's credentials (from the vault or typed),
/// and B's public key as this computer trusted it.
/// </summary>
/// <param name="HostKey">B's public key (SSH wire format), as trusted by this computer's own connection to B.</param>
public sealed record DirectTarget(string Host, int Port, string Username, string? Password, string? PrivateKey, string? Passphrase, byte[] HostKey)
{
    /// <summary>The problem with these credentials for OpenSSH on host A, or null when they can be used.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.AsSpan().IndexOfAny(" \t\r\n'\"\\") >= 0)
            return "Enter the address host A uses to reach host B.";
        if (Port is < 1 or > 65535)
            return "Port must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(Username) || Username.AsSpan().IndexOfAny(" \t\r\n'\"\\@") >= 0)
            return "Host B has no usable username.";
        if (string.IsNullOrWhiteSpace(PrivateKey) && string.IsNullOrEmpty(Password))
            return "Host B has neither a password nor a key: a direct copy has nothing to sign in with.";
        if (HostKey.Length < 8)
            return "Host B's key is not known yet: open its files here once first.";
        try
        {
            _ = DirectCopy.KeyType(HostKey);
        }
        catch (ArgumentException)
        {
            return "Host B's host key is in a form ssh can't check: copy through this computer instead.";
        }
        return null;
    }
}

/// <summary>
/// A copy that host A makes itself, straight to host B, instead of through this computer: TGK runs a script on A (over
/// SSH) that streams the items with <c>tar</c> through <c>ssh</c> to B, where <c>tar</c> unpacks them. A needs
/// <c>ssh</c>, <c>tar</c> and <c>base64</c>; B needs <c>tar</c>; A must reach B.
/// </summary>
/// <remarks>
/// <para>
/// Security: B's key or password goes to A, into a folder only the account there (and root) can read, created for the
/// copy and removed when it ends (also when it fails or is cancelled). A checks B's host key against the key this
/// computer trusted for B, never accepting another one. Use it only with a host A you trust with B's credentials.
/// </para>
/// <para>Links are copied as links; permissions and times are kept by tar.</para>
/// </remarks>
public static class DirectCopy
{
    /// <summary>The alias B's key is checked under on A (so the address A uses does not matter).</summary>
    internal const string HostKeyAlias = "tgk-direct-target";

    /// <summary>A <c>known_hosts</c> line for B's key under <see cref="HostKeyAlias"/>.</summary>
    public static string KnownHostsLine(byte[] hostKey) => $"{HostKeyAlias} {KeyType(hostKey)} {Convert.ToBase64String(hostKey)}";

    /// <summary>The key type stored at the start of a public key blob (e.g. <c>ssh-ed25519</c>, <c>ssh-rsa</c>).</summary>
    public static string KeyType(byte[] hostKey)
    {
        if (hostKey.Length < 4)
            throw new ArgumentException("Not an SSH public key.", nameof(hostKey));
        int length = BinaryPrimitives.ReadInt32BigEndian(hostKey);
        if (length <= 0 || length > 64 || 4 + length > hostKey.Length)
            throw new ArgumentException("Not an SSH public key.", nameof(hostKey));
        string type = Encoding.ASCII.GetString(hostKey, 4, length);
        if (!type.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@'))
            throw new ArgumentException("Not an SSH public key.", nameof(hostKey));
        return type;
    }

    /// <summary>
    /// The script host A runs. Secrets never appear in it: they come on its input (see <see cref="Input"/>), so they
    /// are in no command line and no log.
    /// </summary>
    internal static string Script(string runId, string sourceDirectory, IReadOnlyList<string> names, DirectTarget target, string targetDirectory)
    {
        bool key = !string.IsNullOrWhiteSpace(target.PrivateKey);
        string remote = $"mkdir -p -- {RemotePath.Quote(targetDirectory)} && tar -xpf - -C {RemotePath.Quote(targetDirectory)}";
        string auth = key
            ? "-i \"$d/id\" -o IdentitiesOnly=yes -o PreferredAuthentications=publickey"
            : "-o PubkeyAuthentication=no -o PreferredAuthentications=keyboard-interactive,password";
        var sb = new StringBuilder();
        sb.Append("umask 077\n");
        sb.Append($"d=\"${{TMPDIR:-/tmp}}/{runId}\"\n");
        sb.Append("mkdir -- \"$d\" || { echo 'Could not create a private folder on this host.' >&2; exit 90; }\n");
        sb.Append("trap 'rm -rf -- \"$d\"' EXIT\n");
        sb.Append("trap 'exit 130' HUP INT TERM\n");
        sb.Append("for c in ssh tar base64; do command -v \"$c\" >/dev/null 2>&1 || { echo \"$c is not installed on this host.\" >&2; exit 91; }; done\n");
        sb.Append("IFS= read -r kh; IFS= read -r id; IFS= read -r pw\n");
        sb.Append("printf '%s' \"$kh\" | base64 -d > \"$d/known_hosts\" || exit 92\n");
        sb.Append("printf '%s' \"$id\" | base64 -d > \"$d/id\" || exit 92\n");
        sb.Append("printf '%s' \"$pw\" | base64 -d > \"$d/pw\" || exit 92\n");
        sb.Append("printf '#!/bin/sh\\ncat \"%s\"\\n' \"$d/pw\" > \"$d/ask\" && chmod 700 \"$d/ask\" || exit 92\n");
        sb.Append($"cd -- {RemotePath.Quote(sourceDirectory)} || exit 93\n");
        sb.Append("tar -cf - -- ").Append(string.Join(' ', names.Select(RemotePath.Quote)));
        sb.Append(" | SSH_ASKPASS=\"$d/ask\" SSH_ASKPASS_REQUIRE=force DISPLAY=\"${DISPLAY:-:0}\" ssh -T");
        sb.Append($" -p {target.Port} -l {RemotePath.Quote(target.Username.Trim())}");
        sb.Append($" -o StrictHostKeyChecking=yes -o UserKnownHostsFile=\"$d/known_hosts\" -o GlobalKnownHostsFile=/dev/null -o HostKeyAlias={HostKeyAlias}");
        sb.Append(" -o BatchMode=no -o NumberOfPasswordPrompts=1 -o ConnectTimeout=20 -o ServerAliveInterval=15 -o ServerAliveCountMax=4");
        sb.Append(' ').Append(auth);
        sb.Append(" -- ").Append(RemotePath.Quote(target.Host.Trim())).Append(' ').Append(RemotePath.Quote(remote)).Append('\n');
        sb.Append("exit $?\n");
        return sb.ToString();
    }

    /// <summary>
    /// The script's input: B's known_hosts line, key and password, each base64 on its own line. The key is
    /// <paramref name="openSshKey"/>: B's key converted to OpenSSH's format, unencrypted (see <see cref="KeyFor"/>).
    /// </summary>
    internal static byte[] Input(DirectTarget target, string? openSshKey)
    {
        bool key = !string.IsNullOrWhiteSpace(target.PrivateKey);
        string keyText = key ? openSshKey ?? throw new ArgumentNullException(nameof(openSshKey)) : "";
        string secret = key ? "" : target.Password ?? "";
        static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return Encoding.ASCII.GetBytes($"{B64(KnownHostsLine(target.HostKey) + "\n")}\n{B64(keyText)}\n{B64(secret)}\n");
    }

    /// <summary>
    /// B's key as <c>ssh</c> on host A can load it whatever its format in the vault (PuTTY, PEM, PKCS#8, with a
    /// passphrase): OpenSSH's own format, unencrypted (it lives only in the copy's private folder on A). Null without
    /// a key. Slow for keys with a passphrase (their KDF): not on the UI thread.
    /// </summary>
    /// <exception cref="FileOperationException">The key can't be read.</exception>
    internal static string? KeyFor(DirectTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.PrivateKey))
            return null;
        try
        {
            return KeyInspector.ToOpenSsh(target.PrivateKey, target.Passphrase);
        }
        catch (SshSessionException ex)
        {
            throw new FileOperationException($"Host B's key can't be used for a direct copy: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Adds a direct copy (or move) to <paramref name="queue"/>: names that exist at the target are put to
    /// <paramref name="resolveConflicts"/> first, then <paramref name="connectSource"/> opens a command connection to
    /// host A, which copies to B; a move then deletes the originals on A. Its progress is activity only.
    /// </summary>
    public static FileTransfer Enqueue(TransferQueue queue, SftpFileSystem from, IReadOnlyList<FileEntry> entries, SftpFileSystem to,
        string targetDirectory, bool move, DirectTarget target, Func<CancellationToken, Task<RemoteConnection>> connectSource,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts, string destination)
    {
        FileEntry[] roots = [.. entries];
        string sourceDirectory = from.Parent(roots[0].Path);
        if (roots.Any(e => from.Parent(e.Path) != sourceDirectory))
            throw new ArgumentException("A direct copy takes items of one folder.", nameof(entries));
        string title = roots.Length == 1 ? roots[0].Name : $"{roots.Length} items";
        return queue.Add(TransferKind.Copy, move, title, destination, async (t, ct) =>
        {
            var kept = roots.ToList();
            var conflicts = new List<string>();
            foreach (FileEntry root in roots)
            {
                if (await to.StatAsync(SftpFileSystem.Join(targetDirectory, root.Name), ct).ConfigureAwait(false) is not null)
                    conflicts.Add(root.Name);
            }
            if (conflicts.Count > 0 && resolveConflicts is not null)
            {
                switch (await resolveConflicts(conflicts).ConfigureAwait(false))
                {
                    case ConflictChoice.Cancel:
                        throw new OperationCanceledException();
                    case ConflictChoice.Skip:
                        kept.RemoveAll(r => conflicts.Contains(r.Name));
                        t.Skipped += conflicts.Count;
                        break;
                }
            }
            if (kept.Count == 0)
                return;
            t.SetTotals(0, kept.Count);
            t.SetState(TransferState.Running);
            t.StartFile($"{from.DisplayName} → {to.DisplayName}");
            using RemoteConnection exec = await connectSource(ct).ConfigureAwait(false);
            await RunAsync(exec, sourceDirectory, kept.Select(r => r.Name).ToList(), target, targetDirectory, ct).ConfigureAwait(false);
            foreach (FileEntry _ in kept)
                t.FileDone(0);
            if (move)
            {
                foreach (FileEntry root in kept)
                    await from.DeleteAsync(root, null, ct).ConfigureAwait(false);
            }
        }, source: from, sourcePath: sourceDirectory, target: to, targetPath: targetDirectory, indeterminate: true);
    }

    /// <summary>
    /// Runs the copy on host A through <paramref name="source"/> (a command connection to A) and waits for it.
    /// Cancelling stops it on A (and its ssh to B) and removes the credentials there.
    /// </summary>
    /// <exception cref="FileOperationException">The copy failed; the message says why (A's or B's error).</exception>
    public static async Task RunAsync(RemoteConnection source, string sourceDirectory, IReadOnlyList<string> names, DirectTarget target,
        string targetDirectory, CancellationToken ct)
    {
        if (target.Validate() is { } problem)
            throw new FileOperationException(problem);
        if (names.Count == 0)
            return;
        string? key = await Task.Run(() => KeyFor(target), ct).ConfigureAwait(false);
        string runId = $"tgk-direct-{Guid.NewGuid():N}";
        CommandResult result;
        try
        {
            result = await source.RunAsync(Script(runId, sourceDirectory, names, target, targetDirectory), TimeSpan.FromDays(1),
                input: Input(target, key), ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(source, runId).ConfigureAwait(false);
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            await StopAsync(source, runId).ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
        if (result.ExitCode == 0)
            return;
        throw new FileOperationException(Explain(result, target));
    }

    // Ends what a cancelled run left on A: its ssh (whose command line holds the run's folder) and the folder.
    private static async Task StopAsync(RemoteConnection source, string runId)
    {
        try
        {
            await source.RunAsync($"pkill -TERM -f {RemotePath.Quote(runId)} 2>/dev/null; sleep 1; rm -rf -- \"${{TMPDIR:-/tmp}}/{runId}\"",
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshSessionException or InvalidOperationException or ObjectDisposedException)
        {
            CoreLog.Warn($"Could not stop the direct copy {runId} on {source.Request?.Host}: {ex.Message}");
        }
    }

    internal static string Explain(CommandResult result, DirectTarget target)
    {
        string error = string.Join(" ", result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Warning: Permanently added", StringComparison.Ordinal)).Take(3));
        string b = $"{target.Host}:{target.Port}";
        if (result.TimedOut)
            return "The direct copy took longer than a day and was stopped.";
        if (error.Contains("Host key verification failed", StringComparison.Ordinal) || error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
            return $"Host A reached {b}, but the key there is not the one trusted for host B: nothing was copied. (Is {b} really host B, as seen from A?)";
        if (error.Contains("Load key", StringComparison.Ordinal) || error.Contains("invalid format", StringComparison.Ordinal)
            || error.Contains("libcrypto", StringComparison.Ordinal))
            return $"ssh on host A could not load host B's key (its OpenSSH may be too old for this key type): {error}";
        if (error.Contains("Permission denied", StringComparison.Ordinal) || error.Contains("Too many authentication failures", StringComparison.Ordinal))
            return $"Host B ({b}) refused the sign-in from host A: {error}";
        if (error.Contains("Could not resolve", StringComparison.Ordinal) || error.Contains("Connection refused", StringComparison.Ordinal)
            || error.Contains("timed out", StringComparison.OrdinalIgnoreCase) || error.Contains("No route", StringComparison.Ordinal))
            return $"Host A can't reach host B at {b}: {error} Copy through this computer instead.";
        return result.ExitCode switch
        {
            90 => "Host A could not create a private folder for the copy.",
            91 or 92 => $"Host A can't make a direct copy: {(error.Length > 0 ? error : "a required tool is missing")}",
            93 => "The source folder on host A is gone.",
            _ => error.Length > 0 ? $"The direct copy failed: {error}" : $"The direct copy failed (exit code {result.ExitCode?.ToString() ?? result.ExitSignal ?? "?"}).",
        };
    }
}
