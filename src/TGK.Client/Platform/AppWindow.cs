using System;
using System.Reflection;
using Blossom;
using Blossom.Core;
using Silk.NET.GLFW;
using Silk.NET.Windowing;
using TGK.Client.Controls;

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
        DisplayScale.Install(app, () => Resized?.Invoke()); // first: it turns the view size Blossom just set into logical units
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
