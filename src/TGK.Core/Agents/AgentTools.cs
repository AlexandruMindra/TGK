using System.Collections.Generic;
using System.Text.Json;

namespace TGK.Core.Agents;

/// <summary>One tool as agents see it: name, description and the JSON schema of its arguments.</summary>
/// <param name="ReadOnly">Never changes anything on a host (MCP's readOnlyHint).</param>
public sealed record AgentToolDefinition(string Name, string Title, string Description, string InputSchema, bool ReadOnly)
{
    public JsonElement Schema => JsonDocument.Parse(InputSchema).RootElement.Clone();
}

/// <summary>The tools TGK offers agents. See docs/AGENTS.md.</summary>
public static class AgentTools
{
    public const string ListHosts = "list_hosts";
    public const string RunCommand = "run_command";
    public const string ReadFile = "read_file";
    public const string ListDir = "list_dir";
    public const string Stat = "stat";
    public const string Glob = "glob";
    public const string Grep = "grep";
    public const string EditFile = "edit_file";
    public const string WriteFile = "write_file";
    public const string Upload = "upload";
    public const string Download = "download";
    public const string ListSessions = "list_sessions";
    public const string ReadTerminal = "read_terminal";
    public const string SendToTerminal = "send_to_terminal";

    /// <summary>Most seconds <c>send_to_terminal</c> waits for output.</summary>
    public const int MaxTerminalWaitSeconds = 60;

    /// <summary>Most history lines <c>read_terminal</c> returns.</summary>
    public const int MaxScrollbackLines = 2000;

    private const string HostProperty = """
        "host": { "type": "string", "description": "A saved host: its name in TGK (as list_hosts shows it) or its id. Give host or session." },
                "session": { "type": "string", "description": "Instead of host: an SSH terminal tab open in TGK (its id from list_sessions, e.g. t3), whose connection is used (no new sign-in)." }
        """;

