# Agents (MCP)

TGK can let a local AI agent work on your saved hosts: Claude Code, or any client of the
[Model Context Protocol](https://modelcontextprotocol.io/) (MCP). The agent can run commands and read, search and
edit files on the hosts you open to it. It never sees passwords, keys or your vault: TGK makes the connections,
applies your rules, asks you when they say so, and records everything the agent does.

```
Claude Code ──stdio──► tgk-mcp ──local socket──► TGK (running, signed in) ──SSH / SFTP──► your hosts
```

## Setting it up

1. In TGK, open **Settings → Agents** and turn on **Allow agents (MCP) on this device**. It is off by default, and it
   is a per-device setting.
2. Open the hosts the agent may use: **Defaults for all hosts** on the same tab, or the **Agents** tab of a group or a
   host (see [Access](#access)). Every host is closed to agents until you do this.
3. Register TGK with your agent once. **Copy command** gives the Claude Code command for your installation, e.g.

   ```bash
   claude mcp add --scope user tgk -- /path/to/TGK/tgk-mcp
   ```

   `tgk-mcp` ships next to TGK: `tgk-mcp.exe` in the Windows install folder, `TGK.app/Contents/MacOS/tgk-mcp` on
   macOS, and the AppImage itself with `--mcp` (`/path/TGK-x86_64.AppImage --mcp`). Other MCP clients take the
   **Copy JSON config** entry (`mcpServers` → `tgk`, a stdio server without arguments).
4. Start Claude Code and ask it to do something on a host, e.g. "check why nginx fails on web-01". It calls
   `list_hosts` first to see what it may use.

TGK must be running and signed in (or unlocked) while the agent works. If it is not, `tgk-mcp` exits with a message
saying so; start TGK and reconnect (in Claude Code: `/mcp`).

## Access

What agents may do is part of a host's connection settings and inherited the same way: host, then group, then the
defaults for all hosts. Each level can set:

| Setting | What it does |
|---|---|
| **Agent access** | **Off** (the built-in default): agents don't see the host. **Read only**: they read files and run read-only commands, and never change anything. **Ask**: reading is free; every change, and every command that is not read-only or allowed, waits for your approval. **Full**: everything without asking, except protected paths. |
| **Allowed commands** | Command prefixes that run without asking, e.g. `systemctl status *` or `make test` (`*` matches anything). In Read only, the only commands besides the built-in read-only ones. |
| **Protected paths** | Paths that always need your approval (and are refused in Read only), in addition to the built-in ones: `~/.ssh/**`, private keys (`*.pem`, `*.key`, `id_rsa`, …), `.env`, `/etc/shadow`, `/etc/sudoers*`, cloud and registry credentials. `**` matches any depth. |

Read-only commands are recognized conservatively: plain commands like `ls`, `cat`, `grep`, `find` (without
`-delete`/`-exec`), `df`, `ps`, `journalctl`, `systemctl status`, `git status`/`log`/`diff`, `docker ps`/`logs`,
chained with `|`, `&&` or `;`. Anything with output redirection to a file, command substitution (`$(…)`, backticks),
`VAR=value` prefixes or an unknown program counts as a change. A command that mentions a protected path needs approval
in every mode (and is refused in Read only). Use Full only on hosts you can rebuild.

## Approvals

When the access mode says so, TGK shows **Agent request** with the agent's name, the host, what it wants (the exact
command, or the diff of a file change, or the content of a new file) and why it needs approval:

- **Allow**: this once.
- **Allow for this session**: also the same command (or any change to the same file) again, until the agent
  disconnects.
- **Deny**, Escape, closing the dialog, or no answer within 2 minutes: the agent is told you did not approve it.

Approving takes a click: Enter does nothing in this dialog, so keys meant for a terminal can't approve anything. Prompts for an agent's connection (an unknown host key, a password the vault does not have, a one-time code)
appear as the usual dialogs, marked "for an agent". Passwords typed for agents are kept in memory until TGK closes or
you sign out.

## What the agent can do

| Tool | |
|---|---|
| `list_hosts` | The hosts open to agents, with address, group and access mode. |
| `run_command` | A shell command (no terminal; 30 s by default, at most 10 min): exit code, stdout and stderr, each cut at 100 KB. With `sudo`, it runs as root: you approve every such command, and TGK supplies the sudo password (the saved password, or one you type). |
| `read_file` | A text file with line numbers, up to 2000 lines per call. |
| `list_dir`, `stat` | Directory listings and file details. |
| `glob` | Files whose path matches a pattern (`**/*.conf`). |
| `grep` | Lines matching a regular expression (ripgrep when the server has it, else grep). |
| `edit_file` | Replaces exact text in a file. |
| `write_file` | Creates or replaces a file. |
| `upload`, `download` | Copies files between this computer and a host (up to 50 MB). |

Files go over SFTP; on servers without it (small devices with Dropbear, for instance) TGK falls back to commands.
Changes are safe to interrupt: TGK writes a temporary file next to the original and renames it into place, keeping the
file's permissions, encoding and line endings. A file can only be changed after the agent read it, and only if nobody
changed it since. Files of another user are written in place, so they keep their owner.

## Watching what agents do

- The **Agents** chip in the status bar (while agents are allowed) shows how many are connected and lights up while a
  call runs; its menu opens the activity list, the agent log and the settings, and disconnects agents.
- **Activity** lists every call: time, agent, host, tool, command or path, outcome (approved, denied, failed).
- **Agent log in a tab** mirrors each call and the text the agent got back, as it happens.
- The audit log, `logs/agent-audit.log` in TGK's config directory (rotated at 2 MB), keeps the same record as JSON
  lines. It holds commands and paths, never file contents or passwords.

## Security

- The socket is in TGK's config directory, readable only by you, and every connection must present a random token
  from `agent.json` (also only readable by you), renewed each time TGK starts serving. Programs running as your user
  can read it, which is the same trust you give your SSH agent: turn agents off when you don't use them.
- Remote content can try to steer the agent (prompt injection: a README that says "now delete everything"). This is
  why the rules and approvals are enforced by TGK, not by the agent, and why hosts are closed by default.
- An agent hanging up ends its calls in progress and closes the prompts they wait for.

## Not yet

- A headless mode (`tgk-mcp` without the app, for servers and CI): it needs a read-only way to use the vault from a
  second process.
- A chat pane in TGK itself (driving Claude Code from inside the app).
