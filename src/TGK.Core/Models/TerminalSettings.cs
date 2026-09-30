namespace TGK.Core.Models;

/// <summary>
/// How a terminal looks and behaves. Synced as part of <see cref="HostOptions"/> (see
/// <see cref="EffectiveOptions.Terminal"/>); <see cref="ClientPrefs.Terminal"/> only keeps what older versions saved on
/// the device, which is moved into the vault once.
/// </summary>
public sealed class TerminalSettings
{
    public const string CursorBlock = "block";
    public const string CursorUnderline = "underline";
    public const string CursorBar = "bar";

    public float FontSize { get; set; } = 14;

    /// <summary>Null: the bundled font (<see cref="EffectiveOptions.DefaultFontFamily"/>).</summary>
    public string? FontFamily { get; set; }

    public int ScrollbackLines { get; set; } = 10000;

    /// <summary>One of <see cref="CursorBlock"/>, <see cref="CursorUnderline"/>, <see cref="CursorBar"/>.</summary>
    public string CursorShape { get; set; } = CursorBlock;

    public bool CursorBlink { get; set; } = true;
    public bool CopyOnSelect { get; set; }

    public TerminalSettings Clone() => (TerminalSettings)MemberwiseClone();
}
