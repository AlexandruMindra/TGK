using System;
using System.Diagnostics.CodeAnalysis;

namespace TGK.Core.Services;

/// <summary>Parses the server address the user typed.</summary>
public static class ServerAddress
{
    /// <summary>True for <c>mock://</c> addresses, which the dev <see cref="MockVaultService"/> serves.</summary>
    public static bool IsMock(string? address) =>
        address is not null && address.TrimStart().StartsWith("mock://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes <paramref name="address"/> to <c>https://host[:port][/path]</c> (https is assumed when no scheme is
    /// given). Plain <c>http://</c> is accepted only for loopback hosts (localhost, 127.0.0.1, [::1]). On failure
    /// <paramref name="error"/> is a message for the user (logins report it as <see cref="VaultError.InsecureUrl"/>).
    /// </summary>
    public static bool TryParse(string? address, [NotNullWhen(true)] out Uri? baseUri, [NotNullWhen(false)] out string? error)
    {
        baseUri = null;
        string text = address?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Enter the server address.";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Enter a valid server address, e.g. https://tgk.example.com.";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "Plain http:// is only allowed for localhost. Use https:// so your vault travels encrypted.";
            return false;
        }

        baseUri = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        error = null;
        return true;
    }
}
