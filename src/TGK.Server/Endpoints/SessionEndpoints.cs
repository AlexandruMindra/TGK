using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TGK.Protocol.Dtos;

namespace TGK.Server.Endpoints;

/// <summary>The caller's own signed-in devices.</summary>
public static class SessionEndpoints
{
    public static void Map(RouteGroupBuilder authenticated)
    {
        authenticated.MapGet("/sessions", (AuthSession auth, Store store) => store.ListSessions(auth.UserId, auth.SessionId));
        authenticated.MapDelete("/sessions/{id}", (string id, AuthSession auth, Store store) => store.RevokeSession(auth.UserId, id)
            ? Results.NoContent()
            : Api.Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Session not found."));
        authenticated.MapPost("/sessions/revoke-others", (AuthSession auth, Store store) =>
            new RevokeOthersResponse(store.RevokeSessions(auth.UserId, auth.SessionId)));
    }
}
