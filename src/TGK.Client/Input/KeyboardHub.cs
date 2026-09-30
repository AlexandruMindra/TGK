using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Blossom;
using Blossom.Core;
using Blossom.Core.Visual;
using Silk.NET.GLFW;
using Silk.NET.Input;
using TGK.Client.Platform;

namespace TGK.Client.Input;

/// <summary>
/// The app's single keyboard entry point. Blossom's own key routing drops every Ctrl/Alt combination and never
/// repeats keys, so this hooks the Silk.NET keyboards (and GLFW's character callback, for full Unicode text)
/// directly, generates auto-repeat itself, and dispatches:
/// <list type="number">
/// <item><see cref="Shortcuts"/> (global shortcuts),</item>
/// <item>the focused element (<see cref="View.ActiveKeyboardElement"/>) and then its ancestors, for each that implements <see cref="IKeyInput"/>,</item>
/// <item>the active view, when it implements <see cref="IKeyInput"/>.</item>
/// </list>
/// All callbacks run on the UI thread.
/// </summary>
public static class KeyboardHub
{
    private const int RepeatDelayMs = 400;
    private const int RepeatIntervalMs = 33; // ~30 per second

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Application? _app;
    private static IInputContext? _input;
    private static IKeyboard? _repeatKeyboard;
    private static Key? _repeatKey;
    private static long _nextRepeatMs;

    // Kept in fields so the delegates marshalled to GLFW are never collected.
    private static GlfwCallbacks.CharCallback? _charCallback;
    private static GlfwCallbacks.CharCallback? _previousCharCallback;

    public static ShortcutRegistry Shortcuts { get; } = new();

    /// <summary>When false (default), F12 does not toggle Blossom's debug overlay.</summary>
    public static bool AllowDebugOverlay { get; set; }

    public static bool IsInstalled => _app is not null;

    /// <summary>Modifiers held right now (e.g. for Shift+click).</summary>
    public static KeyModifiers CurrentModifiers
    {
        get
        {
            var mods = KeyModifiers.None;
            if (_input is not null)
            {
                foreach (IKeyboard keyboard in _input.Keyboards)
                    mods |= ReadModifiers(keyboard);
            }
            return mods;
        }
    }

    /// <summary>Hooks the keyboards. Call once, on the UI thread, from the first view's <c>Init</c> (input exists by then).</summary>
    public static void Install(Application app)
    {
        if (_app is not null)
            return;
        _app = app;

        var input = _input = (IInputContext?)typeof(Browser).GetField("input", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Blossom input context is not available yet.");
        foreach (IKeyboard keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
        }

        if (!TryHookGlfwChars())
        {
            // Fallback: Silk truncates characters outside the BMP, but everything else works.
            foreach (IKeyboard keyboard in input.Keyboards)
                keyboard.KeyChar += (_, ch) => OnCodepoint(ch);
        }
    }

    /// <summary>Drives auto-repeat. Called every loop iteration by the active view.</summary>
    public static void Tick()
    {
        if (_repeatKey is not { } key || _repeatKeyboard is null)
            return;
        long now = Clock.ElapsedMilliseconds;
        if (now < _nextRepeatMs)
            return;
        if (!_repeatKeyboard.IsKeyPressed(key))
        {
            _repeatKey = null;
            return;
        }
        _nextRepeatMs = now + RepeatIntervalMs;
        Dispatch(new KeyStroke(key, ReadModifiers(_repeatKeyboard), IsRepeat: true));
    }

    /// <summary>Stops a running auto-repeat, e.g. when focus moves to another window or view.</summary>
    public static void CancelRepeat() => _repeatKey = null;

    /// <summary>Offers <paramref name="stroke"/> to <paramref name="start"/> and then its ancestors, as for the focused element.</summary>
    public static bool RouteKey(VisualElement start, KeyStroke stroke)
    {
        for (VisualElement? el = start; el is not null; el = el.Parent)
        {
            if (el is IKeyInput target && target.OnKey(stroke))
                return true;
        }
        return false;
    }

    /// <summary>Gives <paramref name="text"/> to the first <see cref="IKeyInput"/> from <paramref name="start"/> upwards.</summary>
    public static bool RouteText(VisualElement start, string text)
    {
        for (VisualElement? el = start; el is not null; el = el.Parent)
        {
            if (el is IKeyInput target)
            {
                target.OnText(text);
                return true;
            }
        }
        return false;
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        // Blossom's handler ran first and toggled its overlay; undo that unless explicitly allowed.
        if (key == Key.F12 && !AllowDebugOverlay)
            Browser.ShowDebugOverlay = false;
        if (IsModifier(key) || key == Key.Unknown)
            return;

        _repeatKeyboard = keyboard;
        _repeatKey = key;
        _nextRepeatMs = Clock.ElapsedMilliseconds + RepeatDelayMs;
        Dispatch(new KeyStroke(key, ReadModifiers(keyboard)));
    }

    private static void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        if (_repeatKey == key)
            _repeatKey = null;
    }

