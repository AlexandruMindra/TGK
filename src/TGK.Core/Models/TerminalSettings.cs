namespace TGK.Core.Models;

public sealed class TerminalSettings
{
    public const string CursorBlock = "block";
    public const string CursorUnderline = "underline";
    public const string CursorBar = "bar";

    public float FontSize { get; set; } = 14;
    public int ScrollbackLines { get; set; } = 10000;

    /// <summary>One of <see cref="CursorBlock"/>, <see cref="CursorUnderline"/>, <see cref="CursorBar"/>.</summary>
    public string CursorShape { get; set; } = CursorBlock;

    public bool CursorBlink { get; set; } = true;
    public bool CopyOnSelect { get; set; }

    public TerminalSettings Clone() => (TerminalSettings)MemberwiseClone();
}
