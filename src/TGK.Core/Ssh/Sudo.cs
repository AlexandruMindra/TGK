using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Agents;

namespace TGK.Core.Ssh;

/// <summary>Why <see cref="Sudo.RunAsync"/> could not run a command as root.</summary>
public enum SudoFailure
{
    /// <summary>The server has no sudo.</summary>
    Missing,

    /// <summary>No password was given when one was asked for.</summary>
    Declined,

    /// <summary>sudo did not accept any of the passwords given.</summary>
    Refused,
}

/// <summary>A command could not be run through sudo; <see cref="Exception.Message"/> is user-facing.</summary>
public sealed class SudoException(SudoFailure failure, string message) : Exception(message)
{
    public SudoFailure Failure { get; } = failure;
}

/// <summary>Commands as root over a <see cref="RemoteConnection"/> (no terminal: sudo reads its password from stdin).</summary>
public static class Sudo
{
    /// <summary>How many passwords are tried before giving up.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs <paramref name="line"/> (a shell command line) through sudo. A passwordless sudo runs it directly (never
    /// sending a password it could read); otherwise the password goes to sudo on stdin, with cached credentials ignored
    /// so that sudo always consumes it, and stdin ends after it. A wrong password is asked for again
    /// (<see cref="MaxAttempts"/> in all).
    /// </summary>
    /// <param name="password">
    /// Gives the password to try: called with null first, then with why the previous one was refused. Returns null
    /// when the user gave none.
    /// </param>
    /// <exception cref="SudoException">No sudo, no password given, or every password refused.</exception>
    /// <exception cref="SshSessionException">The connection is gone.</exception>
    public static async Task<CommandResult> RunAsync(RemoteConnection connection, string line, TimeSpan timeout,
        Func<string?, CancellationToken, Task<string?>> password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(line);
        ArgumentNullException.ThrowIfNull(password);
        string wrapped = $"sh -c {RemotePath.Quote(line)}";
        CommandResult probe = await connection.RunAsync("sudo -n true", TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        if (probe.ExitCode == 0)
            return await connection.RunAsync($"sudo -n -- {wrapped} </dev/null", timeout, ct: ct).ConfigureAwait(false);
        SshConnectRequest? request = connection.Request;
        if (probe.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new SudoException(SudoFailure.Missing, $"{request?.Name ?? request?.Host ?? "The server"} has no sudo.");

        string? error = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string given = await password(error, ct).ConfigureAwait(false)
                ?? throw new SudoException(SudoFailure.Declined, "No sudo password was given.");
            byte[] input = Encoding.UTF8.GetBytes(given + "\n");
            CommandResult result = await connection.RunAsync($"sudo -S -k -p '' -- {wrapped}", timeout, input: input, ct: ct).ConfigureAwait(false);
            bool wrong = result.ExitCode != 0 && (result.Stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
                || result.Stderr.Contains("Sorry, try again", StringComparison.Ordinal));
            if (!wrong)
                return result;
            error = $"sudo did not accept the password for {request?.Username}@{request?.Host}.";
        }
        throw new SudoException(SudoFailure.Refused, "sudo did not accept the password.");
    }
}
