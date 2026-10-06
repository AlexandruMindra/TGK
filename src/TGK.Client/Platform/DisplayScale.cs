using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Blossom;
using Blossom.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Monitor = Silk.NET.GLFW.Monitor;
using MouseButton = Silk.NET.Input.MouseButton;

namespace TGK.Client.Platform;

/// <summary>
/// Scales the whole UI to the display's scale factor (Windows' "Scale" setting, e.g. 175% on a 4K screen; Xft.dpi on
/// Linux). Blossom lays out in window coordinates and only scales its drawing by framebuffer / window size, which is
/// right on macOS (window sizes are in points there) but 1 on Windows and X11, where GLFW gives window sizes in
/// physical pixels: everything came out at 100% and tiny. This keeps Blossom's view size in logical units (the window
/// size divided by the factor), so its renderer scales the canvas by the factor, and divides the pointer positions
/// Blossom reads by it too. Blossom has no API for any of this; the fields are looked up once, and if one is ever
/// renamed nothing is changed (the UI stays at 100%).
/// </summary>
internal static class DisplayScale
{
    private const float MinFactor = 1, MaxFactor = 4;

    private static Application? _app;
    private static IWindow? _window;
    private static FieldInfo? _renderRect;
    private static nint _getContentScale;
    private static Action? _onChanged;
    private static nint _previousScaleCallback;
    private static int _minWidth, _minHeight;

    /// <summary>Logical units per physical pixel ratio applied to the UI (1 = no scaling).</summary>
    public static float Factor { get; private set; } = 1;

