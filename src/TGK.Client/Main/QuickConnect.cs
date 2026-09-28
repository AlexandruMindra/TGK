using System;
using System.Globalization;
using TGK.Core.Models;

namespace TGK.Client.Main;

/// <summary>
/// Parses the new-tab page's quick-connect text: <c>user@host</c>, <c>user@host:port</c>, <c>user@[v6]:port</c>, a
/// saved host's name, or a pasted command such as <c>ssh -p 2222 -l root host</c>.
/// </summary>
public static class QuickConnect
{
    /// <summary>Returns a saved host (exact name match) or an unsaved ad-hoc entry; null with <paramref name="error"/> when invalid.</summary>
    public static HostEntry? Parse(string input, VaultData vault, out string? error)
    {
        error = null;
        string text = input.Trim();
        if (text.Length == 0)
        {
            error = "Type user@host or the name of a saved host.";
            return null;
        }

        HostEntry? saved = vault.Hosts.Find(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase));
        if (saved is not null)
            return saved;

        if (!TryParseCommand(text, out string destination, out string? optionUser, out int? optionPort, out error))
            return null;

        int at = destination.LastIndexOf('@');
        string user = optionUser ?? (at > 0 ? destination[..at] : "");
        string address = at >= 0 ? destination[(at + 1)..] : destination;
        int port = 22;
        string host = address;
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']');
            if (close < 0)
            {
                error = "Missing ']' in the IPv6 address.";
                return null;
            }
            host = address[1..close];
            string rest = address[(close + 1)..];
            if (rest.StartsWith(':') && !TryPort(rest[1..], out port, out error))
                return null;
        }
        else if (address.AsSpan().Count(':') == 1)
        {
            int colon = address.IndexOf(':');
            host = address[..colon];
            if (!TryPort(address[(colon + 1)..], out port, out error))
                return null;
        }

        if (host.Length == 0 || host.AsSpan().IndexOfAny("/\\") >= 0)
        {
            error = "That doesn't look like a host name or address.";
            return null;
        }
        if (user.Length == 0)
        {
            error = $"Include a username, e.g. root@{host}.";
            return null;
        }
        return new HostEntry { Host = host, Port = optionPort ?? port, Username = user };
    }

    /// <summary>
    /// Splits the input into the destination and the options an <c>ssh</c> command line may carry (<c>-p port</c>,
    /// <c>-l user</c>, also written <c>-p2222</c>). A leading <c>ssh</c> is optional; other options are rejected
    /// rather than silently ignored.
    /// </summary>
    private static bool TryParseCommand(string text, out string destination, out string? user, out int? port, out string? error)
    {
        destination = "";
        user = null;
        port = null;
        error = null;
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int first = string.Equals(words[0], "ssh", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        for (int i = first; i < words.Length; i++)
        {
            string word = words[i];
            if (word.Length >= 2 && word[0] == '-' && word[1] is 'p' or 'l')
            {
                string value = word.Length > 2 ? word[2..] : i + 1 < words.Length ? words[++i] : "";
                if (value.Length == 0)
                {
                    error = $"{word[..2]} needs a value.";
                    return false;
                }
                if (word[1] == 'l')
                {
                    user = value;
                }
                else
                {
                    if (!TryPort(value, out int p, out error))
                        return false;
                    port = p;
                }
            }
            else if (word.StartsWith('-'))
            {
                error = $"The option {word} isn't supported here. Save a host to set more options.";
                return false;
            }
            else if (destination.Length == 0)
            {
                destination = word;
            }
            else
            {
                error = "Only a destination can be given here, not a remote command.";
                return false;
            }
        }
        if (destination.Length == 0)
        {
            error = "Type user@host or the name of a saved host.";
            return false;
        }
        return true;
    }

    private static bool TryPort(string text, out int port, out string? error)
    {
        error = null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535)
            return true;
        error = "The port must be a number between 1 and 65535.";
        return false;
    }
}
