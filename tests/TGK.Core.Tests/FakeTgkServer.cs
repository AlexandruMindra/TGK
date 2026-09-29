using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Services;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Core.Tests;

/// <summary>
/// An in-memory TGK server behind an <see cref="HttpMessageHandler"/>: enough of protocol v1 to drive
/// <see cref="RemoteVaultService"/> (no TOTP replay protection, no rate limiting).
/// </summary>
internal sealed class FakeTgkServer : HttpMessageHandler
{
    public const string Url = "https://tgk.test";

    /// <summary>The cheapest KDF the client accepts, to keep tests fast.</summary>
    public static readonly KdfParams TestKdf = new(KdfParams.Pbkdf2Sha256, FastCrypto.KdfIterations);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, User> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Token> _tokens = [];

    /// <summary>Every request fails like an unreachable server.</summary>
    public bool Offline { get; set; }

    /// <summary>Answers a request before the fake does (return null to continue normally).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>"METHOD /path" of every request that reached the fake.</summary>
    public List<string> Requests { get; } = [];

    public sealed class User(string name)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; } = name;
        public byte[] AuthKey { get; set; } = [];
        public byte[] Salt { get; set; } = [];
        public KdfParams Kdf { get; set; } = TestKdf;
        public byte[] WrappedVaultKey { get; set; } = [];
        public string? TotpSecret { get; set; }
        public bool Disabled { get; set; }
        public long Revision { get; set; }
        public Dictionary<string, VaultItem> Items { get; } = [];
    }

    private sealed record Token(string SessionId, User User, string DeviceName);

    /// <summary>Creates an account like a client would; returns its vault key.</summary>
    public byte[] AddUser(string name, string password, string totpSecret)
    {
        byte[] salt = VaultCrypto.NewSalt();
        (byte[] authKey, byte[] kek) = VaultCrypto.DeriveKeys(password, salt, TestKdf);
        byte[] vaultKey = VaultCrypto.NewVaultKey();
        lock (_gate)
            _users[name] = new User(name) { AuthKey = authKey, Salt = salt, WrappedVaultKey = VaultCrypto.WrapVaultKey(kek, vaultKey), TotpSecret = totpSecret };
        return vaultKey;
    }

    public User GetUser(string name)
    {
        lock (_gate)
            return _users[name];
    }

    /// <summary>Writes an item as another device would (null data = delete).</summary>
    public void PutItem(string username, string id, byte[]? data)
    {
        lock (_gate)
            Apply(_users[username], id, data);
    }

    /// <summary>Changes the account's server state directly (e.g. to simulate a restored backup).</summary>
    public void Change(string username, Action<User> change)
    {
        lock (_gate)
            change(_users[username]);
    }

    public VaultItem? GetItem(string username, string id)
    {
        lock (_gate)
            return _users[username].Items.GetValueOrDefault(id);
    }

    public int SessionCount(string username)
    {
        lock (_gate)
            return _tokens.Values.Count(t => t.User.Name == username);
    }

    public void RevokeAll()
    {
        lock (_gate)
            _tokens.Clear();
    }

    public static string CurrentCode(string secret) => Totp.ComputeCode(secret, Totp.GetStep(DateTimeOffset.UtcNow));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Yield();
        if (Offline)
            throw new HttpRequestException("Connection refused");
        if (Intercept?.Invoke(request) is { } intercepted)
            return intercepted;

        string path = request.RequestUri!.AbsolutePath;
        string route = $"{request.Method} {path}";
        lock (_gate)
            Requests.Add(route);

        switch (route)
        {
            case "GET /api/info":
                return Json(HttpStatusCode.OK, new InfoResponse(ProtocolConstants.ServerName, "test", 1, true));
            case "POST /api/prelogin":
            {
                var body = await Read<PreloginRequest>(request, ct);
                lock (_gate)
                {
                    return _users.TryGetValue(body.Username, out User? user)
                        ? Json(HttpStatusCode.OK, new PreloginResponse(user.Salt, user.Kdf))
                        : Json(HttpStatusCode.OK, new PreloginResponse(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body.Username))[..16], KdfParams.Default));
                }
            }
            case "POST /api/login":
            {
                var body = await Read<LoginRequest>(request, ct);
                lock (_gate)
                {
                    if (!_users.TryGetValue(body.Username, out User? user) || !user.AuthKey.AsSpan().SequenceEqual(body.AuthKey))
                        return Error(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
                    if (user.Disabled)
                        return Error(HttpStatusCode.Forbidden, ErrorCodes.AccountDisabled);
                    if (user.TotpSecret is null)
                    {
                        if (body.NewTotpSecret is null || !Totp.Verify(body.NewTotpSecret, body.TotpCode, DateTimeOffset.UtcNow, -1, out _))
                            return Error(HttpStatusCode.Forbidden, ErrorCodes.TotpSetupRequired);
                        user.TotpSecret = body.NewTotpSecret;
                    }
                    else if (body.TotpCode is null)
                        return Error(HttpStatusCode.Unauthorized, ErrorCodes.TotpRequired);
                    else if (!Totp.Verify(user.TotpSecret, body.TotpCode, DateTimeOffset.UtcNow, -1, out _))
                        return Error(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
                    (string token, string sessionId) = NewSession(user, body.DeviceName);
                    return Json(HttpStatusCode.OK, new LoginResponse(token, sessionId, user.Id, user.Name, user.WrappedVaultKey, user.Salt, user.Kdf, user.Revision));
                }
            }
            case "POST /api/register":
            {
                var body = await Read<RegisterRequest>(request, ct);
                lock (_gate)
                {
                    if (_users.ContainsKey(body.Username))
                        return Error(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
                    if (!Totp.Verify(body.TotpSecret, body.TotpCode, DateTimeOffset.UtcNow, -1, out _))
                        return Error(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
                    var user = new User(body.Username)
                    {
                        AuthKey = body.AuthKey, Salt = body.Salt, Kdf = body.Kdf, WrappedVaultKey = body.WrappedVaultKey, TotpSecret = body.TotpSecret,
                    };
                    _users[user.Name] = user;
                    (string token, string sessionId) = NewSession(user, body.DeviceName);
                    return Json(HttpStatusCode.Created, new RegisterResponse(token, sessionId, user.Id, user.Name, 0));
                }
            }
        }

        // Authenticated endpoints.
        Token? auth;
        lock (_gate)
            auth = request.Headers.Authorization?.Parameter is { } t ? _tokens.GetValueOrDefault(t) : null;
        if (auth is null)
            return Error(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        User me = auth.User;

        switch (route)
        {
            case "POST /api/logout":
                lock (_gate)
                    _tokens.Remove(request.Headers.Authorization!.Parameter!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "GET /api/sessions":
                lock (_gate)
                {
                    return Json(HttpStatusCode.OK, _tokens.Values.Where(t => t.User == me)
                        .Select(t => new SessionInfo(t.SessionId, t.DeviceName, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "127.0.0.1", t == auth)).ToArray());
                }
            case "POST /api/sessions/revoke-others":
                lock (_gate)
                {
                    var others = _tokens.Where(p => p.Value.User == me && p.Value != auth).Select(p => p.Key).ToList();
                    others.ForEach(k => _tokens.Remove(k));
                    return Json(HttpStatusCode.OK, new RevokeOthersResponse(others.Count));
                }
            case "POST /api/account/password":
            {
                var body = await Read<ChangePasswordRequest>(request, ct);
                lock (_gate)
                {
                    if (!me.AuthKey.AsSpan().SequenceEqual(body.CurrentAuthKey))
                        return Error(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
                    if (!Totp.Verify(me.TotpSecret!, body.TotpCode, DateTimeOffset.UtcNow, -1, out _))
                        return Error(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
                    me.AuthKey = body.NewAuthKey;
                    me.Salt = body.NewSalt;
                    me.Kdf = body.NewKdf;
                    me.WrappedVaultKey = body.NewWrappedVaultKey;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }
            case "GET /api/vault":
            {
                long since = long.Parse(request.RequestUri.Query.Split('=')[1]);
                lock (_gate)
                    return Json(HttpStatusCode.OK, new VaultPullResponse(me.Revision, me.Items.Values.Where(i => i.Revision > since).OrderBy(i => i.Revision).ToList()));
            }
            case "POST /api/vault":
            {
                var body = await Read<VaultPushRequest>(request, ct);
                lock (_gate)
                {
                    var applied = body.Changes.Select(c => new AppliedChange(c.Id, Apply(me, c.Id, c.Data))).ToList();
                    return Json(HttpStatusCode.OK, new VaultPushResponse(me.Revision, applied));
                }
            }
        }

        if (request.Method == HttpMethod.Delete && path.StartsWith("/api/sessions/", StringComparison.Ordinal))
        {
            string id = path["/api/sessions/".Length..];
            lock (_gate)
            {
                string? key = _tokens.FirstOrDefault(p => p.Value.User == me && p.Value.SessionId == id).Key;
                if (key is null)
                    return Error(HttpStatusCode.NotFound, ErrorCodes.NotFound);
                _tokens.Remove(key);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
        return Error(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    private (string Token, string SessionId) NewSession(User user, string deviceName)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string sessionId = Guid.NewGuid().ToString("N");
        _tokens[token] = new Token(sessionId, user, deviceName);
        return (token, sessionId);
    }

    private static long Apply(User user, string id, byte[]? data)
    {
        long revision = ++user.Revision;
        user.Items[id] = new VaultItem(id, revision, data is null, data, DateTimeOffset.UtcNow);
        return revision;
    }

    private static async Task<T> Read<T>(HttpRequestMessage request, CancellationToken ct) =>
        (await request.Content!.ReadFromJsonAsync<T>(ProtocolJson.Options, ct))!;

    public static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body, options: ProtocolJson.Options) };

    public static HttpResponseMessage Error(HttpStatusCode status, string code) => Json(status, new ErrorResponse(code, code));
}

internal sealed class MemoryCredentialStore : IDeviceCredentialStore
{
    public DeviceCredentials? Stored { get; set; }
    public DeviceCredentials? Load() => Stored;
    public void Save(DeviceCredentials credentials) => Stored = credentials;
    public void Clear() => Stored = null;
}
