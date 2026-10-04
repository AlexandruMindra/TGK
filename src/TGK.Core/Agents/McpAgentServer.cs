using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TGK.Core.Agents;

/// <summary>Serves <see cref="AgentToolbox"/> as an MCP server over a pair of streams (one agent session).</summary>
public static class McpAgentServer
{
    public const string ServerName = "tgk";

    private const string Instructions =
        "TGK gives access to the remote servers (SSH hosts) saved in the user's TGK vault. Call list_hosts first to learn which hosts " +
        "you may use and how (read-only, ask, full). Use read_file, list_dir, glob and grep to look around, edit_file or write_file to " +
        "change files (read a file before changing it), and run_command for anything else. Passwords and keys stay in TGK. Some " +
        "operations wait for the user to approve them in TGK; a refusal is final for that request, so do not retry it unchanged.";

    /// <summary>Runs one MCP session until the client disconnects or <paramref name="ct"/> is cancelled.</summary>
    /// <param name="onClientKnown">Called once the client said who it is (after initialize).</param>
    public static async Task RunAsync(Stream input, Stream output, AgentToolbox toolbox, AgentSession session, Action? onClientKnown = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolbox);
        ArgumentNullException.ThrowIfNull(session);
        List<Tool> tools = AgentTools.All.Select(t => new Tool
        {
            Name = t.Name,
            Title = t.Title,
            Description = t.Description,
            InputSchema = t.Schema,
            Annotations = new ToolAnnotations
            {
                Title = t.Title,
                ReadOnlyHint = t.ReadOnly,
                DestructiveHint = !t.ReadOnly,
                OpenWorldHint = true,
            },
        }).ToList();
        bool known = false;

        void Identify(McpServer server)
        {
            if (known || server.ClientInfo is not { } info)
                return;
            known = true;
            session.Client = string.IsNullOrWhiteSpace(info.Version) ? info.Name : $"{info.Name} {info.Version}";
            onClientKnown?.Invoke();
        }

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Title = "TGK", Version = typeof(McpAgentServer).Assembly.GetName().Version?.ToString(3) ?? "0" },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (context, _) =>
                {
                    Identify(context.Server);
                    return ValueTask.FromResult(new ListToolsResult { Tools = tools });
                },
                CallToolHandler = async (context, token) =>
                {
                    Identify(context.Server);
                    CallToolRequestParams? request = context.Params;
                    JsonElement arguments = request?.Arguments is { } args
                        ? JsonSerializer.SerializeToElement(args)
                        : JsonSerializer.SerializeToElement(new Dictionary<string, object>());
                    ToolOutcome outcome = await toolbox.CallAsync(session, request?.Name ?? "", arguments, token).ConfigureAwait(false);
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = outcome.Text }],
                        IsError = outcome.IsError,
                    };
                },
            },
        };

        var transport = new StreamServerTransport(input, output, ServerName);
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(ct).ConfigureAwait(false);
    }
}
