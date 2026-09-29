using System;

namespace TGK.Core.Services;

/// <summary>Why a vault operation failed. Most values mirror the server's error codes.</summary>
public enum VaultError
{
    None,
    InvalidCredentials,
    TotpRequired,
    TotpInvalid,
    TotpSetupRequired,
    AccountDisabled,
    UsernameTaken,
    RegistrationClosed,
    ValidationFailed,
    RateLimited,

    /// <summary>The session was revoked or expired.</summary>
    Unauthorized,

    NotFound,
    PayloadTooLarge,

    /// <summary>The invite code does not match the username or has expired.</summary>
    InviteInvalid,

    /// <summary>The server could not be reached (DNS, connection, TLS or timeout).</summary>
    Network,

    /// <summary>Plain <c>http://</c> to a host other than localhost, or an unusable address.</summary>
    InsecureUrl,

    /// <summary>The server is not a compatible TGK server, sent unsafe parameters or an unreadable response.</summary>
    IncompatibleServer,

    /// <summary>Any other server-side failure (e.g. HTTP 500).</summary>
    Server,

    /// <summary>A local file (the local vault, a backup) could not be read or written, or is damaged.</summary>
    Storage,
}

/// <summary>A failed vault or account operation; <see cref="Exception.Message"/> is suitable for the user.</summary>
public sealed class VaultException(VaultError code, string message, Exception? inner = null) : Exception(message, inner)
{
    public VaultError Code { get; } = code;
}
