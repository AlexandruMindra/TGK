using System;
using System.Linq;
using Blossom.Core.Visual;
using Blossom.Core.Visual.Enums;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Views;

namespace TGK.Client.Dialogs;

/// <summary>
/// A scrollable dialog page: elements go into <see cref="Content"/> (via <see cref="Add{T}"/>) and <see cref="Arrange"/>
/// places them in content coordinates; the page scrolls when they are taller than the space the dialog has.
/// </summary>
public sealed class FormPage : ScrollContainer
{
    private const float ScrollbarRoom = 12;
    private VisualElement? _reveal;

    public FormPage()
    {
        OverflowX = OverflowMode.Clip;
        ScrollbarVisibilityX = ScrollbarVisibility.Hidden;
        ScrollbarThickness = 6;
        ScrollbarRadius = 3;
        ScrollbarThumbColor = Theme.BorderStrong;
        ScrollbarTrackColor = SKColors.Transparent;
        Style = new ElementStyle();
        Content = new VisualElement { Style = new ElementStyle() };
        AddChild(Content);
    }

    public VisualElement Content { get; }

    /// <summary>Positions the page's elements (content-local) for a width and returns their height.</summary>
    public Func<float, float>? Layout { get; set; }

    public T Add<T>(T element) where T : VisualElement
    {
        Content.AddChild(element);
        return element;
    }

    /// <summary>The content height at <paramref name="width"/> (lays the content out).</summary>
    public float Measure(float width) => Layout?.Invoke(width) ?? 0;

    /// <summary>Scrolls <paramref name="element"/> (in the content) into view on the next <see cref="Arrange"/>.</summary>
    public void Reveal(VisualElement element) => _reveal = element;

    /// <summary>Lays the content out for the page's current frame (set it first), leaving room for a scrollbar when needed.</summary>
    public void Arrange()
    {
        float width = Transform.Computed.Width, viewH = Transform.Computed.Height;
        Content.Transform.SetLocalFrame(0, 0, width, Math.Max(Content.Transform.Computed.Height, 1));
        float h = Layout?.Invoke(width) ?? 0;
        if (h > viewH + 0.5f)
            h = Layout?.Invoke(width - ScrollbarRoom) ?? 0;
        Content.Transform.SetLocalFrame(0, 0, width, h);
        InvalidateLayout();

        // Keep the focused field in view (e.g. a row just added at the bottom, or a field with an error).
        VisualElement? target = _reveal is { Visible: true } ? _reveal : ParentView?.ActiveKeyboardElement;
        _reveal = null;
        if (target is not null && Content.ContainsElement(target))
        {
            float top = target.Transform.Computed.Y - Content.Transform.Computed.Y;
            float bottom = top + target.Transform.Computed.Height;
            if (top < ScrollY)
                ScrollY = Math.Max(0, top - 8);
            else if (bottom > ScrollY + viewH)
                ScrollY = bottom - viewH + 8;
        }
    }
}

/// <summary>
/// A dialog with a row of tabs over one <see cref="FormPage"/> per tab. The pages share the height of the tallest
/// one (it grows as pages grow, never shrinks while open, so switching tabs does not resize the dialog) and scroll
/// when the window is too small. <see cref="ShowError"/> shows a validation message under the pages and switches to
/// the page it concerns. Ctrl+Tab / Ctrl+PageDown show the next tab and Ctrl+Shift+Tab / Ctrl+PageUp the previous one
/// (the main window's tab shortcuts are off while a dialog is open).
/// </summary>
public abstract class TabbedDialog : DialogBase
{
    private const float TabsH = 34, TabsGap = 16, ErrorGap = 10, ErrorH = 18;
    private readonly SegmentedControl _tabs;
    private readonly FormPage[] _pages;
    private readonly Label _error;
    private float _pageHeight;

    protected TabbedDialog(TgkView view, string title, float width, params string[] tabs) : base(view, title, width)
    {
        _tabs = AddBody(new SegmentedControl(tabs));
        _tabs.SelectionChanged += ShowTab;
        _pages = tabs.Select(_ => AddBody(new FormPage { Visible = false })).ToArray();
        _pages[0].Visible = true;
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { Visible = false });
    }

    protected int ActiveTab => _tabs.SelectedIndex;

    protected FormPage PageAt(int index) => _pages[index];

    public void ShowTab(int index)
    {
        index = Math.Clamp(index, 0, _pages.Length - 1);
        _tabs.SelectedIndex = index;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visible = i == index;
        OnTabShown(index);
        InvalidateLayout();
    }

    protected virtual void OnTabShown(int index) { }

    public override bool OnKey(KeyStroke k)
    {
        int step = (k.Key, k.Modifiers) switch
        {
            (Key.Tab or Key.PageDown, KeyModifiers.Ctrl) => 1,
            (Key.Tab, KeyModifiers.Ctrl | KeyModifiers.Shift) or (Key.PageUp, KeyModifiers.Ctrl) => -1,
            _ => 0,
        };
        if (step == 0)
            return base.OnKey(k);
        ShowTab((ActiveTab + step + _pages.Length) % _pages.Length);
        if (!FocusNavigator.FocusFirst(_pages[ActiveTab], byKeyboard: true))
            View.SetActiveKeyboardElement(this); // never leave the keys with a field of the hidden page
        return true;
    }

    /// <summary>Shows <paramref name="message"/> under the pages, switches to <paramref name="page"/> and focuses <paramref name="field"/>.</summary>
    protected void ShowError(string message, FormPage? page = null, TextField? field = null)
    {
        int index = page is null ? -1 : Array.IndexOf(_pages, page);
        if (index >= 0 && index != ActiveTab)
            ShowTab(index);
        _error.Text = message;
        _error.Visible = true;
        if (field is not null)
        {
            field.HasError = true;
            field.Focus();
        }
        InvalidateLayout();
    }

    protected void ClearError()
    {
        if (!_error.Visible)
            return;
        _error.Visible = false;
        InvalidateLayout();
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        _tabs.Transform.SetLocalFrame(left, top, width, TabsH);
        float y = top + TabsH + TabsGap;
        float errorH = _error.Visible ? ErrorGap + ErrorH : 0;
        _pageHeight = Math.Max(_pageHeight, _pages.Max(p => p.Measure(width)));
        float pageH = MathF.Round(Math.Max(140, Math.Min(_pageHeight, MaxBodyHeight - TabsH - TabsGap - errorH)));
        FormPage page = _pages[ActiveTab];
        page.Transform.SetLocalFrame(left, y, width, pageH);
        page.Arrange();
        y += pageH;
        if (_error.Visible)
        {
            _error.Transform.SetLocalFrame(left, y + ErrorGap, width, ErrorH);
            y += errorH;
        }
        return y - top;
    }
}
