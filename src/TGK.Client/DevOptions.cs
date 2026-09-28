using System;
using System.Globalization;
using System.Text;

namespace TGK.Client;

/// <summary>Developer command-line flags (harmless in production: nothing happens unless they are passed).</summary>
public sealed class DevOptions
{
    public const string DevUser = "demo";
    public const string DevPassword = "demo";
    public const string DevServer = "mock://localhost";

    /// <summary><c>--dev-login</c>: sign in to the mock server as <see cref="DevUser"/> without showing the login form.</summary>
    public bool AutoLogin { get; private init; }

    /// <summary><c>--scene=&lt;name&gt;</c>: open a UI state directly (for screenshots). See <see cref="Scenes"/>.</summary>
    public string? Scene { get; private init; }

    /// <summary><c>--window=WxH</c>: initial window size.</summary>
    public (int Width, int Height)? WindowSize { get; private init; }

    /// <summary><c>--fps</c>: show Blossom's debug overlay (F12 toggles it).</summary>
    public bool DebugOverlay { get; private init; }

    /// <summary>
    /// <c>--dev-connect=user[:password]@host[:port]</c>: open a session tab to that address after sign-in
    /// (without a password the password prompt appears). For end-to-end checks against a test server.
    /// </summary>
    public string? Connect { get; private init; }

    /// <summary><c>--dev-send=&lt;text&gt;</c>: typed into the <c>--dev-connect</c> session once it is connected; \r \n \t \e \\ are unescaped.</summary>
    public string? Send { get; private init; }

    public static readonly string[] Scenes = ["login", "main", "host-editor", "identities", "settings", "hostkey", "hostkey-changed", "password-prompt", "menu"];

    public static DevOptions Parse(string[] args)
    {
        bool autoLogin = false, overlay = false;
        string? scene = null, connect = null, send = null;
        (int, int)? size = null;
        foreach (string arg in args)
        {
            if (arg == "--dev-login")
                autoLogin = true;
            else if (arg == "--fps")
                overlay = true;
            else if (arg.StartsWith("--scene=", StringComparison.Ordinal))
                scene = arg["--scene=".Length..].Trim().ToLowerInvariant();
            else if (arg.StartsWith("--window=", StringComparison.Ordinal) && TryParseSize(arg["--window=".Length..], out (int, int) s))
                size = s;
            else if (arg.StartsWith("--dev-connect=", StringComparison.Ordinal))
                connect = arg["--dev-connect=".Length..].Trim();
            else if (arg.StartsWith("--dev-send=", StringComparison.Ordinal))
                send = Unescape(arg["--dev-send=".Length..]);
        }
        if (scene is not null && Array.IndexOf(Scenes, scene) < 0)
        {
            Console.Error.WriteLine($"Unknown scene '{scene}'. Known: {string.Join(", ", Scenes)}");
            scene = null;
        }
        // Every scene except the login form, and a dev connection, need a signed-in vault.
        autoLogin |= (scene is not null && scene != "login") || connect is not null;
        return new DevOptions { AutoLogin = autoLogin, Scene = scene, WindowSize = size, DebugOverlay = overlay, Connect = connect, Send = send };
    }

    /// <summary>Splits <see cref="Connect"/> into the address for quick connect and the optional password.</summary>
    public (string Address, string? Password)? ParseConnect()
    {
        if (string.IsNullOrEmpty(Connect))
            return null;
        int at = Connect.LastIndexOf('@');
        int colon = at > 0 ? Connect.IndexOf(':', 0, at) : -1;
        return colon < 0 ? (Connect, null) : (Connect[..colon] + Connect[at..], Connect[(colon + 1)..at]);
    }

    private static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length)
            {
                sb.Append(text[i]);
                continue;
            }
            char next = text[++i];
            sb.Append(next switch { 'r' => '\r', 'n' => '\n', 't' => '\t', 'e' => '\x1b', _ => next });
        }
        return sb.ToString();
    }

    private static bool TryParseSize(string text, out (int, int) size)
    {
        size = default;
        string[] parts = text.ToLowerInvariant().Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            || w < 200 || h < 200)
            return false;
        size = (w, h);
        return true;
    }
}
