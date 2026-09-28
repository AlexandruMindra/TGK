namespace TGK.Core.Models;

/// <summary>Per-device UI preferences. Never contains passwords or other secrets.</summary>
public sealed class ClientPrefs
{
    public string? LastServer { get; set; }
    public string? LastUsername { get; set; }
    public bool RememberLogin { get; set; }
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
