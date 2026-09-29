using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Server.Endpoints;

/// <summary>Sync of opaque, client-encrypted vault items.</summary>
public static class VaultEndpoints
{
    public static void Map(RouteGroupBuilder authenticated)
    {
        authenticated.MapGet("/vault", (AuthSession auth, Store store, ServerOptions options, long since = 0) =>
            since < 0 ? Api.Invalid("since must be >= 0.") : Results.Ok(store.Pull(auth.UserId, since, options.PullPageBytes)));
        authenticated.MapPost("/vault", Push);
    }

    private static IResult Push(VaultPushRequest request, AuthSession auth, Store store, ServerOptions options)
    {
        if (request.Changes.Count > ProtocolConstants.MaxChangesPerRequest)
            return Api.Invalid($"At most {ProtocolConstants.MaxChangesPerRequest} changes per request.");
        foreach (var change in request.Changes)
        {
            if (change is null || string.IsNullOrEmpty(change.Id) || change.Id.Length > ProtocolConstants.MaxItemIdLength)
                return Api.Invalid($"Every change needs an id of 1-{ProtocolConstants.MaxItemIdLength} characters.");
            if (change.Data?.Length > ProtocolConstants.MaxItemBytes)
                return Api.Error(StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge,
                    $"Item {change.Id} exceeds {ProtocolConstants.MaxItemBytes} bytes.");
        }
        return store.Push(auth.UserId, request.Changes, options.MaxItemsPerUser, options.MaxVaultBytesPerUser) is { } applied
            ? Results.Ok(applied)
            : Api.Error(StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge,
                $"Your vault is full: this server allows {options.MaxItemsPerUser} items and {options.MaxVaultBytesPerUser / (1024 * 1024)} MB per account.");
    }
}
