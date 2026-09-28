using System;
using System.Collections.Generic;
using Silk.NET.Input;

namespace TGK.Client.Input;

/// <summary>Application-wide keyboard shortcuts; consulted by <see cref="KeyboardHub"/> before the focused element.</summary>
public sealed class ShortcutRegistry
{
    private readonly List<Entry> _entries = [];

    /// <summary>
    /// Registers <paramref name="action"/> for exactly <paramref name="modifiers"/>+<paramref name="key"/>.
    /// <paramref name="when"/> gates it (e.g. "my view is active and no dialog is open"); a gated-off shortcut
    /// lets the key through to the focused element. Dispose the result to unregister.
    /// </summary>
    public IDisposable Register(KeyModifiers modifiers, Key key, Action action, Func<bool>? when = null, bool repeats = false)
    {
        var entry = new Entry(modifiers, key, action, when, repeats);
        _entries.Add(entry);
        return new Registration(() => _entries.Remove(entry));
    }

    /// <summary>Runs the first enabled shortcut matching <paramref name="stroke"/>; returns whether one ran.</summary>
    public bool TryHandle(KeyStroke stroke)
    {
        foreach (Entry e in _entries.ToArray())
        {
            if (e.Key != stroke.Key || e.Modifiers != stroke.Modifiers)
                continue;
            if (e.When is not null && !e.When())
                continue;
            // A held shortcut that does not repeat is still swallowed, so it never leaks to the focused element.
            if (!stroke.IsRepeat || e.Repeats)
                e.Action();
            return true;
        }
        return false;
    }

    private sealed record Entry(KeyModifiers Modifiers, Key Key, Action Action, Func<bool>? When, bool Repeats);

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
