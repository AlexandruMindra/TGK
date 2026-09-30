using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TGK.Core.Models;

namespace TGK.Client.Main;

/// <summary>
/// Parses the new-tab page's quick-connect text: <c>user@host</c>, <c>user@host:port</c>, <c>user@[v6]:port</c>, a
/// saved host's name, or a pasted command such as <c>ssh -p 2222 -l root host</c>; and searches the saved hosts
/// for what is typed so far.
/// </summary>
public static class QuickConnect
{
    /// <summary>
    /// Saved hosts matching <paramref name="query"/> by name, user and/or host: every word must match. A word with
    /// "@" matches the user before it and the name or host after it (either part may be left out, e.g. <c>root@</c>
    /// or <c>@10.0</c>); other words may appear anywhere in the name, <c>user@host:port</c> or group. A leading
    /// <c>ssh</c> and <c>-p</c>/<c>-l</c> options are ignored. Names starting with the query come first, then
    /// addresses starting with it, then the rest by name. Empty for an empty query.
    /// </summary>
    public static List<HostEntry> Search(string query, VaultData vault)
    {
        List<string> words = SearchWords(query);
        if (words.Count == 0)
            return [];
        string first = words[0].TrimStart('@');
        return vault.Hosts
            .Where(host => words.All(word => Matches(host, word, vault)))
            .OrderBy(host => host.DisplayName.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? 0
                : HostFormat.Address(host, vault).StartsWith(words[0], StringComparison.OrdinalIgnoreCase)
                    || host.Host.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(host => host.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The saved host with the same address, port and login name as <paramref name="entry"/> (e.g. typed as user@host), or null.</summary>
    public static HostEntry? FindSaved(HostEntry entry, VaultData vault) => vault.Hosts.Find(host =>
        string.Equals(host.Host.Trim(), entry.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && host.Port == entry.Port
        && string.Equals(HostFormat.UserOf(host, vault), entry.Username?.Trim(), StringComparison.Ordinal));

    private static List<string> SearchWords(string query)
    {
        string[] parts = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            string word = parts[i];
            if (i == 0 && string.Equals(word, "ssh", StringComparison.OrdinalIgnoreCase))
                continue;
            if (word.Length >= 2 && word[0] == '-' && word[1] is 'p' or 'l')
            {
                if (word.Length == 2)
                    i++; // the option's value
                continue;
            }
            if (word.StartsWith('-'))
                continue;
            words.Add(word);
        }
        return words;
    }

    private static bool Matches(HostEntry host, string word, VaultData vault)
    {
        int at = word.IndexOf('@');
        if (at >= 0)
        {
            string user = word[..at], place = word[(at + 1)..];
            string? login = HostFormat.UserOf(host, vault);
            bool userOk = user.Length == 0 || (login?.Contains(user, StringComparison.OrdinalIgnoreCase) ?? false);
            bool placeOk = place.Length == 0 || host.DisplayName.Contains(place, StringComparison.OrdinalIgnoreCase)
                || (host.Port == 22 ? host.Host : $"{host.Host}:{host.Port}").Contains(place, StringComparison.OrdinalIgnoreCase);
            return userOk && placeOk;
        }
        return host.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)
            || HostFormat.Address(host, vault).Contains(word, StringComparison.OrdinalIgnoreCase)
            || (vault.FindGroup(host.GroupId)?.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false);
    }

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
