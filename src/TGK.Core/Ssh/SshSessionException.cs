using System;
using System.Net.Sockets;
using Renci.SshNet.Common;

namespace TGK.Core.Ssh;

public enum SshErrorKind
{
    InvalidRequest,
    DnsFailure,
    ConnectionRefused,
    HostUnreachable,
    Timeout,
    HostKeyRejected,

    /// <summary>The server rejected the credentials (e.g. a wrong password); asking for them again may help.</summary>
    AuthenticationFailed,

    /// <summary>
    /// The server allows no sign-in method these credentials can use (e.g. keys only, but a password was given),
    /// or asked a question nobody was there to answer. Retrying with the same kind of credentials cannot help.
    /// </summary>
    UnsupportedAuthMethod,

    /// <summary>The private key could not be loaded: malformed, unsupported, missing or wrong passphrase.</summary>
    KeyError,

    /// <summary>The server or network dropped the connection, or an SSH protocol error occurred.</summary>
    ConnectionLost,

    /// <summary>
    /// The server and this client have no key exchange, host key, cipher or MAC algorithm in common. Usually an old
    /// server: enabling "Legacy algorithms" for the host may help (the message says so when it is off).
    /// </summary>
    AlgorithmMismatch,
    Other,
}

/// <summary>A connection failure with a user-facing <see cref="Exception.Message"/>. Messages never contain secrets.</summary>
public sealed class SshSessionException : Exception
{
    public SshSessionException(SshErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public SshErrorKind Kind { get; }

    /// <summary>
    /// Index into <see cref="SshConnectRequest.JumpChain"/> of the jump host the error is about (e.g. its password was
    /// wrong, so ask for that one again), or null when it concerns the final host or the request as a whole.
    /// </summary>
    public int? JumpHostIndex { get; init; }
}

/// <summary>Translates SSH.NET and socket exceptions into friendly <see cref="SshSessionException"/>s.</summary>
internal static class SshErrors
{
    public static SshSessionException Map(Exception exception, SshConnectRequest request)
    {
        string target = $"{request.Host}:{request.Port}";
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case SshSessionException mapped:
                    return mapped;
                case SshPassPhraseNullOrEmptyException:
                    return new(SshErrorKind.KeyError, "The private key is encrypted: enter its passphrase.", exception);
                case SshAuthenticationException auth when auth.Message.StartsWith("No suitable authentication method", StringComparison.Ordinal):
                    return new(SshErrorKind.UnsupportedAuthMethod, NoSuitableMethod(auth.Message, request), exception);
                case SshAuthenticationException:
                    return new(SshErrorKind.AuthenticationFailed,
                        $"Authentication failed for {request.Username}@{request.Host} (tried {Offered(request)}). Check the username, password or key.", exception);
                // OpenSSH disconnects once MaxAuthTries offers were rejected; that is a failed sign-in, not a network problem.
                case SshConnectionException tooMany when tooMany.Message.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase):
                    return new(SshErrorKind.AuthenticationFailed,
                        $"{request.Host} rejected the sign-in as {request.Username} (tried {Offered(request)}) and closed the connection. " +
                        "Check the username and that the key is authorized on the server (~/.ssh/authorized_keys).", exception);
                // SSH.NET: "No matching key exchange algorithm (server offers diffie-hellman-group1-sha1)".
                case SshException negotiation when negotiation.Message.StartsWith("No matching ", StringComparison.Ordinal):
                    return new(SshErrorKind.AlgorithmMismatch, NoCommonAlgorithm(negotiation.Message, request, target), exception);
                case SshOperationTimeoutException or TimeoutException:
                    return new(SshErrorKind.Timeout, $"Timed out connecting to {target}.", exception);
                case SocketException socket:
                    return MapSocket(socket, request, target, exception);
                case SshConnectionException connection:
                    return new(SshErrorKind.ConnectionLost, $"SSH connection to {target} failed: {connection.Message}", exception);
            }
        }
        return new(SshErrorKind.Other, $"Could not connect to {target}: {exception.Message}", exception);
    }

    /// <summary>Short description of why an established connection ended.</summary>
    public static string DescribeLoss(Exception exception) => (exception switch
    {
        SocketException socket => socket.SocketErrorCode switch
        {
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "the connection was reset",
            SocketError.TimedOut => "the connection timed out",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => "the network is unreachable",
            _ => socket.Message,
        },
        SshConnectionException connection => connection.Message,
        _ => exception.Message,
    }).TrimEnd('.'); // callers end the sentence

    private static SshSessionException MapSocket(SocketException socket, SshConnectRequest request, string target, Exception original) =>
        socket.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery =>
                new(SshErrorKind.DnsFailure, $"Could not resolve host \"{request.Host}\".", original),
            SocketError.ConnectionRefused =>
                new(SshErrorKind.ConnectionRefused, $"Connection refused by {target}. Is an SSH server listening on that port?", original),
            SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.NetworkDown =>
                new(SshErrorKind.HostUnreachable, $"{request.Host} is unreachable.", original),
            SocketError.TimedOut =>
                new(SshErrorKind.Timeout, $"Timed out connecting to {target}.", original),
            SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown =>
                new(SshErrorKind.ConnectionLost, $"{target} closed the connection.", original),
            _ => new(SshErrorKind.Other, $"Network error connecting to {target}: {socket.Message}", original),
        };

    private static string Offered(SshConnectRequest request) =>
        (string.IsNullOrWhiteSpace(request.PrivateKey), string.IsNullOrEmpty(request.Password)) switch
        {
            (false, false) => "private key and password",
            (false, true) => "private key",
            (true, false) => "password",
            _ => "no key or password",
        };

    private static string NoCommonAlgorithm(string message, SshConnectRequest request, string target)
    {
        string detail = message["No matching ".Length..].TrimEnd('.');
        return request.LegacyAlgorithms
            ? $"{target} and this app have no algorithm in common: no matching {detail}."
            : $"{request.Host} only supports older, weaker algorithms (no matching {detail}). " +
              "If you trust this server, turn on \"Legacy algorithms\" in the host's settings.";
    }

    // SSH.NET formats this as "No suitable authentication method found to complete authentication (publickey,password)."
    private static string NoSuitableMethod(string message, SshConnectRequest request)
    {
        int open = message.LastIndexOf('(');
        int close = message.LastIndexOf(')');
        if (open < 0 || close <= open)
            return $"{request.Host} does not accept the provided kind of credentials.";
        string allowed = message[(open + 1)..close];
        if (allowed == "publickey" && string.IsNullOrWhiteSpace(request.PrivateKey))
            return $"{request.Host} only accepts sign-in with a key. Give this host an identity with a private key.";
        return $"{request.Host} does not accept the provided kind of credentials (server allows: {allowed}).";
    }
}
