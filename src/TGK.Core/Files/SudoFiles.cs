using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Agents;
using TGK.Core.Ssh;

namespace TGK.Core.Files;

/// <summary>
/// Reads and writes files as root through <c>sudo</c>, for the text editor: configuration files and logs the user can't
/// open or save over SFTP. Runs commands over a command connection to the host. A passwordless sudo is used as it is
/// (never sent a password it could read); otherwise the password goes to sudo on its input, asked through
/// <c>askPassword</c> (with the reason when sudo refused the last one) and kept by this object once sudo took it.
/// </summary>
/// <remarks>
/// A save first writes the content to a private temporary file as the user, then root copies it over the file: the
/// file keeps its owner, permissions and identity (like <c>sudo tee</c>), and the password is the only thing sudo
/// reads from its input.
/// </remarks>
public sealed class SudoFiles
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly RemoteConnection _connection;
    private readonly Func<string?, CancellationToken, Task<string?>> _askPassword;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _passwordless;
    private string? _password;

    /// <param name="askPassword">
    /// Asks for the sudo password (the argument: why the last one failed, or null); null when the user declines.
    /// </param>
    public SudoFiles(RemoteConnection connection, Func<string?, CancellationToken, Task<string?>> askPassword)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _askPassword = askPassword ?? throw new ArgumentNullException(nameof(askPassword));
    }

    public RemoteConnection Connection => _connection;

    /// <summary>The size of a file, read as root.</summary>
    public async Task<long> SizeAsync(string path, CancellationToken ct)
    {
        CommandResult result = await RunAsync("wc -c < \"$1\"", [path], 1024, keepBytes: false, ct).ConfigureAwait(false);
        return long.TryParse(result.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size)
            ? size
            : throw new FileOperationException($"Could not read the size of {path} as root.");
    }

    /// <summary>At most <paramref name="count"/> bytes of a file from <paramref name="offset"/> on, read as root.</summary>
    public async Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct)
    {
        // Opened by the shell: a file that can't be opened fails the command (a pipeline's status would be head's).
        string script = $"{{ tail -c +{(offset + 1).ToString(CultureInfo.InvariantCulture)} | head -c {count.ToString(CultureInfo.InvariantCulture)}; }} < \"$1\"";
        CommandResult result = await RunAsync(script, [path], count + 1, keepBytes: true, ct).ConfigureAwait(false);
        byte[] bytes = result.StdoutBytes ?? [];
        return bytes.Length > count ? bytes[..count] : bytes;
    }

    /// <summary>Replaces the content of a file as root, keeping its owner and permissions (a link is followed).</summary>
    public async Task WriteAsync(string path, byte[] content, CancellationToken ct)
    {
        // The content goes to a private file of the user's first: sudo's own input carries only the password.
        CommandResult staged = await _connection.RunAsync(
            "umask 077; t=$(mktemp \"${TMPDIR:-/tmp}/tgk-edit.XXXXXX\") || exit 1; cat > \"$t\" || { rm -f -- \"$t\"; exit 1; }; printf '%s' \"$t\"",
            Timeout, input: content, ct: ct).ConfigureAwait(false);
        string temp = staged.Stdout.Trim();
        if (staged.ExitCode != 0 || !temp.StartsWith('/'))
            throw new FileOperationException($"Could not prepare the save on the host: {FirstLine(staged.Stderr, "mktemp failed")}");
        try
        {
            await RunAsync("cat -- \"$1\" > \"$2\"", [temp, path], 4096, keepBytes: false, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _connection.RunAsync($"rm -f -- {RemotePath.Quote(temp)}", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SshSessionException or InvalidOperationException or ObjectDisposedException)
            {
                CoreLog.Warn($"Could not remove {temp}: {ex.Message}");
            }
        }
    }

    // Runs `sh -c script sh args…` as root and returns its result; a failure of the script throws with its message.
    private async Task<CommandResult> RunAsync(string script, string[] args, int maxOutput, bool keepBytes, CancellationToken ct)
    {
        var command = new StringBuilder("sh -c ").Append(RemotePath.Quote(script)).Append(" sh");
        foreach (string arg in args)
            command.Append(' ').Append(RemotePath.Quote(arg));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_passwordless is null)
            {
                CommandResult probe = await _connection.RunAsync("sudo -n true", TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
                if (probe.ExitCode != 0 && (probe.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase) || probe.ExitCode == 127))
                    throw new FileOperationException("This host has no sudo.");
                _passwordless = probe.ExitCode == 0;
            }
            if (_passwordless == true)
                return Checked(await _connection.RunAsync($"sudo -n -- {command} </dev/null", Timeout, maxOutput, keepBytes: keepBytes, ct: ct).ConfigureAwait(false));

            string? error = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string password = _password ?? await _askPassword(error, ct).ConfigureAwait(false) ?? throw new OperationCanceledException();
                byte[] input = Encoding.UTF8.GetBytes(password + "\n");
                CommandResult result = await _connection.RunAsync($"sudo -S -k -p '' -- {command}", Timeout, maxOutput, input, keepBytes, ct).ConfigureAwait(false);
                if (IsWrongPassword(result))
                {
                    _password = null;
                    error = "sudo did not accept the password.";
                    continue;
                }
                CommandResult checkedResult = Checked(result);
                _password = password;
                return checkedResult;
            }
            throw new FileOperationException("sudo did not accept the password.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsWrongPassword(CommandResult result) => result.ExitCode != 0
        && (result.Stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) || result.Stderr.Contains("Sorry, try again", StringComparison.Ordinal));

    private static CommandResult Checked(CommandResult result)
    {
        if (result.ExitCode == 0)
            return result;
        string error = FirstLine(result.Stderr, $"exit code {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? result.ExitSignal ?? "?"}");
        if (error.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) || error.Contains("not allowed to execute", StringComparison.OrdinalIgnoreCase))
            throw new FileOperationException($"This user may not use sudo here: {error}");
        if (error.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase) || error.Contains("no tty present", StringComparison.OrdinalIgnoreCase))
            throw new FileOperationException($"sudo on this host only works in a terminal: {error}");
        throw new FileOperationException($"As root: {error}");
    }

    private static string FirstLine(string text, string fallback)
    {
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("[sudo]", StringComparison.Ordinal))
                return line;
        }
        return fallback;
    }
}
