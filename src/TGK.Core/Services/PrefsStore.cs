using System;
using System.IO;
using System.Text.Json;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>Loads and saves <see cref="ClientPrefs"/> as <c>prefs.json</c> in the per-user config directory.</summary>
public sealed class PrefsStore
{
    public const string FileName = "prefs.json";

    /// <param name="baseDirectory">Directory holding the file; defaults to <see cref="AppPaths.ConfigDirectory"/>.</param>
    public PrefsStore(string? baseDirectory = null)
    {
        FilePath = Path.Combine(baseDirectory ?? AppPaths.ConfigDirectory, FileName);
    }

    public string FilePath { get; }

    /// <summary>Returns the saved preferences, or defaults when the file is missing or unreadable.</summary>
    public ClientPrefs Load()
    {
        try
        {
            ClientPrefs? prefs = TgkJson.ReadFile<ClientPrefs>(FilePath);
            if (prefs is null)
                return new ClientPrefs();
            prefs.Terminal ??= new TerminalSettings();
            return prefs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ClientPrefs();
        }
    }

    /// <summary>Writes the preferences atomically. Throws <see cref="IOException"/> on failure.</summary>
    public void Save(ClientPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        TgkJson.WriteFileAtomic(FilePath, prefs);
    }
}
