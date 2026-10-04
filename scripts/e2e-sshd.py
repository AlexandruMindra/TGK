#!/usr/bin/env python3
"""Throwaway SSH server for TGK's end-to-end tests (docs/DEV-TESTING.md). Never expose it: it runs every command
and serves every file of the user that starts it.

Offers what the E2E tests need: password and public-key sign-in for one user, bash in a PTY per interactive session
(pty-req and window-change honoured), exec commands with stdin and exit codes, SFTP on the real filesystem (unless
--no-sftp), and port forwarding (direct-tcpip, tcpip-forward) for the jump host and tunnel tests.

    python3 -m venv .venv-ssh && .venv-ssh/bin/pip install asyncssh
    .venv-ssh/bin/python scripts/e2e-sshd.py --user test --password test [--authorized-key client_key.pub] [--port 2222]
"""
import argparse
import asyncio
import fcntl
import os
import pty
import struct
import termios

import asyncssh

args = None


class Server(asyncssh.SSHServer):
    def begin_auth(self, username):
        return True

    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return username == args.user and password == args.password

    def public_key_auth_supported(self):
        return args.authorized_key is not None

    def connection_requested(self, dest_host, dest_port, orig_host, orig_port):
        return True

    def server_requested(self, listen_host, listen_port):
        return True


async def run_shell(process):
    cols, rows, _, _ = process.term_size or (80, 24, 0, 0)
    pid, fd = pty.fork()
    if pid == 0:
        os.environ['TERM'] = 'xterm-256color'
        os.environ['PS1'] = r'\u@test:\w\$ '
        for name, value in (process.env or {}).items():
            os.environ[name] = value
        os.execvp('bash', ['bash', '--norc', '-i'])

    def setsize(c, r):
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack('HHHH', r, c, 0, 0))

    setsize(cols or 80, rows or 24)
    loop = asyncio.get_running_loop()
    done = asyncio.Event()

    def readable():
        try:
            data = os.read(fd, 65536)
        except OSError:
            data = b''
        if not data:
            loop.remove_reader(fd)
            done.set()
            return
        process.stdout.write(data)

    loop.add_reader(fd, readable)

    async def pump():
        while True:
            try:
                data = await process.stdin.read(4096)
            except asyncssh.TerminalSizeChanged as e:
                setsize(e.width, e.height)
                continue
            except Exception:
                break
            if not data:
                break
            os.write(fd, data)

    asyncio.ensure_future(pump())
    await done.wait()
    process.exit(0)


async def run_command(process):
    proc = await asyncio.create_subprocess_exec(
        'bash', '-c', process.command, cwd=os.path.expanduser('~'),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
        start_new_session=True)

    async def feed():
        try:
            while True:
                data = await process.stdin.read(65536)
                if not data:
                    break
                proc.stdin.write(data)
                await proc.stdin.drain()
        except asyncssh.SignalReceived:
            # TGK stops a command that runs too long with a signal request.
            os.killpg(proc.pid, 9)
        except Exception:
            pass
        finally:
            try:
                proc.stdin.close()
            except Exception:
                pass

    async def copy(src, dst):
        while True:
            data = await src.read(65536)
            if not data:
                break
            dst.write(data)

    feeder = asyncio.ensure_future(feed())
    await asyncio.gather(copy(proc.stdout, process.stdout), copy(proc.stderr, process.stderr))
    code = await proc.wait()
    feeder.cancel()
    process.exit(code if code >= 0 else 128 - code)


async def handle(process):
    if process.command is None:
        await run_shell(process)
    else:
        await run_command(process)


async def main():
    global args
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=2222)
    parser.add_argument('--user', default='test')
    parser.add_argument('--password', default='test')
    parser.add_argument('--authorized-key', help='public key file accepted for the user')
    parser.add_argument('--no-sftp', action='store_true', help='refuse the sftp subsystem (tests the fallback)')
    args = parser.parse_args()

    key = asyncssh.generate_private_key('ssh-ed25519')
    await asyncssh.create_server(
        Server, args.host, args.port, server_host_keys=[key], process_factory=handle, encoding=None,
        line_editor=False, sftp_factory=None if args.no_sftp else True,
        authorized_client_keys=args.authorized_key, allow_scp=False)
    print(f'listening on {args.host}:{args.port}', flush=True)
    await asyncio.Event().wait()


asyncio.run(main())
