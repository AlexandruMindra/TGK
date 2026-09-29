namespace TGK.Protocol.Dtos;

// byte[] members travel as standard base64 strings.

/// <summary><c>POST /api/prelogin</c></summary>
public sealed record PreloginRequest(string Username);

public sealed record PreloginResponse(byte[] Salt, KdfParams Kdf);

/// <summary>
/// <c>POST /api/register</c>; <see cref="TotpCode"/> must be valid for <see cref="TotpSecret"/> (base32). An
/// <see cref="InviteCode"/> from <c>tgk-server user create</c> claims the invited username, even with registration closed.
/// </summary>
public sealed record RegisterRequest(
    string Username,
    byte[] AuthKey,
    byte[] Salt,
    KdfParams Kdf,
    byte[] WrappedVaultKey,
    string TotpSecret,
    string TotpCode,
    string DeviceName,
    string Platform,
    string? InviteCode = null);

public sealed record RegisterResponse(string Token, string SessionId, string UserId, string Username, long Revision);

/// <summary>
/// <c>POST /api/login</c>. <see cref="NewTotpSecret"/> (with a matching <see cref="TotpCode"/>) enrolls an
/// authenticator for an account whose TOTP was reset by the admin.
/// </summary>
public sealed record LoginRequest(
    string Username,
    byte[] AuthKey,
    string DeviceName,
    string Platform,
    string? TotpCode = null,
    string? NewTotpSecret = null);

public sealed record LoginResponse(
    string Token,
    string SessionId,
    string UserId,
    string Username,
    byte[] WrappedVaultKey,
    byte[] Salt,
    KdfParams Kdf,
    long Revision);

/// <summary><c>POST /api/account/password</c>: swaps the verifier and re-wrapped vault key; items are untouched.</summary>
public sealed record ChangePasswordRequest(
    byte[] CurrentAuthKey,
    string TotpCode,
    byte[] NewAuthKey,
    byte[] NewSalt,
    KdfParams NewKdf,
    byte[] NewWrappedVaultKey,
    bool RevokeOtherSessions = false);
