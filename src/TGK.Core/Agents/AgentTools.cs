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

    private const string HostProperty = """
        "host": { "type": "string", "description": "A saved host: its name in TGK (as list_hosts shows it) or its id." }
        """;

    public static readonly IReadOnlyList<AgentToolDefinition> All =
    [
        new(ListHosts, "List hosts",
            "Lists the remote hosts saved in TGK that agents may use, with their address, group and access mode " +
            "(read-only, ask = changes need the user's approval, full). Call this first to learn the host names.",
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
                "sudo": { "type": "boolean", "description": "Run it as root through sudo; TGK supplies the password. The user approves every such command. Do not put sudo in the command itself." }
              },
              "required": ["host", "command"],
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
              "required": ["host", "path"],
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
              "required": ["host"],
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
              "required": ["host", "path"],
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
              "required": ["host", "pattern"],
              "additionalProperties": false
            }
            """, true),

        new(Grep, "Search file contents",
            "Searches file contents on a host for a regular expression (ripgrep when the server has it, else grep -E), below a directory " +
            "or in one file, and returns matching lines as path:line:text. Skips binary files, .git and hidden directories such as .ssh.",
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
              "required": ["host", "pattern"],
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
              "required": ["host", "path", "old_string", "new_string"],
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
              "required": ["host", "path", "content"],
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
              "required": ["host", "local_path", "remote_path"],
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
              "required": ["host", "remote_path", "local_path"],
              "additionalProperties": false
            }
            """, false),
    ];
}