    /// <summary>
    /// Hooks the window once it exists (UI thread). <paramref name="onChanged"/> runs after the factor changes on its own
    /// (the window moved to a display with another scale), so the view can refit; a resize already reports itself.
    /// Must be subscribed to <see cref="Shell.ClientResized"/> before anything that reads the view size.
    /// </summary>
    public static unsafe void Install(Application app, Action onChanged)
    {
        try
        {
            _window = typeof(Shell).GetField("window", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IWindow;
            _renderRect = typeof(Shell).GetField("RenderRect", BindingFlags.NonPublic | BindingFlags.Static);
            var input = typeof(Shell).GetField("input", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IInputContext;
            if (_window is null || _window.Handle == 0 || _renderRect?.FieldType != typeof(RectangleF) || input is null)
            {
                Log.Warning("Display scale: Blossom's window was not found; the UI is not scaled.");
                return;
            }
            Glfw glfw = GlfwProvider.GLFW.Value;
            // Silk.NET binds neither glfwGetWindowContentScale nor its callback (both GLFW 3.3); resolve them from the
            // library Silk loaded.
            INativeContext? ctx = typeof(NativeApiContainer).GetField("_ctx", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(glfw) as INativeContext;
            if (ctx is null || !ctx.TryGetProcAddress("glfwGetWindowContentScale", out _getContentScale) || _getContentScale == 0)
            {
                Log.Warning("Display scale: glfwGetWindowContentScale is not available; the UI is not scaled.");
                return;
            }
            if (!ScalePointer(input.Mice))
            {
                Log.Warning("Display scale: Silk.NET's mouse events were not found; the UI is not scaled.");
                return;
            }

            _app = app;
            _onChanged = onChanged;
            _minWidth = app.Window.MinWidth;
            _minHeight = app.Window.MinHeight;
            Shell.ClientResized += (_, _) => Apply();
            if (ctx.TryGetProcAddress("glfwSetWindowContentScaleCallback", out nint setCallback) && setCallback != 0)
            {
                var set = (delegate* unmanaged[Cdecl]<WindowHandle*, delegate* unmanaged[Cdecl]<WindowHandle*, float, float, void>, nint>)setCallback;
                _previousScaleCallback = set((WindowHandle*)_window.Handle, &OnContentScale);
            }

            if (Apply())
                FitWindow(app.Window.Width, app.Window.Height);
            Log.Info($"Display scale: {Factor:0.##}");
        }
        catch (Exception ex)
        {
            Log.Error($"Display scale: hooking failed, the UI is not scaled: {ex}");
        }
    }

    /// <summary>
    /// Recomputes the factor and puts Blossom's view size in logical units (Blossom has just set it to the window size
    /// when this runs from <see cref="Shell.ClientResized"/>). True if the factor changed.
    /// </summary>
    private static unsafe bool Apply()
    {
        if (_window is null || _renderRect is null)
            return false;
        var handle = (WindowHandle*)_window.Handle;
        Glfw glfw = GlfwProvider.GLFW.Value;
        glfw.GetWindowSize(handle, out int width, out int height);
        glfw.GetFramebufferSize(handle, out int fbWidth, out int fbHeight);
        if (width <= 0 || height <= 0 || fbWidth <= 0 || fbHeight <= 0)
            return false; // minimized: keep the current layout

        float contentScale, unused;
        ((delegate* unmanaged[Cdecl]<WindowHandle*, float*, float*, void>)_getContentScale)(handle, &contentScale, &unused);
        // The part of the content scale the framebuffer does not already carry: the factor on Windows and X11, 1 on
        // macOS (and Wayland), where the framebuffer is already that much larger than the window.
        float factor = Math.Clamp(MathF.Round(contentScale * width / fbWidth * 100) / 100, MinFactor, MaxFactor);
        if (!float.IsFinite(factor))
            factor = 1;
        bool changed = factor != Factor;
        Factor = factor;

        var logical = new RectangleF(0, 0, width / factor, height / factor);
        if ((RectangleF)_renderRect.GetValue(null)! != logical)
        {
            _renderRect.SetValue(null, logical);
            if (_app?.ActiveView is { } view)
                view.ForceLayoutEvaluation();
        }
        if (changed && (_minWidth > 0 || _minHeight > 0))
        {
            (int maxWidth, int maxHeight) = WorkArea(handle);
            Shell.SetMinClientSize(Math.Min((int)MathF.Ceiling(_minWidth * factor), maxWidth), Math.Min((int)MathF.Ceiling(_minHeight * factor), maxHeight));
        }
        return changed;
    }

    /// <summary>Sizes the window to a logical size (fitting the display's work area) and centers it.</summary>
    private static unsafe void FitWindow(int width, int height)
    {
        (int maxWidth, int maxHeight) = WorkArea((WindowHandle*)_window!.Handle);
        Shell.SetClientSize((int)Math.Min(width * Factor, maxWidth * 0.9), (int)Math.Min(height * Factor, maxHeight * 0.9));
        Shell.Center();
    }

    /// <summary>Work area of the monitor holding the window's center (else the primary one).</summary>
    private static unsafe (int Width, int Height) WorkArea(WindowHandle* handle)
    {
        Glfw glfw = GlfwProvider.GLFW.Value;
        glfw.GetWindowPos(handle, out int x, out int y);
        glfw.GetWindowSize(handle, out int width, out int height);
        int cx = x + width / 2, cy = y + height / 2;
        Monitor** monitors = glfw.GetMonitors(out int count);
        Monitor* chosen = glfw.GetPrimaryMonitor();
        for (int i = 0; i < count; i++)
        {
            glfw.GetMonitorWorkarea(monitors[i], out int mx, out int my, out int mw, out int mh);
            if (cx >= mx && cx < mx + mw && cy >= my && cy < my + mh)
                chosen = monitors[i];
        }
        if (chosen is null)
            return (int.MaxValue, int.MaxValue);
        glfw.GetMonitorWorkarea(chosen, out _, out _, out int workWidth, out int workHeight);
        return workWidth > 0 && workHeight > 0 ? (workWidth, workHeight) : (int.MaxValue, int.MaxValue);
    }

    // The window moved to a display with another scale (GLFW does not resize it).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnContentScale(WindowHandle* handle, float xScale, float yScale)
    {
        // Native code calls this: an exception must not escape into it.
        try
        {
            if (_previousScaleCallback != 0)
                ((delegate* unmanaged[Cdecl]<WindowHandle*, float, float, void>)_previousScaleCallback)(handle, xScale, yScale);
            if (Apply())
            {
                Log.Info($"Display scale: {Factor:0.##}");
                _onChanged?.Invoke();
                GlfwProvider.GLFW.Value.PostEmptyEvent();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Display scale change failed: {ex}");
        }
    }

    /// <summary>
    /// Blossom takes pointer positions from Silk's mouse events and <see cref="IMouse.Position"/> (window coordinates):
    /// its handlers are wrapped so they see a mouse whose positions are in logical units. All or nothing.
    /// </summary>
    private static bool ScalePointer(IReadOnlyList<IMouse> mice)
    {
        var hooks = new List<(FieldInfo Field, object Mouse, Delegate Wrapper)>();
        foreach (IMouse mouse in mice)
        {
            FieldInfo? move = EventField(mouse, nameof(IMouse.MouseMove));
            FieldInfo? down = EventField(mouse, nameof(IMouse.MouseDown));
            FieldInfo? up = EventField(mouse, nameof(IMouse.MouseUp));
            if (move?.FieldType != typeof(Action<IMouse, Vector2>) || down?.FieldType != typeof(Action<IMouse, MouseButton>) || up?.FieldType != typeof(Action<IMouse, MouseButton>))
                return false;
            var scaled = new ScaledMouse(mouse);
            if (move.GetValue(mouse) is Action<IMouse, Vector2> onMove)
                hooks.Add((move, mouse, new Action<IMouse, Vector2>((_, pos) => onMove(scaled, pos / Factor))));
            if (down.GetValue(mouse) is Action<IMouse, MouseButton> onDown)
                hooks.Add((down, mouse, new Action<IMouse, MouseButton>((_, button) => onDown(scaled, button))));
            if (up.GetValue(mouse) is Action<IMouse, MouseButton> onUp)
                hooks.Add((up, mouse, new Action<IMouse, MouseButton>((_, button) => onUp(scaled, button))));
        }
        foreach ((FieldInfo field, object mouse, Delegate wrapper) in hooks)
            field.SetValue(mouse, wrapper);
        return true;
    }

    /// <summary>Backing field of a field-like event, declared by the mouse's class or one of its bases.</summary>
    private static FieldInfo? EventField(object target, string name)
    {
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { } field)
                return field;
        return null;
    }

    /// <summary>The real mouse, with its position in logical units.</summary>
    private sealed class ScaledMouse(IMouse inner) : IMouse
    {
        public string Name => inner.Name;
        public int Index => inner.Index;
        public bool IsConnected => inner.IsConnected;
        public IReadOnlyList<MouseButton> SupportedButtons => inner.SupportedButtons;
        public IReadOnlyList<ScrollWheel> ScrollWheels => inner.ScrollWheels;
        public ICursor Cursor => inner.Cursor;

        public Vector2 Position
        {
            get => inner.Position / Factor;
            set => inner.Position = value * Factor;
        }

        public int DoubleClickTime { get => inner.DoubleClickTime; set => inner.DoubleClickTime = value; }
        public int DoubleClickRange { get => inner.DoubleClickRange; set => inner.DoubleClickRange = value; }
        public bool IsButtonPressed(MouseButton btn) => inner.IsButtonPressed(btn);

        public event Action<IMouse, MouseButton>? MouseDown { add => inner.MouseDown += value; remove => inner.MouseDown -= value; }
        public event Action<IMouse, MouseButton>? MouseUp { add => inner.MouseUp += value; remove => inner.MouseUp -= value; }
        public event Action<IMouse, MouseButton, Vector2>? Click { add => inner.Click += value; remove => inner.Click -= value; }
        public event Action<IMouse, MouseButton, Vector2>? DoubleClick { add => inner.DoubleClick += value; remove => inner.DoubleClick -= value; }
        public event Action<IMouse, Vector2>? MouseMove { add => inner.MouseMove += value; remove => inner.MouseMove -= value; }
        public event Action<IMouse, ScrollWheel>? Scroll { add => inner.Scroll += value; remove => inner.Scroll -= value; }
    }
}
