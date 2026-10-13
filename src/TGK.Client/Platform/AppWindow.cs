using System;
using System.Reflection;
using Blossom;
using Blossom.Core;
using Silk.NET.GLFW;
using Silk.NET.Windowing;
using TGK.Client.Controls;
using GlfwMonitor = Silk.NET.GLFW.Monitor;
using GlfwVideoMode = Silk.NET.GLFW.VideoMode;

namespace TGK.Client.Platform;

/// <summary>
/// The native window, through Blossom's <see cref="Shell"/> window API (sizes are set up front, in
/// <c>Application.Window</c>). Only valid on the UI thread once the window exists.
/// </summary>
public static class AppWindow
{
    private static bool _hooked;
    private static Application? _app;
    private static IWindow? _window;
    private static GlfwCallbacks.WindowRefreshCallback? _onRefresh, _previousOnRefresh; // kept alive while GLFW holds them
    private static GlfwCallbacks.WindowCloseCallback? _onClose, _previousOnClose;
    private static bool _inLiveFrame;

    /// <summary>
    /// Asked when the user closes the window: true keeps it open (e.g. files with unsaved changes; the guard then asks
    /// and closes it with <see cref="Close"/> once settled). Called on the UI thread.
    /// </summary>
    public static Func<bool>? CloseGuard { get; set; }

    /// <summary>Raised on the UI thread after a resize, once Blossom has updated the view size.</summary>
    public static event Action? Resized;

    /// <summary>
    /// Subscribes to the window's resize event (raised after Blossom's own handler, so view sizes are current) and, on
    /// macOS, keeps the content painting while the user drags the window's edge.
    /// </summary>
    public static void HookResize(Application app)
    {
        if (_hooked)
            return;
        _hooked = true;
        _app = app;
        Shell.ClientResized += (_, _) => Resized?.Invoke();
        if (OperatingSystem.IsMacOS())
            HookLiveResize();
        HookClose();
    }

    // GLFW sets the window's close flag and then calls this callback, which may clear it again: the guard gets a say
    // before Blossom's own callback (which ends the application) runs.
    private static unsafe void HookClose()
    {
        Glfw glfw = GlfwProvider.GLFW.Value;
        WindowHandle* handle = glfw.GetCurrentContext();
        if (handle is null)
        {
            Log.Warning("Close guard: no window; unsaved files are not asked about when the window closes.");
            return;
        }
        _onClose = OnCloseRequested;
        _previousOnClose = glfw.SetWindowCloseCallback(handle, _onClose);
    }

