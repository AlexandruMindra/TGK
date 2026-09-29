using System;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>DEV ONLY — demo content for a fresh mock vault. Contains no real secrets or keys.</summary>
internal static class SampleVault
{
    public static VaultData Create(DateTimeOffset now)
    {
        var production = new HostGroup { Name = "Production", SortOrder = 0 };
        var staging = new HostGroup { Name = "Staging", SortOrder = 1 };
        var personal = new HostGroup { Name = "Personal", SortOrder = 2 };

        // Password is deliberately empty: the user is asked for it when connecting.
        var deploy = new Identity { Name = "Deploy account", Username = "deploy", AuthKind = AuthKind.Password };

        const string red = "#E5534B", amber = "#D29922", green = "#3FB950", blue = "#58A6FF", purple = "#A371F7";

        // Production is reached through its bastion (a group-level jump host the bastion itself skips).
        var bastion = new HostEntry { Name = "bastion", Host = "bastion.prod.example.com", GroupId = production.Id, IdentityId = deploy.Id, TagColor = purple, Notes = "Jump host for the Production group." };
        production.Options = new HostOptions { JumpHostId = bastion.Id, KeepAliveSeconds = 15 };

        return new VaultData
        {
            Groups = [production, staging, personal],
            Identities = [deploy],
            Hosts =
            [
                new HostEntry { Name = "web-01", Host = "web-01.prod.example.com", GroupId = production.Id, IdentityId = deploy.Id, TagColor = red, Favorite = true, LastConnected = now.AddHours(-3), Notes = "Primary nginx node behind the load balancer." },
                new HostEntry
                {
                    Name = "db-primary", Host = "10.0.1.20", Username = "postgres", GroupId = production.Id, TagColor = red, Notes = "Read-write PostgreSQL. Be careful.",
                    Tunnels = [new PortForward { Kind = ForwardKind.Local, BindPort = 15432, DestinationHost = "127.0.0.1", DestinationPort = 5432, Description = "PostgreSQL" }],
                },
                bastion,
                new HostEntry { Name = "staging-web", Host = "staging.example.com", GroupId = staging.Id, IdentityId = deploy.Id, TagColor = amber, LastConnected = now.AddDays(-1) },
                new HostEntry { Name = "staging-db", Host = "10.0.2.15", Username = "admin", GroupId = staging.Id, TagColor = amber },
                new HostEntry { Name = "homelab", Host = "192.168.1.10", Username = "pi", GroupId = personal.Id, TagColor = green, Favorite = true, LastConnected = now.AddMinutes(-40), Options = new HostOptions { ColorScheme = "Nord" } },
                new HostEntry { Name = "vps", Host = "vps.example.net", Username = "root", GroupId = personal.Id, TagColor = blue },
                new HostEntry { Name = "localhost", Host = "127.0.0.1", Notes = "Local sshd, handy for testing." },
            ],
            Revision = 1,
            UpdatedAt = now,
        };
    }
}
