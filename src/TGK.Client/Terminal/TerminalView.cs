using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Core.Models;
using TGK.Terminal;

namespace TGK.Client.Terminal;

/// <summary>
/// An interactive terminal: renders a <see cref="TerminalEmulator"/>, turns keyboard, mouse and clipboard actions into
/// the bytes a host expects (<see cref="Input"/>) and feeds host output back in through <see cref="Write"/>.
/// It knows nothing about SSH; the session tab connects the two.
/// </summary>
/// <remarks>
/// Everything except <see cref="Write"/> runs on the UI thread. Host output is queued by <see cref="Write"/> and parsed
/// from the view loop with a per-iteration time budget, so a flood of output never starves input or rendering. The
/// queue is bounded: <see cref="Write"/> blocks the producer while it is full, which slows a flooding host down to
/// the speed the terminal can parse instead of buffering without limit.
/// </remarks>
public sealed partial class TerminalView : VisualElement, IKeyInput
{
    private const float PadX = 8, PadY = 6;
    private const int ResizeDebounceMs = 60;
    private const int BellFlashMs = 150;

    // Parsing time per loop iteration; a large backlog gets more so it drains faster, at some cost to input latency.
    private const long FeedBudgetTicks = 8 * TimeSpan.TicksPerMillisecond;
    private const long BacklogFeedBudgetTicks = 25 * TimeSpan.TicksPerMillisecond;
    private const long BacklogBytes = 512 << 10;
    private const int MinFloodPaintMs = 33, MaxFloodPaintMs = 250;

    // Write blocks above MaxQueuedBytes until parsing brings the backlog down to ResumeQueuedBytes. The limit is small
    // because everything queued still has to be shown after Ctrl+C. Chunks are fed in slices so the parse budget also
    // holds for large chunks (and for sequences that expand into a lot of work).
    private const long MaxQueuedBytes = 1 << 20, ResumeQueuedBytes = 256 << 10;
    private const int FeedSliceBytes = 4096;

    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private readonly ManualResetEventSlim _queueHasRoom = new(true);
    private readonly TerminalPalette _palette = new();
    private long _queuedBytes;
    private byte[]? _feeding; // the chunk Pump is part-way through
    private int _feedingOffset;
    private volatile bool _disposed;
    private TerminalSettings _settings;
    private TerminalFont _font;
    private long _resizeDueAt = -1;
    private long _bellUntil;
    private long _nextFloodPaint;
    private long _lastTickMs;
    private long _renderCostMs; // loop time taken by the last frame that painted the terminal
    private bool _paintedSinceTick;
    private int _tick;

    public TerminalView(TerminalSettings settings)
    {
        _settings = settings.Clone();
        _font = new TerminalFont(_settings.FontSize);
        Emulator = new TerminalEmulator(80, 24, Math.Max(0, _settings.ScrollbackLines));
        Emulator.Output += reply => Input?.Invoke(reply);
        Emulator.TitleChanged += title => TitleChanged?.Invoke(title);
        Emulator.Bell += OnBell;

        Name = "Terminal";
        ReceivesKeyboard = true;
        IsClipping = true;
        Cursor = StandardCursor.IBeam;
        Style = new ElementStyle { BackColor = _palette.Background };
        OnFocused += _ => OnFocusChanged(true);
        OnFocusLost += _ => OnFocusChanged(false);
        InitMouse();
        ResizeRows(Emulator.Rows);
        UiClock.Tick += OnTick;
    }

    public TerminalEmulator Emulator { get; }

    /// <summary>Bytes for the host: keystrokes, pastes, mouse reports and the emulator's replies to queries.</summary>
    public event Action<byte[]>? Input;

    /// <summary>The grid size changed (debounced while the window is being resized); forward it to the host.</summary>
    public event Action<int, int>? GridResized;

    /// <summary>The remote application set the window title (OSC 0/2).</summary>
    public event Action<string>? TitleChanged;

    public event Action? BellRang;

    /// <summary>When false (no live session) keys are not sent to the host but bubble to the parent instead.</summary>
    public bool InputEnabled { get; set; }

    public int Cols => Emulator.Cols;
    public int Rows => Emulator.Rows;

