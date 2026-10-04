using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Agents;

/// <summary>
/// What <c>tgk-mcp</c> does: connects an agent's stdio to the running TGK (<see cref="AgentEndpoint"/>) and passes the
/// MCP messages through unchanged in both directions until either side closes.
/// </summary>
public static class AgentBridge
{
    public const int ExitOk = 0;
    public const int ExitNotRunning = 2;
    public const int ExitRefused = 3;

    /// <summary>Runs the bridge; returns the process exit code. Problems are explained on <paramref name="log"/> (stderr).</summary>
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, string? infoPath = null, CancellationToken ct = default)
    {
        AgentEndpointInfo? info = AgentEndpoint.ReadInfo(infoPath);
        if (info is null)
        {
            await log.WriteLineAsync("tgk-mcp: TGK is not serving agents. Start TGK, turn on Settings → Agents → \"Allow agents (MCP)\", " +
                "then reconnect (in Claude Code: /mcp).").ConfigureAwait(false);
            return ExitNotRunning;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(info.Socket), ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            await log.WriteLineAsync("tgk-mcp: TGK is not running (or stopped serving agents). Start TGK with \"Allow agents (MCP)\" on, " +
                "then reconnect (in Claude Code: /mcp).").ConfigureAwait(false);
            return ExitNotRunning;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"{AgentEndpoint.Hello} {info.Token}\n"), ct).ConfigureAwait(false);
        string? answer = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (answer != "OK")
        {
            await log.WriteLineAsync($"tgk-mcp: TGK refused the connection: {(answer?.StartsWith("ERR ", StringComparison.Ordinal) == true ? answer[4..] : "no answer")}").ConfigureAwait(false);
            return ExitRefused;
        }

        // Agent → TGK until the agent closes its end; TGK → agent until TGK closes (it does once the agent's side ends).
        Task up = Task.Run(async () =>
        {
            try
            {
                await input.CopyToAsync(stream, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
            try
            {
                socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);
        Task down = Task.Run(async () =>
        {
            try
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        // TGK closing (it quit, or agents were turned off) ends the bridge even while the agent keeps stdin open.
        await down.ConfigureAwait(false);
        return ExitOk;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        byte[] one = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (line.Length < 1024 && await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) > 0)
            {
                if (one[0] == '\n')
                    return line.ToString();
                line.Append((char)one[0]);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
        return line.Length > 0 ? line.ToString() : null;
    }
}
