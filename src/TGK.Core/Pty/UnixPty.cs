using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TGK.Core.Pty;

/// <summary>
/// Linux and macOS: a PTY from <c>/dev/ptmx</c>, and the program started with <c>posix_spawn</c> as the leader of a new
/// session whose controlling terminal is the PTY (so Ctrl+C, Ctrl+Z and job control work). <c>posix_spawn</c> rather
/// than <c>fork</c>: after a fork only the forking thread exists, and running managed code there can deadlock.
/// </summary>
/// <remarks>
/// The reader waits with <c>select</c> (macOS's <c>poll</c> does not support terminals) so it notices
/// <see cref="PtyProcess.Dispose"/>, and it closes the PTY itself: closing a file descriptor another thread is reading
/// leaves that thread blocked. <c>ioctl</c> is variadic, and Apple's arm64 ABI passes variadic arguments on the stack,
/// hence its padded declaration.
/// </remarks>
internal sealed class UnixPty : PtyProcess
{
    private const int EINTR = 4, EIO = 5;
    private const int SIGHUP = 1, SIGKILL = 9;
    private static readonly bool Mac = OperatingSystem.IsMacOS();
    private static readonly bool MacArm64 = Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private static readonly int O_RDWR = 2, O_NOCTTY = Mac ? 0x20000 : 0x100, O_CLOEXEC = Mac ? 0x1000000 : 0x80000;
    private static readonly nuint TIOCSWINSZ = Mac ? 0x80087467 : 0x5414;
    private const short POSIX_SPAWN_SETSIGDEF = 0x04, POSIX_SPAWN_SETSIGMASK = 0x08;
    private static readonly short POSIX_SPAWN_SETSID = (short)(Mac ? 0x0400 : 0x80);
    private const short POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000; // macOS only: the child gets just the descriptors set up for it
    private static readonly Lock PtsnameGate = new(); // ptsname returns a static buffer

    private readonly Lock _gate = new();
    private int _master = -1;
    private bool _readerDone;
    private Timer? _killTimer; // referenced so it is not collected before it fires

    protected override void StartCore(PtyStartInfo info)
    {
        // open, not posix_openpt: it takes O_CLOEXEC everywhere, so other programs TGK starts never inherit the PTY.
        _master = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (_master < 0)
            throw Failure("Could not open a pseudo terminal");
        if (grantpt(_master) != 0 || unlockpt(_master) != 0)
            throw Failure("Could not set up the pseudo terminal");
        string slave;
        lock (PtsnameGate)
        {
            IntPtr name = ptsname(_master);
            if (name == IntPtr.Zero)
                throw Failure("Could not set up the pseudo terminal");
            slave = Marshal.PtrToStringUTF8(name)!;
        }
        Resize(info.Cols, info.Rows);
        ProcessId = Spawn(info, slave);
    }