    /// <summary>
    /// Queues host output. Thread-safe (call it from one producer thread); the bytes are copied. While the parse
    /// backlog is full this blocks until the view has caught up, or until the view is reset or disposed.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _disposed)
            return;
        _incoming.Enqueue(data.ToArray());
        if (Interlocked.Add(ref _queuedBytes, data.Length) <= MaxQueuedBytes)
            return;
        while (true)
        {
            _queueHasRoom.Reset();
            // Checked after the reset, so a Set from Pump (or ResetSession / Dispose) in between is never missed.
            if (Interlocked.Read(ref _queuedBytes) <= ResumeQueuedBytes || _disposed)
                return;
            _queueHasRoom.Wait();
        }
    }

    /// <summary>
    /// Readies the terminal for a new session: drops output still queued from the previous one and resets the
    /// emulator's modes (alternate screen, mouse reporting, keypad, ...) while keeping the screen and the history.
    /// </summary>
    public void ResetSession()
    {
        _feeding = null;
        while (_incoming.TryDequeue(out byte[]? chunk))
            Interlocked.Add(ref _queuedBytes, -chunk.Length);
        _queueHasRoom.Set(); // releases the old producer if it is blocked in Write
        Emulator.ResetModes();
        CancelPendingAltKey();
        ResetMouseReporting();
        ClearSelection();
        _offset = 0;
        InvalidateAllRows();
        InvalidatePaint();
    }

    /// <summary>Applies changed terminal preferences (font size, cursor, copy-on-select). Scrollback size applies to new sessions.</summary>
    public void ApplySettings(TerminalSettings settings)
    {
        bool fontChanged = Math.Abs(settings.FontSize - _settings.FontSize) > 0.01f;
        _settings = settings.Clone();
        if (fontChanged)
        {
            _font.Dispose();
            _font = new TerminalFont(_settings.FontSize);
            FitToSize();
            InvalidateAllRows();
        }
        ResetBlink();
        InvalidatePaint();
    }

    public override void Dispose()
    {
        _disposed = true;
        _queueHasRoom.Set();
        UiClock.Tick -= OnTick;
        DisposeRendering();
        _font.Dispose();
        base.Dispose();
    }

    protected override void OnSizeChanged(float width, float height) => FitToSize();

    /// <summary>
    /// Recomputes the grid from the element size. Blossom raises no size change for frames set inside a parent's
    /// <c>LayoutChildren</c>, so a parent that lays the terminal out must call this afterwards.
    /// </summary>
    public void FitToSize()
    {
        float w = Transform.Computed.Width - 2 * PadX, h = Transform.Computed.Height - 2 * PadY;
        if (w <= 0 || h <= 0)
            return;
        int cols = Math.Max(2, (int)(w / _font.CellWidth));
        int rows = Math.Max(1, (int)(h / _font.CellHeight));
        if (cols == Emulator.Cols && rows == Emulator.Rows)
            return;
        Emulator.Resize(cols, rows);
        ResizeRows(rows);
        ClearSelection();
        _offset = Math.Min(_offset, Emulator.ScrollbackCount);
        _resizeDueAt = UiClock.NowMs + ResizeDebounceMs;
        InvalidatePaint();
    }

    private void OnTick()
    {
        _tick++;
        long now = UiClock.NowMs;
        if (_paintedSinceTick)
        {
            _paintedSinceTick = false;
            _renderCostMs = now - _lastTickMs;
        }
        if (_feeding is not null || !_incoming.IsEmpty)
            Pump();
        ResolvePendingAltKey();
        if (_resizeDueAt >= 0 && now >= _resizeDueAt)
        {
            _resizeDueAt = -1;
            GridResized?.Invoke(Cols, Rows);
        }
        if (_bellUntil > 0 && now >= _bellUntil)
        {
            _bellUntil = 0;
            InvalidatePaint();
        }
        TickBlink(now);
        TickAutoScroll(now);
        _lastTickMs = UiClock.NowMs; // after parsing, so the gap to the next tick measures the frame alone
    }

    // Parses queued output until the budget is spent; the rest waits for the next loop iteration, so input keeps
    // flowing. A burst that is fully parsed repaints at once (low echo latency); during a flood the terminal repaints
    // at a rate that keeps rendering to about a fifth of the time (slow software GL renders far less often).
    private void Pump()
    {
        long start = Stopwatch.GetTimestamp();
        long budget = Interlocked.Read(ref _queuedBytes) > BacklogBytes ? BacklogFeedBudgetTicks : FeedBudgetTicks;
        long scrolledIn = Emulator.ScrollbackAdded;
        do
        {
            if (_feeding is null)
            {
                if (!_incoming.TryDequeue(out _feeding))
                    break;
                _feedingOffset = 0;
                Interlocked.Add(ref _queuedBytes, -_feeding.Length);
            }
            int count = Math.Min(FeedSliceBytes, _feeding.Length - _feedingOffset);
            Emulator.Feed(_feeding.AsSpan(_feedingOffset, count));
            _feedingOffset += count;
            if (_feedingOffset == _feeding.Length)
                _feeding = null;
        }
        while (Stopwatch.GetElapsedTime(start).Ticks <= budget);
        if (Interlocked.Read(ref _queuedBytes) <= ResumeQueuedBytes)
            _queueHasRoom.Set();

        KeepViewportAnchored(Emulator.ScrollbackAdded - scrolledIn);
        ResetBlink();
        long now = UiClock.NowMs;
        bool caughtUp = _feeding is null && _incoming.IsEmpty;
        if (EffectiveVisible && (caughtUp || now >= _nextFloodPaint))
        {
            _nextFloodPaint = now + Math.Clamp(_renderCostMs * 4, MinFloodPaintMs, MaxFloodPaintMs);
            InvalidatePaint();
        }
    }

    private void OnBell()
    {
        _bellUntil = UiClock.NowMs + BellFlashMs;
        BellRang?.Invoke();
    }

    private void Send(byte[]? bytes)
    {
        if (bytes is { Length: > 0 })
            Input?.Invoke(bytes);
    }

    private void OnFocusChanged(bool focused)
    {
        if (InputEnabled)
            Send(TerminalInput.EncodeFocus(focused, Emulator.Modes));
        if (!focused)
            CancelPendingAltKey();
        ResetBlink();
        InvalidatePaint();
    }
}