    private static unsafe void OnCloseRequested(WindowHandle* handle)
    {
        // Native code calls this: an exception must not escape into it.
        try
        {
            if (CloseGuard?.Invoke() == true)
            {
                GlfwProvider.GLFW.Value.SetWindowShouldClose(handle, false);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Close guard failed: {ex}");
        }
        _previousOnClose?.Invoke(handle);
    }

    /// <summary>
    /// While the user drags the window's edge, macOS (like Windows) runs a modal loop of its own: GLFW's event call does
    /// not return until the mouse button is released, so Blossom's main loop neither ticks nor renders and the content
    /// only catches up on release (the window grows over a stale frame). Blossom renders from its resize handler on
    /// Windows only (since 0.1.4). GLFW still calls the window refresh callback from inside that loop, on every size
    /// step; Silk's handler for it does nothing under Blossom (its frame callback is only set by Silk's own run loop,
    /// which Blossom never enters), so this chains one that runs a loop tick and renders a frame, as the main loop
    /// would. Linux has no such loop; resizing already paints there.
    /// </summary>
    private static unsafe void HookLiveResize()
    {
        // Blossom does not expose its window; the field is read once and the hook is skipped if it is ever renamed.
        _window = typeof(Shell).GetField("window", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IWindow;
        if (_window is null || _window.Handle == 0)
        {
            Log.Warning("Live resize: Blossom's window was not found; the content will repaint when the resize ends.");
            return;
        }
        _onRefresh = OnRefresh;
        _previousOnRefresh = GlfwProvider.GLFW.Value.SetWindowRefreshCallback((WindowHandle*)_window.Handle, _onRefresh);
    }

    private static unsafe void OnRefresh(WindowHandle* handle)
    {
        // Native code calls this: an exception must not escape into it.
        try
        {
            _previousOnRefresh?.Invoke(handle);
            if (_inLiveFrame || _window is not { IsClosing: false } window || _app?.ActiveView is not { } view)
                return;
            _inLiveFrame = true;
            try
            {
                UiClock.RaiseTick(); // what the active view's Loop does (TgkView.OnLoop)
                // Outside a modal loop the main loop would render this frame next, and then has nothing left to render.
                if (view.RenderRequired)
                    window.DoRender();
            }
            finally
            {
                _inLiveFrame = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Live resize frame failed: {ex}");
        }
    }

    /// <summary>
    /// Closes the window the way the user's close button does: the window's close callback runs (Blossom disposes the
    /// application) and the main loop ends. Blossom has no public close, so this goes through GLFW's current context,
    /// which is the app window on the UI thread.
    /// </summary>
    public static unsafe void Close()
    {
        Glfw glfw = GlfwProvider.GLFW.Value;
        WindowHandle* handle = glfw.GetCurrentContext();
        if (handle is null)
            return;
        glfw.SetWindowShouldClose(handle, true);
        // Reading the callback means replacing it, so put it straight back, then run it as GLFW would.
        GlfwCallbacks.WindowCloseCallback? onClose = glfw.SetWindowCloseCallback(handle, null);
        glfw.SetWindowCloseCallback(handle, onClose);
        onClose?.Invoke(handle);
        glfw.PostEmptyEvent();
    }

    /// <summary>Whether the window covers its whole monitor, without the desktop's title bar and taskbar.</summary>
    public static bool IsFullScreen { get; private set; }

    private static (int X, int Y, int Width, int Height, bool Maximized) _windowed; // what leaving full screen restores

    /// <summary>Switches between full screen and the window as it was before.</summary>
    public static void ToggleFullScreen() => SetFullScreen(!IsFullScreen);

    /// <summary>
    /// Makes the window cover the monitor it is (mostly) on, with no title bar and above the taskbar, or puts it back
    /// where and how it was. On Windows it becomes a borderless window the size of the monitor, which the taskbar makes
    /// way for while it is in front (as browsers do): GLFW's own full screen there keeps the window above every other
    /// one, or minimizes it whenever it loses the focus. Elsewhere it is GLFW's full screen in the monitor's current
    /// video mode (so the mode does not change; on X11 the window manager's full screen state), which does not
    /// minimize itself on Linux when another window takes the focus. Goes through GLFW's current context, the app
    /// window on the UI thread.
    /// </summary>
    public static unsafe void SetFullScreen(bool on)
    {
        Glfw glfw = GlfwProvider.GLFW.Value;
        WindowHandle* handle = glfw.GetCurrentContext();
        if (handle is null || on == IsFullScreen)
            return;
        if (on)
        {
            bool maximized = glfw.GetWindowAttrib(handle, WindowAttributeGetter.Maximized);
            if (maximized)
                glfw.RestoreWindow(handle); // the size to come back to is the window's normal one
            glfw.GetWindowPos(handle, out int x, out int y);
            glfw.GetWindowSize(handle, out int width, out int height);
            GlfwMonitor* monitor = MonitorOf(glfw, x, y, width, height);
            if (monitor is null)
            {
                Log.Warning("Full screen: no monitor found.");
                if (maximized)
                    glfw.MaximizeWindow(handle);
                return;
            }
            _windowed = (x, y, width, height, maximized);
            GlfwVideoMode* mode = glfw.GetVideoMode(monitor);
            if (OperatingSystem.IsWindows())
            {
                glfw.GetMonitorPos(monitor, out int mx, out int my);
                glfw.SetWindowAttrib(handle, WindowAttributeSetter.Decorated, false);
                glfw.SetWindowPos(handle, mx, my);
                glfw.SetWindowSize(handle, mode->Width, mode->Height);
            }
            else
            {
                if (OperatingSystem.IsLinux())
                    glfw.SetWindowAttrib(handle, WindowAttributeSetter.AutoIconify, false);
                glfw.SetWindowMonitor(handle, monitor, 0, 0, mode->Width, mode->Height, mode->RefreshRate);
            }
        }
        else
        {
            (int x, int y, int width, int height, bool maximized) = _windowed;
            if (OperatingSystem.IsWindows())
            {
                glfw.SetWindowAttrib(handle, WindowAttributeSetter.Decorated, true);
                glfw.SetWindowPos(handle, x, y);
                glfw.SetWindowSize(handle, width, height);
            }
            else
            {
                glfw.SetWindowMonitor(handle, null, x, y, width, height, Glfw.DontCare);
                if (OperatingSystem.IsLinux())
                    glfw.SetWindowAttrib(handle, WindowAttributeSetter.AutoIconify, true);
            }
            if (maximized)
                glfw.MaximizeWindow(handle);
        }
        IsFullScreen = on;
    }

    /// <summary>The monitor that holds most of the window's area (the primary one if it is on none).</summary>
    private static unsafe GlfwMonitor* MonitorOf(Glfw glfw, int x, int y, int width, int height)
    {
        GlfwMonitor** monitors = glfw.GetMonitors(out int count);
        GlfwMonitor* best = glfw.GetPrimaryMonitor();
        long bestArea = 0;
        for (int i = 0; i < count; i++)
        {
            GlfwVideoMode* mode = glfw.GetVideoMode(monitors[i]);
            if (mode is null)
                continue;
            glfw.GetMonitorPos(monitors[i], out int mx, out int my);
            long w = Math.Min(x + width, mx + mode->Width) - Math.Max(x, mx);
            long h = Math.Min(y + height, my + mode->Height) - Math.Max(y, my);
            if (w > 0 && h > 0 && w * h > bestArea)
            {
                best = monitors[i];
                bestArea = w * h;
            }
        }
        return best;
    }

    /// <summary>
    /// Asks the desktop to draw attention to the window (taskbar flash, dock bounce) when it is not focused — e.g. an
    /// agent needs the user to approve something. Goes through GLFW's current context, the app window on the UI thread.
    /// </summary>
    public static unsafe void RequestAttention()
    {
        Glfw glfw = GlfwProvider.GLFW.Value;
        WindowHandle* handle = glfw.GetCurrentContext();
        if (handle is null || glfw.GetWindowAttrib(handle, WindowAttributeGetter.Focused))
            return;
        glfw.RequestWindowAttention(handle);
    }
}
