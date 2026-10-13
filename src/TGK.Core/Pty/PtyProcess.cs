using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace TGK.Core.Pty;

/// <summary>What to start in a pseudo terminal.</summary>
/// <param name="Path">The program (an absolute path).</param>
/// <param name="Args">Its arguments, after the program name.</param>
/// <param name="WorkingDirectory">Where it starts; null keeps the current directory.</param>
/// <param name="Environment">Extra variables, or null values to remove inherited ones.</param>
/// <param name="ArgZero">The program name it sees (Unix <c>argv[0]</c>, e.g. <c>-zsh</c> for a login shell); null: <paramref name="Path"/>.</param>
public sealed record PtyStartInfo(
    string Path,
    IReadOnlyList<string> Args,
    int Cols,
    int Rows,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? ArgZero = null);

/// <summary>
/// A program running in a pseudo terminal of this computer (a local shell): its output arrives as bytes, keys are written
/// to it and it is told the terminal's size, like an SSH session's shell. Unix (Linux, macOS) uses a PTY, Windows
/// ConPTY (Windows 10 1809 or later). Events are raised on background threads.
/// </summary>
public abstract class PtyProcess : IDisposable
{
    private readonly BlockingCollection<byte[]> _input = new();
    private int _started, _exitRaised, _disposed;

    /// <summary>A pseudo terminal for this operating system: subscribe to its events, then <see cref="Start"/> it.</summary>
    public static PtyProcess Create() => OperatingSystem.IsWindows() ? new WindowsPty() : new UnixPty();

    /// <summary>Starts <paramref name="info"/> (once); throws <see cref="PtyException"/> when it can't.</summary>
    public void Start(PtyStartInfo info)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A pseudo terminal starts once.");
        StartCore(info);
        StartThreads();
    }

    /// <summary>Output, from a reader thread (the buffer is reused after the handler returns).</summary>
    public event Action<byte[], int>? DataReceived;

    /// <summary>The program ended, with its exit code (a Unix signal is reported as 128 + its number). Raised once.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; protected set; }

    /// <summary>The program's exit code once it ended, else null.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Queues <paramref name="data"/> for the program (a writer thread sends it, so a busy program never blocks the caller).</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || Volatile.Read(ref _disposed) != 0 || ExitCode is not null)
            return;
        try
        {
            _input.Add(data.ToArray());
        }
        catch (InvalidOperationException)
        {
            // Disposed meanwhile.
        }
    }

    /// <summary>Tells the program the terminal's new size.</summary>
    public abstract void Resize(int cols, int rows);

    /// <summary>Ends the program (if it still runs) and releases the terminal.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _input.CompleteAdding();
        if (Volatile.Read(ref _started) != 0)
            DisposeCore();
        GC.SuppressFinalize(this);
    }

    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    protected abstract void StartCore(PtyStartInfo info);

    /// <summary>Blocks until output is available; returns the bytes read, 0 at the end of the output.</summary>
    protected abstract int ReadOutput(byte[] buffer);

    protected abstract void WriteInput(byte[] data);

    /// <summary>Blocks until the program ends; returns its exit code.</summary>
    protected abstract int WaitForExit();

    protected abstract void DisposeCore();

    /// <summary>The reader thread has stopped reading (the output ended or the terminal was disposed).</summary>
    protected virtual void ReaderStopped() { }

    private void StartThreads()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "pty-write" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!IsDisposed)
            {
                int n = ReadOutput(buffer);
                if (n <= 0)
                    break;
                DataReceived?.Invoke(buffer, n);
            }
        }
        catch (Exception ex) when (IsDisposed || ex is PtyException or System.IO.IOException or ObjectDisposedException)
        {
            // The terminal was closed.
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Local terminal output failed: {ex}");
        }
        finally
        {
            ReaderStopped();
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (byte[] data in _input.GetConsumingEnumerable())
                WriteInput(data);
        }
        catch (Exception ex) when (IsDisposed || ex is PtyException or System.IO.IOException or ObjectDisposedException)
        {
            // The program or the terminal ended.
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Local terminal input failed: {ex}");
        }
    }

    private void WaitLoop()
    {
        int code;
        try
        {
            code = WaitForExit();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                CoreLog.Warn($"Local terminal: waiting for the program failed: {ex.Message}");
            code = -1;
        }
        RaiseExited(code);
    }

    private void RaiseExited(int code)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0)
            return;
        ExitCode = code;
        Exited?.Invoke(code);
    }
}

/// <summary>A local terminal could not be started or used.</summary>
public sealed class PtyException(string message) : Exception(message);
