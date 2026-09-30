namespace TGK.Protocol;

/// <summary>Values of <see cref="VaultItemPayload.Kind"/>.</summary>
public static class ItemKinds
{
    public const string Group = "group";
    public const string Host = "host";
    public const string Identity = "identity";
    public const string KnownHost = "knownHost";

    /// <summary>
    /// The vault's one settings item (connection defaults). Newer than the other kinds: clients skip items of a kind
    /// they don't know, so an older client ignores it and keeps working with the rest of the vault.
    /// </summary>
    public const string Settings = "settings";

    /// <summary>The vault's one workspace item ("reopen my tabs" and the saved tabs). Older clients skip it, as above.</summary>
    public const string Workspace = "workspace";
}
