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
    public TerminalSettings Terminal { get; set; } = new();

    public ClientPrefs Clone()
    {
        var copy = (ClientPrefs)MemberwiseClone();
        copy.Terminal = Terminal.Clone();
        return copy;
    }
}
