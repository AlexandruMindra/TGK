namespace TGK.Client.Input;

/// <summary>
/// Implemented by elements (or views) that take keyboard input from <see cref="KeyboardHub"/>.
/// Controls must use this instead of Blossom's <c>Events.OnKeyDown/OnKeyType</c>, which lose Ctrl/Alt combos
/// and key repeat; subscribing to both would deliver keys twice.
/// </summary>
public interface IKeyInput
{
    /// <summary>
    /// A key press or auto-repeat. Return true when handled; otherwise it bubbles to the next
    /// <see cref="IKeyInput"/> ancestor and finally to the view. Printable keys also arrive as
    /// <see cref="OnText"/>, so ignore them here unless a modifier makes them a command.
    /// </summary>
    bool OnKey(KeyStroke key);

    /// <summary>Typed text: one Unicode scalar value (astral characters arrive as a surrogate pair).</summary>
    void OnText(string text);
}

/// <summary>An element that Tab / Shift+Tab can move focus to.</summary>
public interface IFocusable
{
    bool IsTabStop { get; }

    /// <summary>Called when focus arrives via the keyboard (Tab), e.g. to select all text.</summary>
    void OnTabFocus();
}
