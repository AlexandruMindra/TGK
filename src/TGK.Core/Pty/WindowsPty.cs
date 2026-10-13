using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TGK.Core.Pty;

/// <summary>
/// Windows: a pseudo console (ConPTY, Windows 10 1809 or later) between two pipes, and the program started attached to
/// it. ConPTY speaks UTF-8 and VT sequences both ways, like a Unix PTY.
/// </summary>
/// <remarks>
/// The child gets explicit null standard handles: otherwise it would inherit TGK's own (e.g. redirected to a log) and
/// write there instead of to the pseudo console. Closing the pseudo console ends the programs attached to it and closes
/// the output pipe, which ends the reader; it can block until the output is read, so it runs off the caller's thread.
/// </remarks>
internal sealed class WindowsPty : PtyProcess
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly object _gate = new();
    private IntPtr _console;
    private IntPtr _process;
    private FileStream? _output, _input;

    protected override void StartCore(PtyStartInfo info)
    {
        if (!CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0)
            || !CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
            throw Failure("Could not create the pipes of the pseudo console");
        int hr;
        try
        {
            hr = CreatePseudoConsole(Size(info.Cols, info.Rows), inputRead, outputWrite, 0, out _console);
        }
        catch (EntryPointNotFoundException)
        {
            throw new PtyException("Local terminals need Windows 10 version 1809 or later (ConPTY).");
        }
        // The pseudo console has its own copies; keeping these would keep the pipes open after it closed.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
            throw new PtyException($"Could not create a pseudo console: {Marshal.GetExceptionForHR(hr)?.Message}");
        _input = new FileStream(inputWrite, FileAccess.Write, 1);
        _output = new FileStream(outputRead, FileAccess.Read, 1);

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attributes = Marshal.AllocHGlobal(size);
        IntPtr environment = IntPtr.Zero;
        bool listReady = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw Failure("Could not start the program");
            listReady = true;
            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Failure("Could not attach the program to the pseudo console");
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = STARTF_USESTDHANDLES },
                lpAttributeList = attributes,
            };
            environment = Marshal.StringToHGlobalUni(EnvironmentBlock(info.Environment));
            var commandLine = new StringBuilder(CommandLine(info));
            if (!CreateProcessW(info.Path, commandLine, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    environment, info.WorkingDirectory is { Length: > 0 } cwd && Directory.Exists(cwd) ? cwd : null, ref startup, out PROCESS_INFORMATION process))
                throw Failure($"Could not start {info.Path}");
            CloseHandle(process.hThread);
            _process = process.hProcess;
            ProcessId = process.dwProcessId;
        }
        finally
        {
            if (listReady)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
        }
    }

    private static string CommandLine(PtyStartInfo info)
    {
        var line = new StringBuilder(Quote(info.ArgZero ?? info.Path));
        foreach (string arg in info.Args)
            line.Append(' ').Append(Quote(arg));
        return line.ToString();
    }

    // The quoting CommandLineToArgvW (and the C runtime) undoes.
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(c);
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    // "NAME=value\0...\0\0", sorted case-insensitively as Windows expects.
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string?>? changes)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        var block = new StringBuilder();
        foreach ((string key, string value) in env)
            block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private static COORD Size(int cols, int rows) => new() { X = (short)Math.Clamp(cols, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

    private static PtyException Failure(string what) => new($"{what}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    public override void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            if (_console != IntPtr.Zero)
                ResizePseudoConsole(_console, Size(cols, rows));
        }
    }

    protected override int ReadOutput(byte[] buffer) => _output!.Read(buffer, 0, buffer.Length);

    protected override void WriteInput(byte[] data)
    {
        _input!.Write(data);
        _input.Flush();
    }

    protected override int WaitForExit()
    {
        WaitForSingleObject(_process, INFINITE);
        int code = GetExitCodeProcess(_process, out uint exit) ? unchecked((int)exit) : -1;
        CloseHandle(_process);
        // The program is gone: closing the pseudo console flushes and ends the output, so the reader stops too.
        Task.Run(CloseConsole);
        return code;
    }

    protected override void DisposeCore() => Task.Run(() =>
    {
        CloseConsole(); // ends the programs attached to it
        _input?.Dispose();
    });

    protected override void ReaderStopped() => _output?.Dispose();

    private void CloseConsole()
    {
        IntPtr console;
        lock (_gate)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        if (console != IntPtr.Zero)
            ClosePseudoConsole(console);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
