# Plan: TGK as an MCP server for local agents (with SFTP file access)

Status: implemented in 0.3.0 (see [AGENTS.md](AGENTS.md) for how to use it). This document records the design and
the decisions behind it.

## Goal

A local agent (Claude Code, or any MCP client) can work on any remote saved in TGK: run commands, read, search and
edit files. It never sees passwords, keys or the vault. TGK stays the only thing that opens connections, decides what
is allowed and keeps a record of what was done.

## Architecture

```
Claude Code ──stdio──► tgk-mcp (small console bridge)
                           │  local Unix domain socket + token
                           ▼
                     TGK (the running app, vault unlocked)
                           │  pooled SSH + SFTP connections
                           ▼
                     the vault's remotes (jump hosts included)
```

- **The running TGK app hosts the MCP server.** It reuses the unlocked vault (no new password prompts), shows
  approvals as TGK dialogs, shows the agent's activity, and secrets never leave the process.
- **`tgk-mcp` is a byte pipe** between stdio and the app's socket. It is a separate console executable (the GUI
  executable is a `WinExe`, a poor stdio server on Windows) that starts instantly and loads no UI. It ships next to
  `TGK` in every package (archives, AppImage via `--mcp`, `TGK.app/Contents/MacOS`, the Windows installer); the
  archive names and layout of the self-update contract do not change.
- **Endpoint:** a Unix domain socket in the config directory (`agent.sock`; Windows 10 1803+ supports them too, so one
  implementation serves every platform), plus a random token in `agent-token` (mode 0600 on Linux/macOS; the
  per-user config directory on Windows). The bridge sends the token first; a wrong token closes the connection.
  If TGK is not running, or agents are off, the bridge answers the MCP handshake with a clear error.
- MCP is implemented with the official C# SDK, `ModelContextProtocol.Core` (low-level server API, explicit JSON
  schemas, no hosting/DI dependencies).

## Connection layer (Core)

- **`SshHop`** (internal): what `SshSession` used to do privately for one hop: load the key, build the
  authentication methods (key, password, keyboard-interactive with the password offered once, other questions to the
  prompt handler), the connection info (timeouts, legacy algorithms), and the local forwarding to the next hop.
  `SshSession` uses it unchanged in behavior.
- **`RemoteConnection`**: for one host (an `SshConnectRequest`, jump chain included) an `SshClient` for commands and,
  on first use, an `SftpClient` through the same route (SSH.NET cannot share a session between the two, so these are
  two TCP connections; the SFTP one must present the host key already trusted, no second prompt).
  - Commands: exit code, stdout/stderr apart, a timeout (30 s default, 10 min max), an output cap (100 KB per stream,
    the rest cut and marked).
- **`ConnectionPool`**: one connection per host, reused between calls, closed after 10 idle minutes, on lock or sign
  out and when agents are turned off; a dropped connection is reconnected once.
- Prompts while connecting (unknown or changed host key, one-time codes) appear as the usual TGK dialogs. A host key
  is never trusted automatically.

## Tools

All take `host` (a saved host's name or id). Agents only see hosts their policy allows.

| Tool | What it does | How |
|---|---|---|
| `list_hosts` | Hosts open to agents: name, address, group, access mode. No secrets. | vault |
| `run_command` | `host, command, cwd?, timeout_seconds?` → exit code, stdout, stderr | exec |
| `read_file` | `host, path, offset?, limit?` → numbered lines (`cat -n` style), at most 2000 lines per call; binary files refused | SFTP |
| `list_dir` | `host, path` → name, type, size, modified, permissions | SFTP |
| `stat` | metadata of a path (symlink target included) | SFTP |
| `glob` | `host, pattern, path?` → matching paths, capped | `find` on the server |
| `grep` | `host, pattern, path?, glob?, ignore_case?, context?` → matches with line numbers | `rg`, else `grep -rn` on the server |
| `edit_file` | `host, path, old_string, new_string, replace_all?`: exact replacement; error if not found or not unique | SFTP read → check → atomic write |
| `write_file` | `host, path, content`: new file or full rewrite | atomic write |
| `upload` / `download` | between this computer and the remote | SFTP |

- **Safe writes:** a file is edited or overwritten only after it was read in the same MCP session and has not
  changed since (size, modification time and content hash), else "the file changed, read it again". Writes go to a
  temporary file in the same directory (permissions copied), then replace the original by rename, so a file is never
  left half written. Line endings and a UTF-8 BOM are kept.
- **Servers without SFTP:** detected once per host; reading and writing then fall back to `cat`/`base64` over exec.

## Policy

Stored in the vault (synced), inherited like the other connection settings: host → group → defaults.

- **Agent access** per host/group/default: `Off` (the default: the host is invisible to agents), `Read only` (read
  tools and read-only commands), `Ask` (reads are free, writes and other commands need approval), `Full` (no approval,
  except for protected paths).
- **Allowed commands**: command prefixes that run without approval (`git status`, `systemctl status`); in `Read only`
  they are the only commands besides the built-in read-only ones. Compound commands (`;`, `&&`, `|`, `$(...)`,
  redirections) pass only if every part passes; anything unclear needs approval.
- **Protected paths** (built-in: `~/.ssh/**`, `/etc/shadow`, `/etc/sudoers*`, private keys, plus the user's list):
  reading or writing them always needs approval, also in `Full`; in `Read only` they are refused.
- **Approval dialog**: host, the command or a diff of the file, which client asks; *Allow once* / *Allow for this
  session* / *Deny*; no answer in 2 minutes is a denial.
- **Audit log** (local, rotated): time, client, host, tool, command or path, result, duration, who approved. File
  contents are never logged.
- **Limits**: 5 MB per read or write, 100 KB of command output per stream, at most 4 concurrent calls per client.

## UI

- **Settings → Agents**: "Allow agents (MCP)" (off by default, per device), status, "Copy setup command" for Claude
  Code (`claude mcp add tgk -- <path>/tgk-mcp`) and the `.mcp.json` snippet, connected clients, "Disconnect all".
- **Agent access** in the host editor, group settings and connection defaults, with the other inherited options.
- **Status bar**: an agent chip while clients are connected; it opens the activity list (recent calls, denials).

## Phases

1. Connection refactor (`SshHop`), `RemoteConnection`, `ConnectionPool`, commands, read-only file tools, E2E tests
   against an asyncssh test server with SFTP.
2. Writes (`edit_file`, `write_file`), policy model and evaluation, approvals, audit log.
3. MCP server in the app, `tgk-mcp`, settings, status chip and activity, packaging on every platform, docs,
   changelog; a real Claude Code session against the test server.
4. Extensions: `upload`/`download`; later: a headless mode for servers/CI, mirroring agent commands into a visible
   tab, an AI chat pane, sudo with approval.

## Testing

- Unit: policy (inheritance, compound commands, path globs, protected paths), `edit_file` (unique, missing,
  `replace_all`, CRLF, BOM), output caps, policy serialization in the vault.
- E2E (asyncssh with SFTP and exec, `scripts/e2e-sshd.py`): read, list, glob, grep, atomic edit and write, a file
  changed between read and write, the fallback without SFTP, jump hosts.
- MCP: an SDK client in-process against the server over a socket pair; a wrong token is refused.

## Risks

- Two connections per host: with one-time codes the user is asked twice; SFTP opens lazily, and the exec fallback
  covers servers where it cannot.
- Any process of the same user can read the token: the same trust level as an SSH agent socket. Hence off by default
  and documented.
- Prompt injection: remote file contents can try to steer the agent. That is why policy and approvals live in TGK,
  not in the agent.
