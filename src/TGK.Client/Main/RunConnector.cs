using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TGK.Client.Dialogs;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Main;

/// <summary>
/// Reaches the hosts of a run tab (<see cref="RunCommandTabContent"/>) and runs its command there: one connection per
/// host and run, through the host's jump hosts with its credentials and host-key checks, closed when the host is done.
/// What the vault lacks is asked for, one prompt at a time: usernames, passwords of the hosts and their jump hosts, sudo
/// passwords (the saved password is tried first), unknown host keys. What is typed is kept in memory until the tab
/// closes. A password typed with "use it for the other hosts" is tried once on each other host of the tab that has none;
/// where it fails, that host asks for its own.
/// </summary>
public sealed class RunConnector : IDisposable
{
    private readonly MainView _view;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _typed = []; // what was typed, by kind and address (never saved)
    private readonly SemaphoreSlim _asking = new(1, 1); // one prompt at a time (usernames, passwords, host keys)
    private string? _sharedPassword, _sharedSudo;
    private volatile bool _disposed;

    public RunConnector(MainView view) => _view = view;

    /// <summary>
    /// Connects to <paramref name="host"/> and runs <paramref name="command"/> there, as root through sudo when
    /// <paramref name="sudo"/>, with stdin closed (a command waiting for input gets end-of-file, not a hang). Reports
    /// <see cref="HostRunState.Running"/> once connected. Any thread; see <see cref="MultiHostRun.HostCommand"/>.
    /// </summary>
    /// <param name="shareable">Other hosts take part: password prompts offer to use the password for them too.</param>
    public async Task<CommandResult> RunAsync(VaultData vault, HostEntry host, string command, bool sudo, TimeSpan timeout, bool shareable,
        Action<HostRunState> progress, CancellationToken ct)
    {
        using RemoteConnection connection = await ConnectAsync(vault, host, shareable, ct).ConfigureAwait(false);
        progress(HostRunState.Running);
        if (!sudo)
            return await connection.RunAsync(command, timeout, input: [], ct: ct).ConfigureAwait(false);
        SshConnectRequest request = connection.Request!;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return await Sudo.RunAsync(connection, command, timeout, (error, token) => SudoPasswordAsync(host, request, tried, error, shareable, token), ct)
                .ConfigureAwait(false);
        }
        catch (SudoException ex) when (ex.Failure == SudoFailure.Declined)
        {
            throw new OperationCanceledException("No sudo password was entered.", ex);
        }
    }

    /// <summary>The tab closed: forgets what was typed; prompts still waiting give up.</summary>
    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            _typed.Clear();
            _sharedPassword = _sharedSudo = null;
        }
    }

    private async Task<RemoteConnection> ConnectAsync(VaultData vault, HostEntry host, bool shareable, CancellationToken ct)
    {
        // A command needs neither the terminal's tunnels nor its startup command or environment.
        SshConnectRequest saved = SshConnectRequest.ForHost(vault, host) with { Tunnels = [], StartupCommand = null, Environment = [] };
        var hops = new List<SshConnectRequest>();
        foreach (SshConnectRequest hop in saved.JumpChain)
            hops.Add(await CompleteAsync(hop, $"jump host {hop.Name ?? hop.Host} of {host.DisplayName}", null, false, ct).ConfigureAwait(false));
        SshConnectRequest request = await CompleteAsync(saved with { JumpChain = hops }, host.DisplayName, null, shareable, ct).ConfigureAwait(false);

        for (int attempt = 0; ; attempt++)
        {
            var connection = new RemoteConnection(new OneAtATime(this,
                    new KnownHostsVerifier(_view.Services.Vault, (info, token) => AskAsync(view => HostKeyDialog.ShowAsync(view, info, token), false, token))),
                (prompt, token) => AskAsync(view => SignInPromptDialog.ShowAsync(view,
                    $"{prompt.Username}@{(prompt.Port == 22 ? prompt.Host : $"{prompt.Host}:{prompt.Port}")}", prompt, true, token), (string?)null, token));
            try
            {
                await connection.ConnectAsync(request, ct).ConfigureAwait(false);
                return connection;
            }
            catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && attempt < 2 && !ct.IsCancellationRequested
                && (ex.JumpHostIndex is int jump ? jump < saved.JumpChain.Count && Typed(saved.JumpChain[jump]) : ex.JumpHostIndex is null && Typed(saved)))
            {
                // A password typed here (or shared from another host) was wrong: ask for this one again.
                connection.Dispose();
                if (ex.JumpHostIndex is int index)
                {
                    Forget(Key(request.JumpChain[index]));
                    SshConnectRequest hop = await CompleteAsync(saved.JumpChain[index] with { Username = request.JumpChain[index].Username },
                        $"jump host {saved.JumpChain[index].Name ?? saved.JumpChain[index].Host} of {host.DisplayName}", ex.Message, false, ct).ConfigureAwait(false);
                    var chain = new List<SshConnectRequest>(request.JumpChain) { [index] = hop };
                    request = request with { JumpChain = chain };
                }
                else
                {
                    Forget(Key(request));
                    request = await CompleteAsync(saved with { Username = request.Username, JumpChain = request.JumpChain }, host.DisplayName, ex.Message, shareable, ct)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        // The vault has neither a password nor a key for it: the password used was typed.
        static bool Typed(SshConnectRequest hop) => string.IsNullOrEmpty(hop.Password) && string.IsNullOrWhiteSpace(hop.PrivateKey);
    }

    /// <summary>
    /// Fills in what <paramref name="hop"/> lacks to sign in: a username, and a password when it has neither one nor a
    /// key (typed earlier, shared, or asked now; <paramref name="error"/> says why the last one failed, and then a
    /// shared password is not tried again).
    /// </summary>
    private async Task<SshConnectRequest> CompleteAsync(SshConnectRequest hop, string what, string? error, bool shareable, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hop.Username))
        {
            string userKey = $"user|{hop.Host}:{hop.Port}";
            string? user = await AskOnceAsync(userKey, () => Remembered(userKey), view => new PromptDialog(view, "Username required", what,
                $"{what} has no saved username.", "Username", "Continue", guardEnter: true).ShowAsync(ct), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(user))
                throw new OperationCanceledException("No username was entered.");
            hop = hop with { Username = user.Trim() };
        }
        if (!string.IsNullOrWhiteSpace(hop.PrivateKey) || !string.IsNullOrEmpty(hop.Password))
            return hop;
        string target = Address(hop);
        string key = Key(hop);
        string? password = await AskOnceAsync(key, () => Remembered(key) ?? (shareable && error is null ? Shared(sudo: false) : null),
            view => Ask(view, "Password required", target, $"{what} has no saved password. It is used for this tab only and kept until it closes.",
                "Connect", error, shareable, sudo: false, ct), ct).ConfigureAwait(false);
        if (password is null)
            throw new OperationCanceledException("No password was entered.");
        return hop with { Password = password };
    }

    // The sudo password to try next: each of one typed for this host's sudo, the shared one and the login password
    // once, then one asked for now (again after each refusal, with why).
    private async Task<string?> SudoPasswordAsync(HostEntry host, SshConnectRequest request, HashSet<string> tried, string? error, bool shareable,
        CancellationToken ct)
    {
        string key = "sudo|" + Address(request);
        string target = Address(request);
        string? password = await AskOnceAsync(key, Untried, view => Ask(view, "sudo password", target,
            $"The command runs as root on {host.DisplayName}. Enter the sudo password of {request.Username}; it is kept until this tab closes.",
            "Continue", error, shareable, sudo: true, ct), ct).ConfigureAwait(false);
        if (password is not null)
            tried.Add(password);
        return password;

        string? Untried()
        {
            foreach (string? candidate in new[] { Remembered(key), shareable ? Shared(sudo: true) : null, request.Password })
            {
                if (!string.IsNullOrEmpty(candidate) && !tried.Contains(candidate))
                    return candidate;
            }
            return null;
        }
    }

    // A password prompt; the answer is shared with the other hosts when its check box is ticked.
    private async Task<string?> Ask(MainView view, string title, string target, string message, string ok, string? error, bool shareable, bool sudo,
        CancellationToken ct)
    {
        var dialog = new PromptDialog(view, title, target, message, "Password", ok, isPassword: true, error: error, guardEnter: true,
            option: shareable ? "Use it for the other hosts of this tab too" : null);
        string? password = await dialog.ShowAsync(ct);
        if (password is not null && dialog.OptionChecked)
        {
            lock (_gate)
            {
                if (sudo)
                    _sharedSudo = password;
                else
                    _sharedPassword = password;
            }
        }
        return password;
    }

    /// <summary>
    /// What <paramref name="known"/> gives (typed or shared earlier), else asked now and kept under <paramref name="key"/>:
    /// one prompt at a time, so another host's prompt may have answered it meanwhile. Null when the user cancelled.
    /// </summary>
    private async Task<string?> AskOnceAsync(string key, Func<string?> known, Func<MainView, Task<string?>> ask, CancellationToken ct)
    {
        if (known() is { } answer)
            return answer;
        try
        {
            await _asking.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            if (known() is { } answered)
                return answered;
            answer = await AskAsync(ask, null, ct).ConfigureAwait(false);
            if (answer is not null && !_disposed)
            {
                lock (_gate)
                    _typed[key] = answer;
            }
            return answer;
        }
        finally
        {
            _asking.Release();
        }
    }

    /// <summary>Shows a dialog on the main view (drawing attention to the window); <paramref name="fallback"/> once the tab or the view is gone.</summary>
    private async Task<T> AskAsync<T>(Func<MainView, Task<T>> show, T fallback, CancellationToken ct)
    {
        Task<T> asked = UiThread.InvokeAsync(() =>
        {
            if (_disposed || _view.IsTornDown)
                return Task.FromResult(fallback);
            AppWindow.RequestAttention();
            return show(_view);
        });
        try
        {
            return await asked.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
    }

    private string? Remembered(string key)
    {
        lock (_gate)
            return _typed.TryGetValue(key, out string? value) ? value : null;
    }

    private void Forget(string key)
    {
        lock (_gate)
            _typed.Remove(key);
    }

    private string? Shared(bool sudo)
    {
        lock (_gate)
            return sudo ? _sharedSudo : _sharedPassword;
    }

    /// <summary>
    /// Unknown host keys are shown one at a time, between the other prompts; a server whose key another host's prompt
    /// trusted meanwhile (e.g. a jump host they share) is not asked about again.
    /// </summary>
    private sealed class OneAtATime(RunConnector owner, IHostKeyVerifier verifier) : IHostKeyVerifier
    {
        public async Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct)
        {
            if (owner._view.Services.Vault.Current.FindKnownHost(info.Host, info.Port)?.FingerprintSha256 == info.FingerprintSha256)
                return true;
            await owner._asking.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await verifier.VerifyAsync(info, ct).ConfigureAwait(false); // looks in the vault again first
            }
            finally
            {
                owner._asking.Release();
            }
        }
    }

    private static string Key(SshConnectRequest hop) => "password|" + Address(hop);

    private static string Address(SshConnectRequest hop) => $"{hop.Username}@{(hop.Port == 22 ? hop.Host : $"{hop.Host}:{hop.Port}")}";
}
