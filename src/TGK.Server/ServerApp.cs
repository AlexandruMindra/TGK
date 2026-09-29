using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using TGK.Server.Endpoints;

namespace TGK.Server;

public sealed class ServerOptions
{
    public const string DefaultUrls = "http://127.0.0.1:5080";
    public const int DefaultRateLimit = 20;
    public static readonly string DefaultDbPath = Path.Combine("data", "tgk.db");

    public string DbPath { get; init; } = DefaultDbPath;
    public string Urls { get; init; } = DefaultUrls;
    public string? CertPath { get; init; }
    public string? CertPassword { get; init; }

    /// <summary>Honour X-Forwarded-For/Proto from a reverse proxy on loopback.</summary>
    public bool TrustProxy { get; init; }

    /// <summary>Requests per minute per client IP to prelogin, login, register and password change (together).</summary>
    public int RateLimitPerMinute { get; init; } = DefaultRateLimit;

    /// <summary>Per-account vault limits; tombstones count as items. A push that would exceed them is refused.</summary>
    public long MaxItemsPerUser { get; init; } = 50_000;

    public long MaxVaultBytesPerUser { get; init; } = 64 * 1024 * 1024;

    /// <summary>Roughly how much item data one <c>GET /api/vault</c> returns before the client has to ask for the next page.</summary>
    public long PullPageBytes { get; init; } = Store.DefaultPullPageBytes;
}

/// <summary>The caller's session, set by the session middleware on endpoints that require one.</summary>
public sealed record AuthSession(string SessionId, string UserId, string Username)
{
    public static ValueTask<AuthSession?> BindAsync(HttpContext context) => ValueTask.FromResult(context.Features.Get<AuthSession>());
}

/// <summary>Endpoint metadata: the request needs a valid bearer session.</summary>
public sealed class RequiresSession;

public static class Api
{
    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), statusCode: status);

    public static IResult Invalid(string message) => Error(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, message);

    public static Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorResponse(code, message));
    }

    public static string? ClientIp(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? null : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}

public static class ServerApp
{
    public const string AuthRateLimitPolicy = "auth";

    /// <summary>Body limit for everything but <c>POST /api/vault</c>.</summary>
    private const long SmallBodyLimit = 64 * 1024;

    public static void Run(ServerOptions options, string[] hostArgs) => Build(options, hostArgs).Run();

    /// <param name="hostArgs">Extra <c>--Key=Value</c> ASP.NET configuration (the test host passes its settings this way).</param>
    public static WebApplication Build(ServerOptions options, string[] hostArgs)
    {
        var builder = WebApplication.CreateBuilder(hostArgs);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        var certificate = options.CertPath is null ? null : X509CertificateLoader.LoadPkcs12FromFile(options.CertPath, options.CertPassword);
        builder.WebHost.UseUrls(options.Urls);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = ProtocolConstants.MaxRequestBytes;
            if (certificate is not null)
                kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
        });

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new Db(sp.GetRequiredService<ServerOptions>().DbPath));
        services.AddSingleton<Store>();
        services.AddSingleton<LoginThrottle>();
        services.ConfigureHttpJsonOptions(o => ProtocolJson.Apply(o.SerializerOptions));
        // Malformed bodies throw BadHttpRequestException, which HandleErrors turns into a JSON error.
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) => new ValueTask(Api.WriteError(context.HttpContext, StatusCodes.Status429TooManyRequests,
                ErrorCodes.RateLimited, "Too many requests. Try again in a minute."));
            o.AddPolicy(AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                Api.ClientIp(context) ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<ServerOptions>().RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });

        var app = builder.Build();
        options = app.Services.GetRequiredService<ServerOptions>(); // tests replace the registered options
        var db = app.Services.GetRequiredService<Db>();
        app.Logger.LogInformation("Database {Path}{New}; registration {Registration}", db.Path, db.IsNew ? " (created)" : "",
            app.Services.GetRequiredService<Store>().RegistrationOpen ? "open" : "closed");
        if (!options.TrustProxy && options.Urls.Split(';').Any(IsPublicPlainHttp))
            app.Logger.LogWarning("Serving plain HTTP on a non-loopback address; clients refuse it. Use --cert, or a TLS reverse proxy with --trust-proxy.");

        app.Use(HandleErrors);
        if (options.TrustProxy)
            app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
        app.Use(LimitBodySize);
        app.UseRouting();
        app.UseRateLimiter();
        app.Use(RequireSession);

        var api = app.MapGroup("/api");
        var authenticated = api.MapGroup("").WithMetadata(new RequiresSession());
        AccountEndpoints.Map(api, authenticated);
        SessionEndpoints.Map(authenticated);
        VaultEndpoints.Map(authenticated);
        app.MapFallback(() => Api.Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found."));
        return app;
    }

    private static bool IsPublicPlainHttp(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;

    private static async Task HandleErrors(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            if (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                await Api.WriteError(context, e.StatusCode, ErrorCodes.PayloadTooLarge, "Request body too large.");
            else
                await Api.WriteError(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                    e.InnerException is JsonException json ? json.Message : e.Message);
        }
        catch (Exception e) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ServerApp))
                .LogError(e, "Unhandled error in {Method} {Path}", context.Request.Method, context.Request.Path);
            await Api.WriteError(context, StatusCodes.Status500InternalServerError, "server_error", "Internal server error.");
        }
    }

    private static Task LimitBodySize(HttpContext context, RequestDelegate next)
    {
        long limit = context.Request.Path.StartsWithSegments("/api/vault") ? ProtocolConstants.MaxRequestBytes : SmallBodyLimit;
        if (context.Request.ContentLength > limit)
            return Api.WriteError(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge, "Request body too large.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit; // enforced by Kestrel for chunked bodies
        return next(context);
    }

    private static Task RequireSession(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresSession>() is null)
            return next(context);

        const string prefix = "Bearer ";
        string? header = context.Request.Headers.Authorization;
        var session = header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? context.RequestServices.GetRequiredService<Store>().Authenticate(header[prefix.Length..].Trim(), Api.ClientIp(context))
            : null;
        if (session is null)
            return Api.WriteError(context, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "Not signed in, or the session expired or was revoked.");

        context.Features.Set(session);
        return next(context);
    }
}