    private static void Dispatch(KeyStroke stroke)
    {
        try
        {
            View? view = _app?.ActiveView;
            if (OperatingSystem.IsMacOS() && MacKeys.Translate(stroke, view is not null && FocusedElement(view) is ISendsControlKeys) is { } mac)
            {
                if (Shortcuts.TryHandle(mac.ShortcutFirst) || Shortcuts.TryHandle(mac.ShortcutSecond))
                    return;
                stroke = mac.ForFocused;
            }
            else if (Shortcuts.TryHandle(stroke))
            {
                return;
            }
            if (view is null)
                return;
            if (FocusedElement(view) is { } focused && RouteKey(focused, stroke))
                return;
            (view as IKeyInput)?.OnKey(stroke);
        }
        catch (Exception ex)
        {
            Log.Error($"Key handler failed for {stroke}: {ex}");
        }
    }

    private static void OnCodepoint(uint codepoint)
    {
        // Control characters arrive as key strokes; C1 controls and invalid scalars are never text.
        if (codepoint < 0x20 || codepoint is >= 0x7F and < 0xA0 || !Rune.IsValid(codepoint))
            return;
        string text = new Rune(codepoint).ToString();
        try
        {
            View? view = _app?.ActiveView;
            if (view is null)
                return;
            if (FocusedElement(view) is { } focused && RouteText(focused, text))
                return;
            (view as IKeyInput)?.OnText(text);
        }
        catch (Exception ex)
        {
            Log.Error($"Text handler failed: {ex}");
        }
    }

    private static VisualElement? FocusedElement(View view)
    {
        VisualElement? focused = view.ActiveKeyboardElement;
        if (focused is not null && (focused.IsDisposed || !focused.EffectiveVisible))
        {
            view.SetActiveKeyboardElement(null);
            return null;
        }
        return focused;
    }

    private static KeyModifiers ReadModifiers(IKeyboard k)
    {
        var mods = KeyModifiers.None;
        if (k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight)) mods |= KeyModifiers.Ctrl;
        if (k.IsKeyPressed(Key.AltLeft) || k.IsKeyPressed(Key.AltRight)) mods |= KeyModifiers.Alt;
        if (k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight)) mods |= KeyModifiers.Shift;
        if (k.IsKeyPressed(Key.SuperLeft) || k.IsKeyPressed(Key.SuperRight)) mods |= KeyModifiers.Super;
        return mods;
    }

    private static bool IsModifier(Key key) => key is Key.ControlLeft or Key.ControlRight or Key.AltLeft or Key.AltRight
        or Key.ShiftLeft or Key.ShiftRight or Key.SuperLeft or Key.SuperRight or Key.CapsLock or Key.NumLock or Key.ScrollLock;

    // Silk's IKeyboard.KeyChar casts the codepoint to char, mangling emoji and other astral characters.
    // Take GLFW's character callback over (chaining to Silk's, so Blossom still sees its events).
    private static unsafe bool TryHookGlfwChars()
    {
        try
        {
            WindowHandle* handle = AppWindow.Handle;
            if (handle is null)
                return false;
            _charCallback = OnGlfwChar;
            _previousCharCallback = GlfwProvider.GLFW.Value.SetCharCallback(handle, _charCallback);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not hook the GLFW character callback: {ex.Message}");
            return false;
        }
    }

    private static unsafe void OnGlfwChar(WindowHandle* window, uint codepoint)
    {
        _previousCharCallback?.Invoke(window, codepoint);
        OnCodepoint(codepoint);
    }
}
