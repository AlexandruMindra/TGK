using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Server.Endpoints;

/// <summary>Info, prelogin, register, login, logout and password change.</summary>
public static class AccountEndpoints
{
    private const int MaxWrappedKeyBytes = 1024;
    private const int MaxLabelLength = 64;

    private static readonly string Version = typeof(AccountEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void Map(RouteGroupBuilder api, RouteGroupBuilder authenticated)
    {
        api.MapGet("/info", (Store store) =>
            new InfoResponse(ProtocolConstants.ServerName, Version, ProtocolConstants.Version, store.RegistrationOpen));
        api.MapPost("/prelogin", Prelogin).RequireRateLimiting(ServerApp.AuthRateLimitPolicy);
        api.MapPost("/register", Register).RequireRateLimiting(ServerApp.AuthRateLimitPolicy);
        api.MapPost("/login", Login).RequireRateLimiting(ServerApp.AuthRateLimitPolicy);
        authenticated.MapPost("/logout", (AuthSession auth, Store store) =>
        {
            store.RevokeSession(auth.UserId, auth.SessionId);
            return Results.NoContent();
        });
        authenticated.MapPost("/account/password", ChangePassword).RequireRateLimiting(ServerApp.AuthRateLimitPolicy);
    }

    private static IResult Prelogin(PreloginRequest request, Store store)
    {
        if (UsernameRules.Validate(request.Username) is { } reason)
            return Api.Invalid(reason);
        if (store.FindUser(request.Username) is { } user)
            return Results.Ok(new PreloginResponse(user.KdfSalt, user.Kdf));

        // Unknown users get a stable fake salt so the response does not reveal whether the account exists.
        byte[] mac = HMACSHA256.HashData(store.Pepper, Encoding.UTF8.GetBytes(request.Username.ToLowerInvariant()));
        return Results.Ok(new PreloginResponse(mac[..VaultCrypto.SaltSize], KdfParams.Default));
    }

    private static IResult Register(RegisterRequest request, HttpContext http, Store store, TimeProvider clock, ILoggerFactory logs)
    {
        if (UsernameRules.Validate(request.Username) is { } reason)
            return Api.Invalid(reason);
        // An invited username can only be claimed with its code; everyone else needs open registration.
        var invite = store.FindInvite(request.Username);
        if (!string.IsNullOrWhiteSpace(request.InviteCode))
        {
            if (invite is null || !Store.CheckInviteCode(invite, request.InviteCode))
                return Api.Error(StatusCodes.Status403Forbidden, ErrorCodes.InviteInvalid, "The invite code is not valid for this username or has expired.");
        }
        else if (invite is not null)
        {
            return Api.Error(StatusCodes.Status409Conflict, ErrorCodes.UsernameTaken, "That username is taken.");
        }
        else if (!store.RegistrationOpen)
        {
            return Api.Error(StatusCodes.Status403Forbidden, ErrorCodes.RegistrationClosed, "Registration is closed on this server.");
        }
        if (ValidateKeyMaterial(request.AuthKey, request.Salt, request.Kdf, request.WrappedVaultKey) is { } invalid)
            return invalid;
        if (!Totp.IsValidSecret(request.TotpSecret))
            return Api.Invalid("totpSecret must be base32 for 16-64 bytes.");
        if (!Totp.Verify(request.TotpSecret, request.TotpCode, clock.GetUtcNow(), 0, out long step))
            return Api.Error(StatusCodes.Status400BadRequest, ErrorCodes.TotpInvalid, "The authenticator code is not valid.");

        var user = store.CreateUser(request.Username, request.AuthKey, request.Salt, request.Kdf, request.WrappedVaultKey, request.TotpSecret, step);
        if (user is null)
            return Api.Error(StatusCodes.Status409Conflict, ErrorCodes.UsernameTaken, "That username is taken.");

        var (token, sessionId) = store.CreateSession(user.Id, Label(request.DeviceName), Label(request.Platform), Api.ClientIp(http));
        Log(logs).LogInformation("Registered user {Username} from {Ip}{Invited}", user.Username, Api.ClientIp(http), invite is null ? "" : " (invited)");
        return Results.Json(new RegisterResponse(token, sessionId, user.Id, user.Username, 0), statusCode: StatusCodes.Status201Created);
    }

    private static IResult Login(LoginRequest request, HttpContext http, Store store, LoginThrottle throttle, TimeProvider clock, ILoggerFactory logs)
    {
        var log = Log(logs);
        string? ip = Api.ClientIp(http);
        if (request.AuthKey.Length != VaultCrypto.KeySize)
            return Api.Invalid("authKey must be 32 bytes.");
        if (UsernameRules.Validate(request.Username) is not null)
            return InvalidCredentials(); // no such account can exist
        if (throttle.IsLocked(request.Username))
        {
            log.LogWarning("Login for {Username} from {Ip} refused: locked out", request.Username, ip);
            return Api.Error(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, "Too many failed sign-ins. Try again in a few minutes.");
        }

        var user = store.FindUser(request.Username);
        if (!Store.CheckAuthKey(user, request.AuthKey) || user is null)
            return Failed("bad credentials", InvalidCredentials());
        if (user.Disabled)
            return Api.Error(StatusCodes.Status403Forbidden, ErrorCodes.AccountDisabled, "This account is disabled.");

        var now = clock.GetUtcNow();
        if (user.TotpSecret is null)
        {
            if (request.NewTotpSecret is null)
                return Api.Error(StatusCodes.Status403Forbidden, ErrorCodes.TotpSetupRequired, "Set up an authenticator app to continue.");
            if (!Totp.IsValidSecret(request.NewTotpSecret))
                return Api.Invalid("newTotpSecret must be base32 for 16-64 bytes.");
            if (!Totp.Verify(request.NewTotpSecret, request.TotpCode, now, 0, out long step) || !store.EnrollTotp(user.Id, request.NewTotpSecret, step))
                return Failed("bad TOTP code (enrollment)", TotpInvalid());
            log.LogInformation("User {Username} enrolled a new authenticator", user.Username);
        }
        else if (string.IsNullOrWhiteSpace(request.TotpCode))
        {
            return Api.Error(StatusCodes.Status401Unauthorized, ErrorCodes.TotpRequired, "Enter the code from your authenticator app.");
        }
        else if (!Totp.Verify(user.TotpSecret, request.TotpCode, now, user.TotpLastStep, out long step) || !store.AdvanceTotpStep(user.Id, step))
        {
            return Failed("bad or reused TOTP code", TotpInvalid());
        }

        throttle.Reset(request.Username);
        string device = Label(request.DeviceName);
        var (token, sessionId) = store.CreateSession(user.Id, device, Label(request.Platform), ip);
        log.LogInformation("User {Username} signed in from {Ip} ({Device})", user.Username, ip, device);
        return Results.Ok(new LoginResponse(token, sessionId, user.Id, user.Username, user.WrappedVaultKey, user.KdfSalt, user.Kdf, user.Revision));

        IResult Failed(string reason, IResult result)
        {
            throttle.RecordFailure(request.Username);
            log.LogWarning("Login for {Username} from {Ip} failed: {Reason}", request.Username, ip, reason);
            return result;
        }
    }

    private static IResult ChangePassword(
        ChangePasswordRequest request, AuthSession auth, Store store, LoginThrottle throttle, TimeProvider clock, ILoggerFactory logs)
    {
        if (request.CurrentAuthKey.Length != VaultCrypto.KeySize)
            return Api.Invalid("currentAuthKey must be 32 bytes.");
        if (ValidateKeyMaterial(request.NewAuthKey, request.NewSalt, request.NewKdf, request.NewWrappedVaultKey) is { } invalid)
            return invalid;
        if (throttle.IsLocked(auth.Username))
            return Api.Error(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, "Too many failed attempts. Try again in a few minutes.");

        var user = store.GetUser(auth.UserId)!;
        if (!Store.CheckAuthKey(user, request.CurrentAuthKey))
        {
            throttle.RecordFailure(auth.Username);
            return InvalidCredentials();
        }
        if (user.TotpSecret is null)
            return Api.Error(StatusCodes.Status403Forbidden, ErrorCodes.TotpSetupRequired, "Set up an authenticator app first.");
        if (!Totp.Verify(user.TotpSecret, request.TotpCode, clock.GetUtcNow(), user.TotpLastStep, out long step) || !store.AdvanceTotpStep(user.Id, step))
        {
            throttle.RecordFailure(auth.Username);
            return TotpInvalid();
        }

        throttle.Reset(auth.Username);
        store.ChangePassword(user.Id, request.NewAuthKey, request.NewSalt, request.NewKdf, request.NewWrappedVaultKey);
        int revoked = request.RevokeOtherSessions ? store.RevokeSessions(user.Id, auth.SessionId) : 0;
        Log(logs).LogInformation("User {Username} changed their password ({Revoked} other sessions revoked)", user.Username, revoked);
        return Results.NoContent();
    }

    private static IResult? ValidateKeyMaterial(byte[] authKey, byte[] salt, KdfParams kdf, byte[] wrappedVaultKey)
    {
        if (authKey.Length != VaultCrypto.KeySize)
            return Api.Invalid("authKey must be 32 bytes.");
        if (salt.Length != VaultCrypto.SaltSize)
            return Api.Invalid("salt must be 16 bytes.");
        if (!kdf.IsAcceptable())
            return Api.Invalid($"Unsupported KDF (expected {KdfParams.Pbkdf2Sha256} with {KdfParams.AcceptedMinIterations}-{KdfParams.MaxIterations} iterations).");
        if (wrappedVaultKey.Length is 0 or > MaxWrappedKeyBytes)
            return Api.Invalid("wrappedVaultKey is missing or too large.");
        return null;
    }

    private static IResult InvalidCredentials() =>
        Api.Error(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials, "Invalid username or password.");

    private static IResult TotpInvalid() =>
        Api.Error(StatusCodes.Status401Unauthorized, ErrorCodes.TotpInvalid, "The authenticator code is not valid or was already used.");

    /// <summary>
    /// A client-supplied device name or platform, safe to print in the admin's terminal and the log: control and
    /// format characters (escape sequences, line breaks, bidi overrides) are dropped, and the length is capped.
    /// </summary>
    private static string Label(string value)
    {
        string clean = string.Concat(value.Where(c => char.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format))).Trim();
        return clean.Length <= MaxLabelLength ? clean : clean[..MaxLabelLength];
    }

    private static ILogger Log(ILoggerFactory logs) => logs.CreateLogger(typeof(AccountEndpoints));
}
