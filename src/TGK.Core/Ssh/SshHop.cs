using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TGK.Core.Ssh;

/// <summary>
/// One SSH connection of a (possibly jumped) route: the client for one hop, with its key loaded and its sign-in
/// methods set up, and for a jump host the local forwarding to the next hop. Shared by <see cref="SshSession"/> (a
/// terminal) and <see cref="RemoteConnection"/> (commands and files for agents).
/// </summary>
/// <remarks>
/// A hop behind a jump host connects to the previous hop's local port (SSH.NET cannot run SSH over a channel stream);
/// its host key is still verified against its own host and port by the owner (<see cref="SshClient.HostKeyReceived"/>).
/// </remarks>
internal sealed class SshHop : IDisposable
{
    // Hidden prompts that ask for the account password (PAM's "Password: " and common translations). Prompts that also
    // mention a code or token (e.g. "One-time password (OATH)") are second factors and never get the password.
    private static readonly string[] PasswordWords =
        ["password", "passwort", "passwd", "mot de passe", "contraseña", "senha", "wachtwoord", "hasło", "пароль", "lösenord", "salasana", "heslo", "密码", "密碼", "パスワード", "비밀번호"];
    private static readonly string[] SecondFactorWords = ["one-time", "one time", "otp", "oath", "code", "token", "verification", "factor"];

    private readonly PrivateKeyFile? _keyFile;
    private readonly Func<InteractivePrompt, string?>? _askUser;

    private SshHop(SshConnectRequest request, bool isFinal, PrivateKeyFile? keyFile, Func<InteractivePrompt, string?>? askUser)
    {
        Request = request;
        IsFinal = isFinal;
        _keyFile = keyFile;
        _askUser = askUser;
        Client = null!; // set by Create
    }

    public SshConnectRequest Request { get; }
    public bool IsFinal { get; }
    public SshClient Client { get; private set; }

    /// <summary>The fingerprint the owner trusted for this hop. Guarded by the owner's lock.</summary>
    public string? TrustedFingerprint { get; set; }

    /// <summary>The local port leading to the next hop (jump hosts only).</summary>
    public ForwardedPortLocal? Forward { get; private set; }

    /// <summary>The local port this hop connects through (behind a jump host), or null for a direct connection.</summary>
    public ForwardedPortLocal? Via { get; private set; }

    /// <summary>
    /// Loads the key and creates the client for one hop, connecting directly or, behind a jump host, to
    /// <paramref name="via"/>'s local port.
    /// </summary>
    /// <param name="askUser">
    /// Answers keyboard-interactive questions the stored password can't (one-time codes and the like); returns null
    /// when the user declined. Runs on an SSH.NET worker thread while the sign-in waits. Without it such a server fails
    /// with <see cref="SshErrorKind.UnsupportedAuthMethod"/>.
    /// </param>
    /// <param name="promptTimeout">Added to the connect timeout while key exchange and sign-in may wait for prompts.</param>
    public static SshHop Create(SshConnectRequest request, bool isFinal, ForwardedPortLocal? via, Func<InteractivePrompt, string?>? askUser, TimeSpan promptTimeout)
    {
        PrivateKeyFile? keyFile = string.IsNullOrWhiteSpace(request.PrivateKey)
            ? null
            : KeyInspector.Load(request.PrivateKey, request.Passphrase);
        var hop = new SshHop(request, isFinal, keyFile, askUser) { Via = via };
        ConnectionInfo info = hop.CreateConnectionInfo(promptTimeout);
        hop.Client = new SshClient(info)
        {
            KeepAliveInterval = request.KeepAlive > TimeSpan.Zero ? request.KeepAlive : Timeout.InfiniteTimeSpan,
        };
        return hop;
    }

    /// <summary>
    /// New connection details for one more connection to this hop along the same route (e.g. an SFTP client next to the
    /// command client): the same credentials and algorithms, fresh sign-in methods (SSH.NET keeps state in them).
    /// </summary>
    public ConnectionInfo CreateConnectionInfo(TimeSpan promptTimeout)
    {
        SshConnectRequest request = Request;
        string username = request.Username.Trim();

        // SSH.NET tries the methods the server allows in the order listed here.
        ConnectionInfo? info = null;
        var methods = new List<AuthenticationMethod>();
        if (_keyFile is not null)
            methods.Add(new PrivateKeyAuthenticationMethod(username, new SingleSignatureKeySource(_keyFile, () => info?.ServerVersion)));
        if (!string.IsNullOrEmpty(request.Password))
            methods.Add(new PasswordAuthenticationMethod(username, request.Password));
        if (!string.IsNullOrEmpty(request.Password) || _askUser is not null)
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(username);
            bool passwordSent = false; // the prompts of one sign-in arrive one request at a time
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (AuthenticationPrompt prompt in e.Prompts)
                    prompt.Response = AnswerPrompt(e.Instruction, prompt, ref passwordSent);
            };
            methods.Add(interactive);
        }

        info = Via is null
            ? new ConnectionInfo(request.Host.Trim(), request.Port, username, methods.ToArray())
            : new ConnectionInfo(Via.BoundHost, (int)Via.BoundPort, username, methods.ToArray());
        // SSH.NET waits for key exchange and authentication, which include the prompts, with this timeout.
        info.Timeout = request.ConnectTimeout + promptTimeout;
        if (!request.LegacyAlgorithms)
            SshAlgorithms.RemoveLegacy(info);
        return info;
    }

    // Runs on an SSH.NET worker thread; throwing fails the keyboard-interactive method (and so the sign-in).
    private string AnswerPrompt(string? instruction, AuthenticationPrompt prompt, ref bool passwordSent)
    {
        SshConnectRequest request = Request;
        if (!prompt.IsEchoed && !string.IsNullOrEmpty(request.Password) && IsPasswordPrompt(prompt.Request))
        {
            // The password is offered once: a server that asks again has rejected it.
            if (passwordSent)
                throw new SshAuthenticationException("Permission denied (keyboard-interactive).");
            passwordSent = true;
            return request.Password;
        }

        if (_askUser is null)
        {
            throw new SshSessionException(SshErrorKind.UnsupportedAuthMethod,
                $"{request.Host} asks \"{prompt.Request.Trim()}\" to sign in, which can't be answered here.");
        }
        var question = new InteractivePrompt(request.Host, request.Port, request.Username, instruction ?? "", prompt.Request, prompt.IsEchoed);
        return _askUser(question) ?? throw new SshAuthenticationException("The sign-in prompt was not answered.");
    }

    internal static bool IsPasswordPrompt(string text) =>
        Array.Exists(PasswordWords, w => text.Contains(w, StringComparison.OrdinalIgnoreCase))
        && !Array.Exists(SecondFactorWords, w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Listens on an ephemeral loopback port whose connections this hop forwards to <paramref name="next"/>.</summary>
    public void ForwardTo(SshConnectRequest next)
    {
        var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), 0, next.Host.Trim(), (uint)next.Port);
        Client.AddForwardedPort(forward);
        forward.Start();
        Forward = forward;
    }

    public void Dispose()
    {
        IList<AuthenticationMethod> methods = Client.ConnectionInfo.AuthenticationMethods; // unreadable once disposed
        // Best effort: each step runs even if an earlier one fails on a half-closed connection.
        Try(Client.Dispose);
        foreach (AuthenticationMethod method in methods)
            Try(() => (method as IDisposable)?.Dispose());
        Try(() => _keyFile?.Dispose());
    }

    internal static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Teardown errors are irrelevant: the connection is gone either way.
        }
    }
}
