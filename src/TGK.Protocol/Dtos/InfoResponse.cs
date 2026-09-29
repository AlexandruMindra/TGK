namespace TGK.Protocol.Dtos;

/// <summary><c>GET /api/info</c></summary>
public sealed record InfoResponse(string Name, string Version, int Protocol, bool RegistrationOpen);
