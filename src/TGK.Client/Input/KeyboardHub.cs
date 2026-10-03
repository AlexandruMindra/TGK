using System;
using System.Text;
using Blossom;
using Blossom.Core;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Silk.NET.Input;

namespace TGK.Client.Input;

/// <summary>
/// The app's single keyboard entry point. It takes Blossom's application-level key and text events (key presses
/// with their modifiers, the OS's auto-repeat, and full Unicode text) and dispatches them:
/// <list type="number">
/// <item><see cref="Shortcuts"/> (global shortcuts),</item>
/// <item>the focused element (<see cref="View.ActiveKeyboardElement"/>) and then its ancestors, for each that implements <see cref="IKeyInput"/>,</item>
/// <item>the active view, when it implements <see cref="IKeyInput"/>.</item>
/// </list>
/// Every key is marked handled, so Blossom's own hotkeys and Tab navigation never act on it. All callbacks run on
/// the UI thread.
/// </summary>
public static class KeyboardHub
{
    private static Application? _app;

    public static ShortcutRegistry Shortcuts { get; } = new();

    public static bool IsInstalled => _app is not null;

    /// <summary>Modifiers held right now (e.g. for Shift+click).</summary>
    public static KeyModifiers CurrentModifiers
    {
        get
        {
            if (_app is not { } app)
                return KeyModifiers.None;
            EventMap events = app.Events;
            return ToModifiers(events.IsControlDown, events.IsAltDown, events.IsShiftDown, events.IsSuperDown);
        }
    }

    /// <summary>Subscribes to the application's keyboard events. Call once, on the UI thread, from the first view's <c>Init</c>.</summary>
    public static void Install(Application app)
    {
        if (_app is not null)
            return;
        _app = app;
        app.Events.OnKeyDown += OnKeyDown;
        app.Events.OnTextInput += OnTextInput;
    }

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

    private static void OnKeyDown(KeyEvent e)
    {
        e.Handled = true;
        if (IsModifier(e.Key) || e.Key == Key.Unknown)
            return;
        Dispatch(new KeyStroke(e.Key, ToModifiers(e.Control, e.Alt, e.Shift, e.Super), e.IsRepeat));
    }

    private static void OnTextInput(TextEvent e)
    {
        e.Handled = true;
        foreach (Rune rune in e.Text.EnumerateRunes())
            OnCodepoint((uint)rune.Value);
    }

    private static void Dispatch(KeyStroke stroke)
    {
        try
        {
            View? view = _app?.ActiveView;
            // The element the keys go to: the focused one or, with nothing focused, the view's default (a tab's terminal).
            VisualElement? target = view is null ? null : FocusedElement(view) ?? (view as Views.TgkView)?.UnfocusedKeyTarget;
            if (OperatingSystem.IsMacOS() && MacKeys.Translate(stroke, target is ISendsControlKeys) is { } mac)
            {
                if (Shortcuts.TryHandle(mac.ShortcutFirst) || Shortcuts.TryHandle(mac.ShortcutSecond))
                    return;
                if (mac.ForTarget is not { } forTarget)
                    return;
                stroke = forTarget;
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

    private static KeyModifiers ToModifiers(bool ctrl, bool alt, bool shift, bool super)
    {
        var mods = KeyModifiers.None;
        if (ctrl) mods |= KeyModifiers.Ctrl;
        if (alt) mods |= KeyModifiers.Alt;
        if (shift) mods |= KeyModifiers.Shift;
        if (super) mods |= KeyModifiers.Super;
        return mods;
    }

    private static bool IsModifier(Key key) => key is Key.ControlLeft or Key.ControlRight or Key.AltLeft or Key.AltRight
        or Key.ShiftLeft or Key.ShiftRight or Key.SuperLeft or Key.SuperRight or Key.CapsLock or Key.NumLock or Key.ScrollLock;
}
