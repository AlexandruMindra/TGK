using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Agents;

namespace TGK.Mcp;

/// <summary>
/// The MCP server command agents run (e.g. <c>claude mcp add tgk -- /path/to/tgk-mcp</c>): passes MCP over stdio
/// through to the running TGK, which does the work. stdout carries only MCP messages; problems go to stderr.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--version":
                    Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
                    return 0;
                case "--help" or "-h":
                    Console.WriteLine("""
                        tgk-mcp: lets an MCP client (Claude Code, …) use the remote hosts saved in TGK.

                        Run by the MCP client over stdio, e.g.:
                          claude mcp add tgk -- "<path to tgk-mcp>"
                        TGK must be running with Settings → Agents → "Allow agents (MCP)" on. What agents may do
                        on each host is set in TGK (edit a host or group → Agent access). See docs/AGENTS.md.
                        """);
                    return 0;
                default:
                    Console.Error.WriteLine($"tgk-mcp: unknown argument {args[0]} (try --help)");
                    return 64;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        await using Stream input = Console.OpenStandardInput();
        await using Stream output = Console.OpenStandardOutput();
        try
        {
            return await AgentBridge.RunAsync(input, output, Console.Error, ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}
