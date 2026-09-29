using System;

namespace TGK.Protocol.Dtos;

/// <summary>An element of <c>GET /api/sessions</c>; <see cref="Current"/> marks the caller's own session.</summary>
public sealed record SessionInfo(
    string Id,
    string DeviceName,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? LastIp,
    bool Current);

/// <summary><c>POST /api/sessions/revoke-others</c></summary>
public sealed record RevokeOthersResponse(int Revoked);
