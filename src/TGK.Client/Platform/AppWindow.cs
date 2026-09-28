using System;
using System.Reflection;
using Blossom;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace TGK.Client.Platform;

/// <summary>
/// Access to the native window, which Blossom keeps private (no public size, minimum-size or resize API).
/// Only valid on the UI thread once the window exists.
/// </summary>
public static class AppWindow
{
    private static bool _hooked;

    /// <summary>Raised on the UI thread after a resize, once Blossom has updated the view size.</summary>
    public static event Action? Resized;

    public static IWindow? Window =>
        (IWindow?)typeof(Browser).GetField("window", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    public static unsafe WindowHandle* Handle => Window is { } w ? (WindowHandle*)w.Handle : null;

    /// <summary>Subscribes to the window's resize event (after Blossom's own handler, so view sizes are current).</summary>
    public static void HookResize()
    {
        if (_hooked || Window is not { } window)
            return;
        _hooked = true;
        window.Resize += _ => Resized?.Invoke();
    }

    public static unsafe void SetMinimumSize(int width, int height)
    {
        WindowHandle* handle = Handle;
        if (handle is not null)
            GlfwProvider.GLFW.Value.SetWindowSizeLimits(handle, width, height, Glfw.DontCare, Glfw.DontCare);
    }

    public static void SetSize(int width, int height)
    {
        if (Window is not { } window)
            return;
        window.Size = new Vector2D<int>(width, height);
        window.Center();
    }
}
