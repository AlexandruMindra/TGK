using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Core.Services;

/// <summary>JSON over HTTP to <c>{server}/api/</c>; every failure surfaces as a <see cref="VaultException"/>.</summary>
internal sealed class VaultApiClient : IDisposable
{
    private readonly HttpClient _http;

    public VaultApiClient(HttpMessageHandler? handler, TimeSpan timeout)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TGK", ClientVersion));
    }

    public static string ClientVersion { get; } = typeof(VaultApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public void Dispose() => _http.Dispose();

    public async Task<T> SendAsync<T>(Uri baseUri, HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendCoreAsync(baseUri, method, path, body, token, ct).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ProtocolJson.Options, ct).ConfigureAwait(false)
                ?? throw new JsonException("Empty response.");
        }
        catch (JsonException ex)
        {
            throw new VaultException(VaultError.IncompatibleServer, "The server sent an unexpected response. Is this a TGK server?", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw Unreachable(baseUri, ex);
        }
    }

    public async Task SendAsync(Uri baseUri, HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendCoreAsync(baseUri, method, path, body, token, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(Uri baseUri, HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUri, "api/" + path));
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: ProtocolJson.Options);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(baseUri, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Unreachable(baseUri, ex); // HttpClient.Timeout
        }

        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw ToException(response.StatusCode, await ReadErrorAsync(response, ct).ConfigureAwait(false));
    }

    private static async Task<ErrorResponse?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponse>(ProtocolJson.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or NotSupportedException)
        {
            return null; // not a TGK error body (e.g. a proxy's HTML page)
        }
    }

    internal static VaultException ToException(HttpStatusCode status, ErrorResponse? error)
    {
        VaultError code = error?.Error switch
        {
            ErrorCodes.InvalidCredentials => VaultError.InvalidCredentials,
            ErrorCodes.TotpRequired => VaultError.TotpRequired,
            ErrorCodes.TotpInvalid => VaultError.TotpInvalid,
            ErrorCodes.TotpSetupRequired => VaultError.TotpSetupRequired,
            ErrorCodes.AccountDisabled => VaultError.AccountDisabled,
            ErrorCodes.UsernameTaken => VaultError.UsernameTaken,
            ErrorCodes.RegistrationClosed => VaultError.RegistrationClosed,
            ErrorCodes.ValidationFailed => VaultError.ValidationFailed,
            ErrorCodes.RateLimited => VaultError.RateLimited,
            ErrorCodes.Unauthorized => VaultError.Unauthorized,
            ErrorCodes.NotFound => VaultError.NotFound,
            ErrorCodes.PayloadTooLarge => VaultError.PayloadTooLarge,
            ErrorCodes.InviteInvalid => VaultError.InviteInvalid,
            _ => status switch
            {
                HttpStatusCode.Unauthorized => VaultError.Unauthorized,
                HttpStatusCode.RequestEntityTooLarge => VaultError.PayloadTooLarge,
                HttpStatusCode.TooManyRequests => VaultError.RateLimited,
                >= HttpStatusCode.InternalServerError => VaultError.Server,
                _ => VaultError.IncompatibleServer,
            },
        };
        string message = code switch
        {
            VaultError.InvalidCredentials => "Invalid username or password.",
            VaultError.TotpRequired => "Enter the code from your authenticator app.",
            VaultError.TotpInvalid => "That authenticator code is wrong or was already used. Wait for the next code and try again.",
            VaultError.TotpSetupRequired => "Your authenticator was reset by the administrator. Set up a new one to sign in.",
            VaultError.AccountDisabled => "This account is disabled. Contact the server administrator.",
            VaultError.UsernameTaken => "That username is already taken.",
            VaultError.RegistrationClosed => "This server does not accept new accounts.",
            VaultError.RateLimited => "Too many attempts. Wait a few minutes and try again.",
            VaultError.Unauthorized => "Your session has ended. Sign in again.",
            VaultError.PayloadTooLarge when string.IsNullOrWhiteSpace(error?.Message) => "The change is too large for the server.",
            VaultError.InviteInvalid => "The invite code is wrong for this username or has expired.",
            VaultError.IncompatibleServer => $"The server sent an unexpected response (HTTP {(int)status}). Is this a TGK server?",
            _ => !string.IsNullOrWhiteSpace(error?.Message) ? error.Message : $"The server returned an error (HTTP {(int)status}).",
        };
        return new VaultException(code, message);
    }

    private static VaultException Unreachable(Uri baseUri, Exception ex) =>
        new(VaultError.Network, $"Cannot reach the server at {baseUri.Authority}: {ex.Message}", ex);
}
