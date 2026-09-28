using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Blossom.Core;
using Blossom.Core.Design;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Platform;

namespace TGK.Client.Views;

/// <summary>
/// Base for TGK's screens: drives <see cref="KeyboardHub"/> auto-repeat and <see cref="UiClock"/> from the view loop,
/// owns the dialog stack, the shared popup menu and the toast, and is the last stop for unhandled keys.
/// </summary>
public abstract class TgkView : View, IKeyInput
{
    private readonly List<DialogBase> _dialogs = [];
    private readonly List<VisualElement> _fullWindow = [];
    private PopupMenu? _menu;
    private Toast? _toast;
    private bool _built, _showPending;

    protected TgkView(string name, ClientServices services) : base(name)
    {
        Services = services;
        BackColor = Theme.AppBg;
        Canvas = new DesignCanvas(1280, 800);
        Loop += OnLoop;
    }

    public ClientServices Services { get; }

    public TgkApplication App => (TgkApplication)Application;

    public bool IsActive => Application?.ActiveView == this;

    /// <summary>True while a modal dialog is open; global shortcuts should check this.</summary>
    public bool HasModal => _dialogs.Count > 0;

    public DialogBase? TopDialog => _dialogs.Count > 0 ? _dialogs[^1] : null;

    /// <summary>The view's popup menu (context menus, dropdown lists, the account menu).</summary>
    public PopupMenu Menu
    {
        get
        {
            if (_menu is null)
            {
                _menu = new PopupMenu();
                AddFullWindow(_menu);
            }
            return _menu;
        }
    }

    /// <summary>Shows a short, self-dismissing notification at the bottom of the window.</summary>
    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        if (_toast is null)
        {
            _toast = new Toast();
            AddElement(_toast);
        }
        _toast.Show(message, kind);
    }

    /// <summary>
    /// Runs a vault mutation. Validation errors (thrown synchronously) and failed pushes are shown as an error
    /// toast. Returns false when the call was rejected up front.
    /// </summary>
    public bool RunVault(Func<Task> operation)
    {
        Task task;
        try
        {
            task = operation();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowToast(ex.Message, ToastKind.Error);
            return false;
        }
        task.ContinueWith(t => UiThread.Post(() => ShowToast(t.Exception?.GetBaseException().Message ?? "The change could not be saved.", ToastKind.Error)),
            TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    /// <summary>Root element whose tab stops Tab / Shift+Tab cycles through when no dialog is open.</summary>
    protected abstract VisualElement? FocusScope { get; }

    /// <summary>
    /// Element that receives keys and text typed while nothing is focused (e.g. after a click on an empty area): it
    /// takes the focus, and the key goes to it and its ancestors as usual.
    /// </summary>
    protected virtual VisualElement? DefaultFocus => null;

    public sealed override void Init()
    {
        KeyboardHub.Install(Application);
        AppWindow.HookResize();
        Build();
        _built = true;
        if (_showPending)
        {
            _showPending = false;
            OnShown();
        }
    }

    /// <summary>Builds the element tree (runs once, on first activation).</summary>
    protected abstract void Build();

    // Blossom activates the first view before it is built (Init runs once the window exists), so OnShown is
    // deferred until Build has run.
    public sealed override void OnActivated()
    {
        base.OnActivated();
        if (_built)
            OnShown();
        else
            _showPending = true;
    }

    /// <summary>The view became active (and is built).</summary>
    protected virtual void OnShown() => FitToWindow();

    /// <summary>
    /// Adds a root element that always covers the whole window (screen roots, dialogs, popups). Blossom's anchors
    /// capture the parent size at attach time, which is unreliable for roots, so these are sized explicitly.
    /// </summary>
    public void AddFullWindow(VisualElement element)
    {
        AddElement(element);
        _fullWindow.Add(element);
        Fit(element);
    }

    /// <summary>Stops tracking <paramref name="element"/> (call before disposing it).</summary>
    public void RemoveFullWindow(VisualElement element) => _fullWindow.Remove(element);

    /// <summary>Resizes the full-window roots to the current window size (on show and on every resize).</summary>
    internal void FitToWindow()
    {
        foreach (VisualElement element in _fullWindow)
            Fit(element);
    }

    private void Fit(VisualElement element)
    {
        element.Transform.SetAbsoluteFrame(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        element.InvalidateLayout();
    }

    internal int PushDialog(DialogBase dialog)
    {
        _dialogs.Add(dialog);
        return _dialogs.Count;
    }

    internal void RemoveDialog(DialogBase dialog) => _dialogs.Remove(dialog);

    public override void OnDeactivated()
    {
        KeyboardHub.CancelRepeat();
        base.OnDeactivated();
    }

    private void OnLoop()
    {
        KeyboardHub.Tick();
        UiClock.RaiseTick();
    }

    public virtual bool OnKey(KeyStroke key)
    {
        if (TopDialog is { } dialog)
            return dialog.OnKey(key);
        if (key.Key == Key.Tab && (key.Modifiers & ~KeyModifiers.Shift) == 0 && FocusScope is { } scope)
            return FocusNavigator.Move(scope, key.Shift);
        if (ActiveKeyboardElement is null && DefaultFocus is { EffectiveVisible: true } element)
        {
            SetActiveKeyboardElement(element);
            return KeyboardHub.RouteKey(element, key);
        }
        return false;
    }

    public virtual void OnText(string text)
    {
        if (TopDialog is null && ActiveKeyboardElement is null && DefaultFocus is { EffectiveVisible: true } element)
        {
            SetActiveKeyboardElement(element);
            KeyboardHub.RouteText(element, text);
        }
    }
}
