namespace TGK.Protocol.Dtos;

/// <summary>Body of every non-2xx response. <see cref="Error"/> is one of <see cref="ErrorCodes"/>.</summary>
public sealed record ErrorResponse(string Error, string Message);

public static class ErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string TotpRequired = "totp_required";
    public const string TotpInvalid = "totp_invalid";
    public const string TotpSetupRequired = "totp_setup_required";
    public const string AccountDisabled = "account_disabled";
    public const string UsernameTaken = "username_taken";
    public const string RegistrationClosed = "registration_closed";
    public const string ValidationFailed = "validation_failed";
    public const string RateLimited = "rate_limited";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not_found";
    public const string PayloadTooLarge = "payload_too_large";
    public const string InviteInvalid = "invite_invalid";
}
