using System.Collections.Generic;
using Blossom.Core;
using Blossom.Core.Visual;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>
/// Base for TGK's custom-drawn controls: tracks hover/pressed state and paints through <see cref="Paint"/>
/// (element-local coordinates, replayed by Blossom whenever the area is repainted, so it must be idempotent).
/// </summary>
public abstract class Control : VisualElement
{
    private bool _enabled = true;

    protected Control()
    {
        Style = new ElementStyle();
        Events.OnMouseEnter += _ => SetHover(true);
        Events.OnMouseLeave += _ => { SetHover(false); SetPressed(false); };
        Events.OnMouseDown += (_, e) => { if (e.Button == 0) SetPressed(true); };
        Events.OnMouseUp += (_, _) => SetPressed(false);
    }

    public bool IsHovered { get; private set; }
    public bool IsPressed { get; private set; }

    /// <summary>Disabled controls ignore the mouse (clicks fall through to the parent) and paint dimmed.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Interactive = value;
            if (!value)
            {
                IsHovered = false;
                IsPressed = false;
            }
            InvalidatePaint();
        }
    }

    public float W => Transform.Computed.Width;
    public float H => Transform.Computed.Height;

    protected abstract void Paint(SKCanvas c);

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds) =>
        cmds.Add(new DrawCallbackCommand(c => { if (W > 0 && H > 0) Paint(c); }));

    private void SetHover(bool value)
    {
        if (IsHovered == value)
            return;
        IsHovered = value;
        OnHoverChanged();
        InvalidatePaint();
    }

    private void SetPressed(bool value)
    {
        if (IsPressed == value)
            return;
        IsPressed = value;
        InvalidatePaint();
    }

    protected virtual void OnHoverChanged() { }

    /// <summary>Sets <paramref name="field"/> and repaints when the value changed.</summary>
    protected bool SetAndPaint<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        InvalidatePaint();
        return true;
    }
}
