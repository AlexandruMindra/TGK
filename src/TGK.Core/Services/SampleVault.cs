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

        return new VaultData
        {
            Groups = [production, staging, personal],
            Identities = [deploy],
            Hosts =
            [
                new HostEntry { Name = "web-01", Host = "web-01.prod.example.com", GroupId = production.Id, IdentityId = deploy.Id, TagColor = red, Favorite = true, LastConnected = now.AddHours(-3), Notes = "Primary nginx node behind the load balancer." },
                new HostEntry { Name = "db-primary", Host = "10.0.1.20", Username = "postgres", GroupId = production.Id, TagColor = red, Notes = "Read-write PostgreSQL. Be careful." },
                new HostEntry { Name = "api-gateway", Host = "api.prod.example.com", Port = 2222, GroupId = production.Id, IdentityId = deploy.Id, TagColor = purple },
                new HostEntry { Name = "staging-web", Host = "staging.example.com", GroupId = staging.Id, IdentityId = deploy.Id, TagColor = amber, LastConnected = now.AddDays(-1) },
                new HostEntry { Name = "staging-db", Host = "10.0.2.15", Username = "admin", GroupId = staging.Id, TagColor = amber },
                new HostEntry { Name = "homelab", Host = "192.168.1.10", Username = "pi", GroupId = personal.Id, TagColor = green, Favorite = true, LastConnected = now.AddMinutes(-40) },
                new HostEntry { Name = "vps", Host = "vps.example.net", Username = "root", GroupId = personal.Id, TagColor = blue },
                new HostEntry { Name = "localhost", Host = "127.0.0.1", Notes = "Local sshd, handy for testing." },
            ],
            Revision = 1,
            UpdatedAt = now,
        };
    }
}
