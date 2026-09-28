using Blossom.Core.Visual;

namespace TGK.Client.Controls;

/// <summary>Helpers for "caption above control" form rows.</summary>
public static class Form
{
    public const float CaptionH = 16;
    public const float CaptionGap = 6;
    public const float RowGap = 16;

    public static Label Caption(string text) => new(text, Theme.FontSm, Theme.TextSecondary, Theme.WeightSemibold);

    /// <summary>Places <paramref name="caption"/> and <paramref name="control"/> (parent-local); returns the bottom of the control.</summary>
    public static float Place(Label caption, VisualElement control, float x, float y, float w, float h = Theme.FieldHeight)
    {
        caption.Transform.SetLocalFrame(x, y, w, CaptionH);
        control.Transform.SetLocalFrame(x, y + CaptionH + CaptionGap, w, h);
        return y + CaptionH + CaptionGap + h;
    }

    /// <summary>Height of one caption + control row.</summary>
    public static float RowHeight(float controlH = Theme.FieldHeight) => CaptionH + CaptionGap + controlH;
}