    private static int Spawn(PtyStartInfo info, string slave)
    {
        // Opaque libc types, allocated generously (glibc's are 80 and 336 bytes, macOS's a pointer; sigset_t 128 bytes).
        IntPtr actions = Marshal.AllocHGlobal(1024), attr = Marshal.AllocHGlobal(1024), signals = Marshal.AllocHGlobal(256);
        var strings = new List<IntPtr>();
        bool actionsReady = false, attrReady = false;
        try
        {
            Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            actionsReady = true;
            Check(posix_spawnattr_init(attr), "posix_spawnattr_init");
            attrReady = true;
            // The new session's leader opens the terminal first: on Linux that makes it the controlling terminal.
            Check(posix_spawn_file_actions_addopen(actions, 0, slave, O_RDWR, 0), "posix_spawn_file_actions_addopen");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "posix_spawn_file_actions_adddup2");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "posix_spawn_file_actions_adddup2");
            if (info.WorkingDirectory is { Length: > 0 } cwd)
            {
                try
                {
                    Check(posix_spawn_file_actions_addchdir_np(actions, cwd), "posix_spawn_file_actions_addchdir_np");
                }
                catch (EntryPointNotFoundException)
                {
                    // An old libc (glibc before 2.29, macOS before 10.15): the program starts in TGK's directory.
                }
            }
            // .NET blocks and ignores signals the program must get with their defaults (SIGPIPE, SIGINT, SIGCHLD...).
            sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attr, signals), "posix_spawnattr_setsigmask");
            sigfillset(signals);
            Check(posix_spawnattr_setsigdefault(attr, signals), "posix_spawnattr_setsigdefault");
            short flags = (short)(POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID | (Mac ? POSIX_SPAWN_CLOEXEC_DEFAULT : 0));
            Check(posix_spawnattr_setflags(attr, flags), "posix_spawnattr_setflags");

            var argv = new List<IntPtr> { Utf8(strings, info.ArgZero ?? info.Path) };
            foreach (string arg in info.Args)
                argv.Add(Utf8(strings, arg));
            argv.Add(IntPtr.Zero);
            var envp = new List<IntPtr>();
            foreach ((string key, string value) in Environment(info.Environment))
                envp.Add(Utf8(strings, $"{key}={value}"));
            envp.Add(IntPtr.Zero);

            int error = posix_spawn(out int pid, info.Path, actions, attr, argv.ToArray(), envp.ToArray());
            if (error != 0)
                throw new PtyException($"Could not start {info.Path}: {Marshal.GetPInvokeErrorMessage(error)}");
            return pid;
        }
        finally
        {
            if (actionsReady)
                posix_spawn_file_actions_destroy(actions);
            if (attrReady)
                posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(signals);
            foreach (IntPtr s in strings)
                Marshal.FreeCoTaskMem(s);
        }
    }

    // TGK's environment with the changes applied, sorted for a stable order.
    private static SortedDictionary<string, string> Environment(IReadOnlyDictionary<string, string?>? changes)
    {
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            if (e.Key is string key && e.Value is string value)
                env[key] = value;
        }
        foreach ((string key, string? value) in changes ?? new Dictionary<string, string?>())
        {
            if (value is null)
                env.Remove(key);
            else
                env[key] = value;
        }
        return env;
    }

    private static IntPtr Utf8(List<IntPtr> strings, string s)
    {
        IntPtr p = Marshal.StringToCoTaskMemUTF8(s);
        strings.Add(p);
        return p;
    }

    private static void Check(int error, string call)
    {
        if (error != 0)
            throw new PtyException($"Could not start the program ({call}: {Marshal.GetPInvokeErrorMessage(error)})");
    }

    private static PtyException Failure(string what)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new PtyException($"{what}: {Marshal.GetPInvokeErrorMessage(errno)}");
    }

    public override void Resize(int cols, int rows)
    {
        var size = new WinSize { Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue), Cols = (ushort)Math.Clamp(cols, 1, ushort.MaxValue) };
        lock (_gate)
        {
            if (_master < 0)
                return;
            // The kernel sends SIGWINCH to the terminal's foreground process group.
            if (MacArm64)
                ioctl_padded(_master, TIOCSWINSZ, 0, 0, 0, 0, 0, 0, ref size);
            else
                ioctl(_master, TIOCSWINSZ, ref size);
        }
    }

    protected override int ReadOutput(byte[] buffer)
    {
        int master = _master;
        var readable = new byte[128]; // fd_set: the same bit layout on Linux and macOS (little-endian)
        while (!IsDisposed)
        {
            if (master < 1024)
            {
                Array.Clear(readable);
                readable[master / 8] |= (byte)(1 << (master % 8));
                var timeout = new TimeVal { Seconds = 0, Microseconds = 250_000 };
                int ready = select(master + 1, readable, IntPtr.Zero, IntPtr.Zero, ref timeout);
                if (ready < 0 && Marshal.GetLastPInvokeError() == EINTR)
                    continue;
                if (ready == 0)
                    continue;
            }
            nint n = read(master, buffer, buffer.Length);
            if (n > 0)
                return (int)n;
            if (n < 0 && Marshal.GetLastPInvokeError() == EINTR)
                continue;
            // 0 or EIO: everything that had the terminal open has closed it (the program ended).
            return 0;
        }
        return 0;
    }

    protected override void ReaderStopped()
    {
        lock (_gate)
        {
            _readerDone = true;
            if (IsDisposed)
                CloseMaster();
        }
    }

    protected override void WriteInput(byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            nint n;
            lock (_gate)
            {
                if (_master < 0)
                    return;
                n = write(_master, ref data[offset], data.Length - offset);
            }
            if (n > 0)
                offset += (int)n;
            else if (n < 0 && Marshal.GetLastPInvokeError() is not EINTR)
                throw new PtyException($"Writing to the terminal failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }
    }

    protected override int WaitForExit()
    {
        while (true)
        {
            int pid = waitpid(ProcessId, out int status, 0);
            if (pid == ProcessId)
                return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            int errno = Marshal.GetLastPInvokeError();
            if (pid < 0 && errno == EINTR)
                continue;
            throw new PtyException($"waitpid: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
    }

    protected override void DisposeCore()
    {
        // A hangup, as when a terminal window closes: the shell passes it on to its jobs and exits. The reader closes the
        // PTY within its select timeout, which hangs up whatever still has it open.
        if (ProcessId > 0 && ExitCode is null)
        {
            kill(-ProcessId, SIGHUP);
            kill(ProcessId, SIGHUP);
            int pid = ProcessId;
            // One that ignores the hangup is ended after a while (waitpid then reaps it).
            _killTimer = new Timer(_ =>
            {
                if (ExitCode is null)
                    kill(pid, SIGKILL);
            }, null, 3000, Timeout.Infinite);
        }
        lock (_gate)
        {
            if (_readerDone || ProcessId == 0)
                CloseMaster();
        }
    }

    // Under _gate.
    private void CloseMaster()
    {
        if (_master < 0)
            return;
        close(_master);
        _master = -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows, Cols, PixelWidth, PixelHeight;
    }

    // Linux: two longs; macOS: a long and an int (then padding), which a little-endian long fills the same way.
    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal
    {
        public long Seconds, Microseconds;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref WinSize size);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_padded(int fd, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref WinSize size);

    [DllImport("libc", SetLastError = true)]
    private static extern int select(int nfds, byte[] readfds, IntPtr writefds, IntPtr exceptfds, ref TimeVal timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    private static extern int sigemptyset(IntPtr set);

    [DllImport("libc")]
    private static extern int sigfillset(IntPtr set);

    [DllImport("libc")]
    private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr fileActions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr set);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr set);
}
