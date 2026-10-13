using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Pty;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>Local terminals on Unix: a real PTY and /bin/sh (Windows' ConPTY is not covered here).</summary>
public sealed class PtyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunsTheProgramAndReportsItsExitCode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTY");
        using var run = Run("/bin/sh", "-c", "printf 'hello from the pty'; exit 3");
        await run.WaitForOutput("hello from the pty");
        Assert.Equal(3, await run.Exit);
    }

    [Fact]
    public async Task TheProgramSeesTheTerminalSizeAndItsChanges()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTY");
        using var run = Run("/bin/sh", "-c", "stty size; read line; stty size", cols: 91, rows: 27);
        await run.WaitForOutput("27 91");
        run.Pty.Resize(100, 30);
        run.Pty.Write("\n"u8);
        await run.WaitForOutput("30 100");
        Assert.Equal(0, await run.Exit);
    }

    [Fact]
    public async Task CtrlCInterruptsTheProgram()
    {
        // The PTY is the program's controlling terminal and SIGINT has its default disposition again (.NET changes
        // some; a non-interactive sh can't trap a signal that was ignored when it started).
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTY");
        using var run = Run("/bin/sh", "-c", "trap 'echo interrupted; exit 7' INT; echo ready; while :; do sleep 0.1; done");
        await run.WaitForOutput("ready");
        run.Pty.Write([0x03]);
        await run.WaitForOutput("interrupted");
        Assert.Equal(7, await run.Exit);
    }

    [Fact]
    public async Task ShellsStartInTheHomeFolderAsAnXtermTerminal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTY");
        var shell = new LocalShell("/bin/sh", "sh", "/bin/sh", ["-c", "echo \"[$TERM][$TERM_PROGRAM][$(pwd)]\""]);
        using var run = Run(shell.StartInfo(80, 24, "1.2.3"));
        await run.WaitForOutput($"[xterm-256color][TGK][{LocalShells.HomeFolder}]");
    }

    [Fact]
    public async Task DisposeEndsARunningProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix PTY");
        var run = Run("/bin/sh", "-c", "echo ready; sleep 30");
        await run.WaitForOutput("ready");
        run.Dispose();
        int code = await run.Exit.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(128 + 1, code); // SIGHUP
    }

    [Fact]
    public void FindsTheUsersShell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix shells");
        Assert.NotEmpty(LocalShells.Available());
        Assert.NotNull(LocalShells.Find("/no/such/shell")); // falls back to the default
        Assert.Equal("/bin/sh", LocalShells.Find("/bin/sh")?.Path);
    }

    private static Running Run(string path, params string[] args) => Run(new PtyStartInfo(path, args, 80, 24));

    private static Running Run(string path, string a, string b, int cols, int rows) => Run(new PtyStartInfo(path, [a, b], cols, rows));

    private static Running Run(PtyStartInfo info)
    {
        var run = new Running(PtyProcess.Create());
        run.Pty.Start(info);
        return run;
    }

    private sealed class Running : IDisposable
    {
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _changed = new(0);

        public Running(PtyProcess pty)
        {
            Pty = pty;
            pty.DataReceived += (buffer, count) =>
            {
                lock (_output)
                    _output.Append(Encoding.UTF8.GetString(buffer, 0, count));
                _changed.Release();
            };
            pty.Exited += code => _exit.TrySetResult(code);
        }

        public PtyProcess Pty { get; }

        public Task<int> Exit => _exit.Task.WaitAsync(Timeout);

        public async Task WaitForOutput(string text)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                lock (_output)
                {
                    if (_output.ToString().Contains(text, StringComparison.Ordinal))
                        return;
                }
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    lock (_output)
                        Assert.Fail($"No \"{text}\" in the output: {_output}");
                }
                await _changed.WaitAsync(left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200));
            }
        }

        public void Dispose() => Pty.Dispose();
    }
}
