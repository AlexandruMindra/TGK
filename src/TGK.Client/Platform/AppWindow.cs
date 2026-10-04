using System;
using Blossom;
using Silk.NET.GLFW;

namespace TGK.Client.Platform;

/// <summary>
/// The native window, through Blossom's <see cref="Shell"/> window API (sizes are set up front, in
/// <c>Application.Window</c>). Only valid on the UI thread once the window exists.
/// </summary>
public static class AppWindow
{
    private static bool _hooked;

    /// <summary>Raised on the UI thread after a resize, once Blossom has updated the view size.</summary>
    public static event Action? Resized;

    /// <summary>Subscribes to the window's resize event (raised after Blossom's own handler, so view sizes are current).</summary>
    public static void HookResize()
    {
        if (_hooked)
            return;
        _hooked = true;
        Shell.ClientResized += (_, _) => Resized?.Invoke();
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
