using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TGK.Client.Main;
using TGK.Core.Agents;
using TGK.Core.Models;
using TGK.Core.Ssh;
using TGK.Terminal;

namespace TGK.Client.Agents;

/// <summary>
/// An SSH terminal tab as agents use it (<see cref="IAgentTerminal"/>): reads and typing go to the UI thread, the
/// connection is the tab's own. Made on the UI thread by <see cref="AgentService.TerminalsAsync"/>, used on workers.
/// </summary>
internal sealed class SessionAgentTerminal(SessionTabContent tab) : IAgentTerminal
{
    // Output that already arrived is parsed over the next frames; a read waits this long at most for it.
    private const int SettleSteps = 20;
    private const int SettleStepMs = 25;

    public string Id { get; } = tab.AgentId;

    public string Title { get; } = tab.Title;

    public HostEntry Host { get; } = tab.AgentHost;

    public bool IsConnected => tab.AgentSession is { State: SessionState.Connected };

    public long OutputVersion => tab.AgentOutputVersion;

    public async Task<TerminalSnapshot> ReadAsync(int scrollbackLines, CancellationToken ct)
    {
        for (int i = 0; i < SettleSteps && await UiThread.InvokeAsync(() => Task.FromResult(tab.HasPendingOutput)).WaitAsync(ct).ConfigureAwait(false); i++)
            await Task.Delay(SettleStepMs, ct).ConfigureAwait(false);
        return await UiThread.InvokeAsync(() => Task.FromResult(tab.AgentSnapshot(scrollbackLines))).WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> SendAsync(string text, IReadOnlyList<TerminalKeyPress> keys, CancellationToken ct) =>
        UiThread.InvokeAsync(() => Task.FromResult(tab.AgentType(text, keys))).WaitAsync(ct);

    public RemoteConnection? AttachConnection() => tab.AgentSession is { } session ? RemoteConnection.Attach(session) : null;

    /// <summary>The bytes a key sends in a terminal with <paramref name="modes"/> (cursor keys depend on them).</summary>
    public static byte[] Encode(TerminalKeyPress key, TerminalModes modes)
    {
        KeyMods mods = (key.Ctrl ? KeyMods.Ctrl : KeyMods.None) | (key.Alt ? KeyMods.Alt : KeyMods.None) | (key.Shift ? KeyMods.Shift : KeyMods.None);
        if (!key.IsNamed)
            return TerminalInput.EncodeChar(key.Key[0], mods);
        TermKey named = key.Key switch
        {
            "enter" => TermKey.Enter,
            "tab" => TermKey.Tab,
            "escape" => TermKey.Escape,
            "backspace" => TermKey.Backspace,
            "delete" => TermKey.Delete,
            "insert" => TermKey.Insert,
            "up" => TermKey.Up,
            "down" => TermKey.Down,
            "left" => TermKey.Left,
            "right" => TermKey.Right,
            "home" => TermKey.Home,
            "end" => TermKey.End,
            "pageup" => TermKey.PageUp,
            "pagedown" => TermKey.PageDown,
            "f1" => TermKey.F1,
            "f2" => TermKey.F2,
            "f3" => TermKey.F3,
            "f4" => TermKey.F4,
            "f5" => TermKey.F5,
            "f6" => TermKey.F6,
            "f7" => TermKey.F7,
            "f8" => TermKey.F8,
            "f9" => TermKey.F9,
            "f10" => TermKey.F10,
            "f11" => TermKey.F11,
            "f12" => TermKey.F12,
            _ => TermKey.Char,
        };
        return named == TermKey.Char ? [] : TerminalInput.EncodeKey(named, mods, modes);
    }
}
