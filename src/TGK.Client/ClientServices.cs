using System;
using System.IO;
using Blossom;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client;

/// <summary>
/// The client's service instances, created once in <see cref="Program"/> and shared by all views
/// (reach them through <c>TgkView.Services</c> or <c>MainView.Services</c>).
/// </summary>
public sealed class ClientServices
{
    public ClientServices(RoutingVaultService vault, PrefsStore prefsStore, DevOptions dev)
    {
        Vault = vault;
        Migration = new VaultMigration(vault);
        PrefsStore = prefsStore;
        Dev = dev;
        Prefs = prefsStore.Load();
    }

    /// <summary>
    /// The user's vault: a server account or the local vault (<see cref="RoutingVaultService.Mode"/>). Its <c>Changed</c>
    /// event fires on any thread: marshal with <c>Shell.Post</c>.
    /// </summary>
    public RoutingVaultService Vault { get; }

    /// <summary>Moves data between the local vault and server accounts, and to and from backup files.</summary>
    public VaultMigration Migration { get; }

    /// <summary>The vault is the local one (this device only, nothing syncs).</summary>
    public bool IsLocal => Vault.Mode == VaultMode.Local;

    public PrefsStore PrefsStore { get; }

    /// <summary>Current per-device preferences. Read freely; change them only through <see cref="UpdatePrefs"/>.</summary>
    public ClientPrefs Prefs { get; }

    public DevOptions Dev { get; }

    /// <summary>Raised on the UI thread after <see cref="UpdatePrefs"/> (e.g. so terminals re-read <c>Prefs.Terminal</c>).</summary>
    public event Action? PrefsChanged;

    /// <summary>Applies <paramref name="edit"/> to <see cref="Prefs"/>, saves them and raises <see cref="PrefsChanged"/>. UI thread only.</summary>
    public void UpdatePrefs(Action<ClientPrefs> edit)
    {
        edit(Prefs);
        try
        {
            PrefsStore.Save(Prefs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save preferences: {ex.Message}");
        }
        PrefsChanged?.Invoke();
    }
}
