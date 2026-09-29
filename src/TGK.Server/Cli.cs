using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TGK.Protocol;

namespace TGK.Server;

/// <summary>Command-line entry: <c>serve</c> (the default) or an admin command run directly against the database.</summary>
public static class Cli
{
    private const string Usage =
        """
        tgk-server - TGK sync server

        Usage:
          tgk-server [serve] [--urls <urls>] [--db <path>] [--cert <pfx> [--cert-password <pw>]] [--trust-proxy] [--rate-limit <n>]
          tgk-server users                          List accounts
          tgk-server user create <name>             Invite a user: prints a one-time code they use to create the account in the app
          tgk-server user show <name>               Account details and signed-in devices
          tgk-server user disable|enable <name>     Block or allow sign-in (disable also signs out every device)
          tgk-server user delete <name> [--yes]     Delete an account and all of its synced data (or a pending invite)
          tgk-server user reset-totp <name>         Require a new authenticator at the next sign-in
          tgk-server user revoke-sessions <name>    Sign out every device
          tgk-server registration on|off|status     Allow or refuse self-registration from clients

        Options:
          --db <path>          SQLite database (default ./data/tgk.db, env TGK_DB); used by every command
          --urls <urls>        Listen URLs, ';'-separated (default http://127.0.0.1:5080, env TGK_URLS)
          --cert <pfx>         Serve HTTPS with this PKCS#12 certificate (use https:// URLs)
          --trust-proxy        Honour X-Forwarded-For/Proto from a reverse proxy on loopback
          --rate-limit <n>     Auth requests per minute per client IP (default 20)
        """;

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        var positional = new List<string>();
        var hostArgs = new List<string>();
        string dbPath = Environment.GetEnvironmentVariable("TGK_DB") is { Length: > 0 } envDb ? envDb : ServerOptions.DefaultDbPath;
        string urls = Environment.GetEnvironmentVariable("TGK_URLS") is { Length: > 0 } envUrls ? envUrls : ServerOptions.DefaultUrls;
        string? cert = null, certPassword = null;
        bool trustProxy = false, yes = false;
        int rateLimit = ServerOptions.DefaultRateLimit;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h" or "--help":
                        output.WriteLine(Usage);
                        return 0;
                    case "--db": dbPath = Value(); break;
                    case "--urls": urls = Value(); break;
                    case "--cert": cert = Value(); break;
                    case "--cert-password": certPassword = Value(); break;
                    case "--trust-proxy": trustProxy = true; break;
                    case "--yes" or "-y": yes = true; break;
                    case "--rate-limit":
                        rateLimit = int.TryParse(Value(), out int n) && n > 0 ? n : throw new UsageException("--rate-limit needs a positive number.");
                        break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('='))
                            hostArgs.Add(arg); // ASP.NET configuration, e.g. --Logging:LogLevel:Default=Debug
                        else if (arg.StartsWith('-'))
                            throw new UsageException($"Unknown option '{arg}'.");
                        else
                            positional.Add(arg);
                        break;
                }

                string Value() => ++i < args.Length ? args[i] : throw new UsageException($"{arg} needs a value.");
            }

            if (positional is ["help"])
            {
                output.WriteLine(Usage);
                return 0;
            }
            if (positional.Count == 0 || positional[0] == "serve")
            {
                if (positional.Count > 1)
                    throw new UsageException($"Unexpected argument '{positional[1]}'.");
                ServerApp.Run(new ServerOptions
                {
                    DbPath = dbPath, Urls = urls, CertPath = cert, CertPassword = certPassword, TrustProxy = trustProxy, RateLimitPerMinute = rateLimit,
                }, hostArgs.ToArray());
                return 0;
            }

            var db = new Db(dbPath);
            if (db.IsNew)
                error.WriteLine($"Created a new database at {db.Path}.");
            return Admin(positional, new Store(db, TimeProvider.System), yes, input, output, error);
        }
        catch (UsageException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine("Run 'tgk-server --help' for usage.");
            return 2;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static int Admin(List<string> args, Store store, bool yes, TextReader input, TextWriter output, TextWriter error)
    {
        switch (args)
        {
            case ["users"]:
                var users = store.ListUsers();
                var invites = store.ListInvites();
                if (users.Count == 0 && invites.Count == 0)
                {
                    output.WriteLine("No users.");
                    return 0;
                }
                WriteTable(output, ["USERNAME", "STATUS", "AUTHENTICATOR", "ITEMS", "SESSIONS", "LAST SEEN", "CREATED"],
                    users.Select(u => new[]
                    {
                        u.Username, u.Disabled ? "disabled" : "active", u.TotpEnrolled ? "enrolled" : "pending", u.Items.ToString(),
                        u.ActiveSessions.ToString(), Time(u.LastSeenAt), Time(u.CreatedAt),
                    }).Concat(invites.Select(i => new[] { i.Username, $"invited until {Time(i.ExpiresAt)}", "-", "-", "-", "-", Time(i.CreatedAt) })));
                return 0;

            case ["registration", ("on" or "off" or "status") and var mode]:
                if (mode != "status")
                    store.RegistrationOpen = mode == "on";
                output.WriteLine($"Registration is {(store.RegistrationOpen ? "open" : "closed")}.");
                return 0;

            case ["user", "create", var name]:
                return InviteUser(store, name, output, error);

            case ["user", var action and ("show" or "disable" or "enable" or "delete" or "reset-totp" or "revoke-sessions"), var name]:
                if (store.FindUser(name) is { } user)
                    return UserAction(store, user, action, yes, input, output);
                if (store.FindInvite(name) is not { } invite)
                {
                    error.WriteLine($"User '{name}' not found.");
                    return 1;
                }
                if (action == "delete")
                {
                    store.DeleteInvite(invite.Username);
                    output.WriteLine($"Deleted the invite for '{invite.Username}'.");
                    return 0;
                }
                error.WriteLine($"'{invite.Username}' is invited (until {Time(invite.ExpiresAt)}) but has not created the account yet.");
                return 1;

            default:
                throw new UsageException($"Unknown command '{string.Join(' ', args)}'.");
        }
    }

    private static int UserAction(Store store, UserRecord user, string action, bool yes, TextReader input, TextWriter output)
    {
        switch (action)
        {
            case "show":
                var (live, deleted) = store.CountItems(user.Id);
                output.WriteLine($"Username       {user.Username}");
                output.WriteLine($"Id             {user.Id}");
                output.WriteLine($"Status         {(user.Disabled ? "disabled" : "active")}");
                output.WriteLine($"Authenticator  {(user.TotpSecret is null ? "pending (set up at next sign-in)" : "enrolled")}");
                output.WriteLine($"Items          {live} ({deleted} deleted), revision {user.Revision}");
                output.WriteLine($"Created        {Time(user.CreatedAt)}");
                output.WriteLine();
                var sessions = store.ListSessions(user.Id, null);
                output.WriteLine($"Sessions ({sessions.Count}):");
                if (sessions.Count > 0)
                {
                    WriteTable(output, ["ID", "DEVICE", "PLATFORM", "LAST SEEN", "LAST IP", "CREATED"],
                        sessions.Select(s => new[] { s.Id, s.DeviceName, s.Platform, Time(s.LastSeenAt), s.LastIp ?? "-", Time(s.CreatedAt) }));
                }
                return 0;

            case "disable":
                store.SetDisabled(user.Id, true);
                int signedOut = store.RevokeSessions(user.Id);
                output.WriteLine($"Disabled '{user.Username}' and signed out {signedOut} session(s).");
                return 0;

            case "enable":
                store.SetDisabled(user.Id, false);
                output.WriteLine($"Enabled '{user.Username}'.");
                return 0;

            case "delete":
                if (!yes)
                {
                    output.Write($"Delete '{user.Username}' and all of their synced data? This cannot be undone. [y/N] ");
                    if (input.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
                    {
                        output.WriteLine("Aborted.");
                        return 1;
                    }
                }
                store.DeleteUser(user.Id);
                output.WriteLine($"Deleted '{user.Username}'.");
                return 0;

            case "reset-totp":
                store.ResetTotp(user.Id);
                output.WriteLine($"Reset the authenticator of '{user.Username}'; they will set up a new one at their next sign-in.");
                return 0;

            default: // revoke-sessions
                output.WriteLine($"Signed out {store.RevokeSessions(user.Id)} session(s) of '{user.Username}'.");
                return 0;
        }
    }

    /// <summary>
    /// Reserves the username for a one-time code. The user creates the account in the app with it, so the password,
    /// the vault key and the authenticator are chosen on their device: neither the server nor the admin ever knows them.
    /// </summary>
    private static int InviteUser(Store store, string name, TextWriter output, TextWriter error)
    {
        if (UsernameRules.Validate(name) is { } reason)
            throw new UsageException(reason);
        if (store.FindUser(name) is not null)
        {
            error.WriteLine($"User '{name}' already exists.");
            return 1;
        }

        string code = store.CreateInvite(name);
        output.WriteLine($"Invited '{name}'. In the TGK app, they choose \"Create an account\" and enter this username and invite code:");
        output.WriteLine();
        output.WriteLine($"    {code}");
        output.WriteLine();
        output.WriteLine($"The code works once, also while registration is off, and expires in {Store.InviteLifetime.TotalDays:0} days.");
        return 0;
    }

    private static void WriteTable(TextWriter output, string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.Prepend(headers).ToList();
        int[] widths = headers.Select((_, i) => all.Max(row => row[i].Length)).ToArray();
        foreach (var row in all)
            output.WriteLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
    }

    private static string Time(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    private sealed class UsageException(string message) : Exception(message);
}
