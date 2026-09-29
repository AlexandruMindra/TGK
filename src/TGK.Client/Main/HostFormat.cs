using System;
using System.Linq;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Core.Models;
using TGK.Core.Services;

namespace TGK.Client.Main;

/// <summary>Display strings shared by the sidebar, the new-tab page and the status bar.</summary>
public static class HostFormat
{
    /// <summary>The login name used for <paramref name="host"/> (the host's own, else its identity's), or null.</summary>
    public static string? UserOf(HostEntry host, VaultData vault)
    {
        if (!string.IsNullOrWhiteSpace(host.Username))
            return host.Username.Trim();
        string? identityUser = vault.FindIdentity(host.IdentityId)?.Username;
        return string.IsNullOrWhiteSpace(identityUser) ? null : identityUser.Trim();
    }

    /// <summary><c>user@host</c>, with <c>:port</c> when not 22.</summary>
    public static string Address(HostEntry host, VaultData vault)
    {
        string? user = UserOf(host, vault);
        string address = host.Port == 22 ? host.Host : $"{host.Host}:{host.Port}";
        return user is null ? address : $"{user}@{address}";
    }

    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } t)
            return "never";
        TimeSpan d = now - t;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays}d ago";
        return t.LocalDateTime.ToString("yyyy-MM-dd");
    }

    /// <summary>Short sync status, e.g. "Synced · 2m", "Syncing…", "Sync error".</summary>
    public static string Sync(IVaultService vault, DateTimeOffset now) => vault.Status switch
    {
        SyncState.Syncing => "Syncing…",
        SyncState.Error => "Sync error",
        SyncState.Offline => "Offline",
        _ => vault.LastSync is { } last && now - last >= TimeSpan.FromMinutes(1)
            ? $"Synced · {Short(now - last)}"
            : "Synced",
    };

    private static string Short(TimeSpan d) =>
        d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m"
        : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours}h"
        : $"{(int)d.TotalDays}d";
}

/// <summary>Small icons after a host's name: it connects through a jump host, it has enabled tunnels.</summary>
public static class HostHints
{
    private const float Size = 13, Step = 18;

    /// <summary>Draws the icons right-aligned at <paramref name="right"/>; returns the x where they start (= <paramref name="right"/> when none).</summary>
    public static float Draw(SKCanvas c, HostEntry host, VaultData vault, float right, float cy, SKColor color)
    {
        float x = right;
        if (host.Tunnels.Any(t => t.Enabled))
        {
            Icons.Draw(c, "tunnel", x - Size / 2f, cy, Size, color);
            x -= Step;
        }
        if (EffectiveOptions.Resolve(vault, host).JumpHostId.Value is not null)
        {
            Icons.Draw(c, "route", x - Size / 2f, cy, Size, color);
            x -= Step;
        }
        return x;
    }
}