    public static readonly IReadOnlyList<AgentToolDefinition> All =
    [
        new(ListHosts, "List hosts",
            "Lists the remote hosts saved in TGK that agents may use, with their address, group and access mode " +
            "(read-only, ask = changes need the user's approval, full). Call this first to learn the host names; list_sessions shows " +
            "the SSH terminal tabs open in TGK, which can be used too.",
            """{ "type": "object", "properties": {}, "additionalProperties": false }""", true),

        new(RunCommand, "Run a command",
            "Runs a shell command on a host (non-interactive, no terminal; the user's login shell environment may not be loaded) " +
            "and returns its exit code, stdout and stderr (each cut at 100 KB). Interactive programs (vim, top, less without a pipe) " +
            "do not work. Depending on the host's access mode the user may have to approve the command first.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "command": { "type": "string", "description": "The command line, run by the user's shell (sh -c style)." },
                "cwd": { "type": "string", "description": "Directory to run in (absolute, ~/…, or relative to the home directory). Default: home." },
                "timeout_seconds": { "type": "integer", "minimum": 1, "maximum": 600, "description": "Stop the command after this long. Default 30." },
                "sudo": { "type": "boolean", "description": "Run it as root through sudo; TGK supplies the password. Unless the host has full access, the user approves every such command. Do not put sudo in the command itself." }
              },
              "required": ["command"],
              "additionalProperties": false
            }
            """, false),

        new(ReadFile, "Read a file",
            "Reads a text file on a host over SFTP and returns its lines numbered like `cat -n` (at most 2000 lines per call; " +
            "use offset and limit for more). Paths may be absolute, start with ~/, or be relative to the home directory. " +
            "A file must be read before edit_file or write_file may change it.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "path": { "type": "string", "description": "The file." },
                "offset": { "type": "integer", "minimum": 1, "description": "First line to return (1-based). Default 1." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 2000, "description": "Most lines to return. Default 2000." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """, true),

        new(ListDir, "List a directory",
            "Lists a directory on a host: type, permissions, size, modification time and name of each entry (at most 1000).",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "path": { "type": "string", "description": "The directory. Default: the home directory." }
              },
              "required": [],
              "additionalProperties": false
            }
            """, true),

        new(Stat, "File information",
            "Tells whether a path exists on a host and what it is: type (file, directory, symlink), size, permissions, owner id, modification time.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "path": { "type": "string", "description": "The path." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """, true),

        new(Glob, "Find files",
            "Finds files and directories on a host whose path matches a glob pattern (`*` within a name, `**` across directories, " +
            "e.g. `**/*.conf` or `src/**/test_*.py`), below a directory. Skips .git and node_modules. At most 500 results.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "pattern": { "type": "string", "description": "The glob, relative to path." },
                "path": { "type": "string", "description": "The directory to search. Default: the home directory." }
              },
              "required": ["pattern"],
              "additionalProperties": false
            }
            """, true),

        new(Grep, "Search file contents",
            "Searches file contents on a host for a regular expression (ripgrep when the server has it, else grep -E), below a directory " +
            "or in one file, and returns matching lines as path:line:text. Skips binary files, .git, node_modules and protected paths " +
            "(keys and credentials such as .ssh, ssh, *.pem, *.key, .env, shadow).",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "pattern": { "type": "string", "description": "The regular expression." },
                "path": { "type": "string", "description": "A directory or file. Default: the home directory." },
                "glob": { "type": "string", "description": "Only search files whose name matches this glob, e.g. *.py." },
                "ignore_case": { "type": "boolean", "description": "Match case-insensitively." },
                "context": { "type": "integer", "minimum": 0, "maximum": 10, "description": "Lines of context around each match." },
                "max_results": { "type": "integer", "minimum": 1, "maximum": 1000, "description": "Most output lines. Default 200." }
              },
              "required": ["pattern"],
              "additionalProperties": false
            }
            """, true),

        new(EditFile, "Edit a file",
            "Replaces exact text in a file on a host: old_string must occur exactly once (or pass replace_all). Read the file with " +
            "read_file first and copy old_string exactly, indentation included, without the line-number prefix. The file is " +
            "written atomically, keeping its permissions, encoding and line endings. Fails if the file changed since it was read. " +
            "Depending on the host's access mode the user may have to approve the change.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "path": { "type": "string", "description": "The file." },
                "old_string": { "type": "string", "description": "The text to replace." },
                "new_string": { "type": "string", "description": "The replacement." },
                "replace_all": { "type": "boolean", "description": "Replace every occurrence. Default false." }
              },
              "required": ["path", "old_string", "new_string"],
              "additionalProperties": false
            }
            """, false),

        new(WriteFile, "Write a file",
            "Creates a file on a host, or replaces an existing one entirely (which must have been read with read_file first). " +
            "Missing parent directories are created. Written atomically; an existing file keeps its permissions. Prefer edit_file " +
            "for changes to existing files. Depending on the host's access mode the user may have to approve it.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "path": { "type": "string", "description": "The file." },
                "content": { "type": "string", "description": "The complete new content." }
              },
              "required": ["path", "content"],
              "additionalProperties": false
            }
            """, false),

        new(Upload, "Upload a file",
            "Copies a file from this computer to a host (any content, at most 50 MB), replacing the remote file atomically if it exists.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "local_path": { "type": "string", "description": "Absolute path of the file on this computer." },
                "remote_path": { "type": "string", "description": "Where to put it on the host." }
              },
              "required": ["local_path", "remote_path"],
              "additionalProperties": false
            }
            """, false),

        new(Download, "Download a file",
            "Copies a file from a host to this computer (any content, at most 50 MB). An existing local file is only replaced with overwrite.",
            $$"""
            {
              "type": "object",
              "properties": {
                {{HostProperty}},
                "remote_path": { "type": "string", "description": "The file on the host." },
                "local_path": { "type": "string", "description": "Absolute path to save it to on this computer." },
                "overwrite": { "type": "boolean", "description": "Replace an existing local file. Default false." }
              },
              "required": ["remote_path", "local_path"],
              "additionalProperties": false
            }
            """, false),

        new(ListSessions, "List open sessions",
            "Lists the SSH terminal tabs open in TGK that agents may use: session id, host, address, state and access mode. Pass the id " +
            "as session (instead of host) to the other tools: commands and files then go over that tab's own connection, with no new " +
            "sign-in (also for hosts that ask for a one-time code, and for tabs opened with Quick Connect, which are not saved hosts). " +
            "read_terminal and send_to_terminal work with the tab's live terminal: what the user sees, and the shell they are in.",
            """{ "type": "object", "properties": {}, "additionalProperties": false }""", true),

        new(ReadTerminal, "Read a terminal",
            "Returns what an SSH terminal tab shows now: the visible screen, the cursor position, and optionally the lines of history " +
            "above it. Use it to see the state of the shell or program the user has open in that tab.",
            $$"""
            {
              "type": "object",
              "properties": {
                "session": { "type": "string", "description": "The terminal tab (its id from list_sessions, e.g. t3)." },
                "scrollback": { "type": "integer", "minimum": 0, "maximum": {{MaxScrollbackLines}}, "description": "Also return this many lines of history above the screen. Default 0." }
              },
              "required": ["session"],
              "additionalProperties": false
            }
            """, true),

        new(SendToTerminal, "Type into a terminal",
            "Types into an SSH terminal tab as the user would, then waits until the output settles (or wait_seconds pass) and returns " +
            "the screen. text is typed as is (no Enter at the end: add \"enter\" to keys; a line break in text also acts as Enter). " +
            "keys are pressed after it, in order: enter, tab, escape, backspace, delete, insert, up, down, left, right, home, end, " +
            "pageup, pagedown, f1-f12, a single character, or combinations such as ctrl+c, ctrl+d, ctrl+r, alt+b, shift+tab. " +
            "For ordinary commands prefer run_command with this session: it returns the complete output and the exit code. Use the " +
            "terminal when the live shell's state matters (another user after su or sudo -i, inside a container, a database client, " +
            "a program waiting for input, a device's console). The user sees everything typed. Depending on the host's access mode " +
            "the user may have to approve each input; read-only access cannot type.",
            $$"""
            {
              "type": "object",
              "properties": {
                "session": { "type": "string", "description": "The terminal tab (its id from list_sessions, e.g. t3)." },
                "text": { "type": "string", "description": "Text to type." },
                "keys": { "type": "array", "items": { "type": "string" }, "description": "Keys to press after the text, e.g. [\"enter\"] or [\"ctrl+c\"]." },
                "wait_seconds": { "type": "number", "minimum": 0, "maximum": {{MaxTerminalWaitSeconds}}, "description": "Longest wait for the output to settle. Default 3; raise it for slow commands." }
              },
              "required": ["session"],
              "additionalProperties": false
            }
            """, false),
    ];
}
