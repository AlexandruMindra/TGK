using System;
using TGK.Core.Services;

namespace TGK.Core.Models;

/// <summary>Per-device UI preferences. Never contains passwords or other secrets.</summary>
public sealed class ClientPrefs
{
    /// <summary>The login screen to open with: <see cref="VaultMode.Server"/> or <see cref="VaultMode.Local"/> (never Mock).</summary>
    public VaultMode Mode { get; set; } = VaultMode.Server;

    public string? LastServer { get; set; }
    public string? LastUsername { get; set; }
    /// <summary>State of the login form's "Keep me signed in" box.</summary>
    public bool KeepSignedIn { get; set; } = true;
    /// <summary>State of the local unlock form's "Keep unlocked on this device" box.</summary>
    public bool KeepLocalUnlocked { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public float SidebarWidth { get; set; } = 260;
    /// <summary>Terminal settings saved by versions before 0.2.1; they now live in the vault (<see cref="HostOptions"/>).</summary>
    public TerminalSettings Terminal { get; set; } = new();

    /// <summary><see cref="Terminal"/> has been moved into the vault (done once per device, see <see cref="HostOptions.WithLocalTerminal"/>).</summary>
    public bool TerminalInVault { get; set; }

    /// <summary>Look for newer TGK releases on GitHub (in the background, about twice a day) and mention them in the status bar.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Stable releases or nightly builds too; null follows the installed build (see <see cref="UpdateChecker.DefaultChannel"/>).</summary>
    public UpdateChannel? UpdateChannel { get; set; }

    /// <summary>When GitHub was last asked, and the newer version it reported then (null: none), with its release page.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string? AvailableUpdate { get; set; }
    public string? AvailableUpdateUrl { get; set; }

    /// <summary>A version the user chose to skip: it is not mentioned again (a later one is).</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>Why the update downloaded in the last run could not be installed when TGK closed; shown once, then cleared.</summary>
    public string? UpdateError { get; set; }

    public ClientPrefs Clone()
    {
        var copy = (ClientPrefs)MemberwiseClone();
        copy.Terminal = Terminal.Clone();
        return copy;
    }
}
