namespace TGK.Protocol;

public static class ProtocolConstants
{
    public const string ServerName = "tgk-server";
    public const int Version = 1;

    public const int MaxChangesPerRequest = 500;

    /// <summary>Largest sealed item payload, in bytes (after base64 decoding).</summary>
    public const int MaxItemBytes = 256 * 1024;

    public const int MaxRequestBytes = 8 * 1024 * 1024;
    public const int MaxItemIdLength = 64;
}
