using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>
/// Outcome of moving the local vault to a server account. <see cref="Login"/> carries the sign-in or registration
/// error (e.g. <see cref="VaultError.TotpRequired"/>); on success the client is in server mode, <see cref="Uploaded"/>
/// tells whether the server acknowledged every item, and <see cref="LocalVaultRemoved"/> whether the local vault was
/// deleted (never while something is not uploaded or <see cref="MergeCounts.Conflicts"/> kept the account's version).
/// </summary>
public sealed record MigrationResult(LoginResult Login, MergeCounts Counts, bool Uploaded, bool LocalVaultRemoved)
{
    public bool Success => Login.Success;

    internal static MigrationResult Fail(LoginResult login) => new(login, MergeCounts.None, false, false);
}

/// <summary>
/// Moves vault data between the local vault and server accounts, and to and from encrypted backup files, for the
/// client's <see cref="RoutingVaultService"/>.
/// </summary>
public sealed class VaultMigration(RoutingVaultService vault)
{
    /// <summary>Extension of backup files (the file dialog's filter; the format itself does not depend on it).</summary>
    public const string BackupExtension = ".tgkbackup";

    /// <summary>
    /// Local → a NEW account: registers (with the usual authenticator enrollment) using the local vault key as the
    /// account's vault key and uploads every local item as it is. On success the client is in server mode and the
    /// local vault is locked; with <paramref name="removeLocalVault"/> it is also deleted, but only once the server
    /// acknowledged every item. Requires the unlocked local vault.
    /// </summary>
    public async Task<MigrationResult> LocalToNewAccountAsync(string serverUrl, string username, string password, string? inviteCode,
        string totpSecret, string totpCode, bool keepSignedIn, bool removeLocalVault, CancellationToken ct = default)
    {
        LocalVaultService local = RequireLocal();
        (byte[] vaultKey, List<KeyValuePair<string, byte[]>> items) = await local.ExportSealedAsync().ConfigureAwait(false);
        try
        {
            LoginResult login = await vault.Remote.RegisterAsync(serverUrl, username, password, inviteCode, totpSecret, totpCode, keepSignedIn, vaultKey, items, ct).ConfigureAwait(false);
            if (!login.Success)
                return MigrationResult.Fail(login);
            vault.Remote.ImportLastConnected(local.Current.Hosts);
            return await FinishAsync(new MergeCounts(items.Count, 0, 0), removeLocalVault).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    /// <summary>
    /// Local → an EXISTING account: signs in and merges the local data into the account's vault (re-encrypted with its
    /// key; see <see cref="VaultMerge"/>: what the account already has wins). Then like
    /// <see cref="LocalToNewAccountAsync"/>, except that the local vault is kept when some of its entries conflicted
    /// (their local version is only there). Requires the unlocked local vault.
    /// </summary>
    public async Task<MigrationResult> LocalToExistingAccountAsync(string serverUrl, string username, string password, string? totpCode,
        bool keepSignedIn, bool removeLocalVault, CancellationToken ct = default)
    {
        LocalVaultService local = RequireLocal();
        await local.SyncAsync(ct).ConfigureAwait(false);
        LoginResult login = await vault.Remote.LoginAsync(serverUrl, username, password, totpCode, keepSignedIn, ct).ConfigureAwait(false);
        if (!login.Success)
            return MigrationResult.Fail(login);
        MergeCounts counts;
        try
        {
            counts = await MergeAsync(vault.Remote, local.Current).ConfigureAwait(false);
            vault.Remote.ImportLastConnected(local.Current.Hosts);
        }
        catch
        {
            await vault.Remote.LogoutAsync().ConfigureAwait(false);
            throw;
        }
        return await FinishAsync(counts, removeLocalVault).ConfigureAwait(false);
    }

    /// <summary>
    /// Server → local ("save an offline copy"): writes the account's current data to a local vault protected by
    /// <paramref name="masterPassword"/> (under a new vault key), replacing an existing local vault only with
    /// <paramref name="replace"/> (confirm first: see <see cref="LocalVaultService.Exists"/>). The account and the
    /// current mode are unchanged; the copy stays locked.
    /// </summary>
    public Task<LoginResult> SaveOfflineCopyAsync(string masterPassword, bool replace, CancellationToken ct = default)
    {
        if (vault.Mode == VaultMode.Local || !vault.IsLoggedIn)
            throw new InvalidOperationException("Sign in to a server account first.");
        return vault.Local.CreateAsync(masterPassword, vault.Current, replace, unlock: false, keepUnlocked: false, ct);
    }

    /// <summary>Writes the current vault (either mode) to an encrypted backup file protected by <paramref name="backupPassword"/>.</summary>
    /// <exception cref="VaultException"><see cref="VaultError.ValidationFailed"/> for a short password, <see cref="VaultError.Storage"/> when the file cannot be written.</exception>
    public Task ExportBackupAsync(string path, string backupPassword, CancellationToken ct = default)
    {
        if (LocalVaultService.CheckNewPassword(backupPassword) is { } problem)
            throw new VaultException(VaultError.ValidationFailed, problem);
        if (!vault.IsLoggedIn)
            throw new InvalidOperationException("Not logged in.");
        VaultData data = vault.Current;
        return Task.Run(() => VaultBackup.Write(path, data, backupPassword, vault.Local.Options.NewKdf), ct);
    }

    /// <summary>
    /// Merges a backup file into the current vault (either mode; see <see cref="VaultMerge"/>). Items of a kind this
    /// version does not know count as skipped.
    /// </summary>
    /// <exception cref="VaultException">
    /// <see cref="VaultError.InvalidCredentials"/> for a wrong password, <see cref="VaultError.Storage"/> for a file that
    /// is unreadable, not a backup, too large or damaged, <see cref="VaultError.ValidationFailed"/> for an entry too
    /// large for a vault (nothing is imported then).
    /// </exception>
    public async Task<MergeCounts> ImportBackupAsync(string path, string backupPassword, CancellationToken ct = default)
    {
        if (!vault.IsLoggedIn)
            throw new InvalidOperationException("Not logged in.");
        (VaultData incoming, int unknown) = await Task.Run(() => VaultBackup.Read(path, backupPassword), ct).ConfigureAwait(false);
        MergeCounts counts = await MergeAsync(vault, incoming).ConfigureAwait(false);
        return counts with { Skipped = counts.Skipped + unknown };
    }

    private LocalVaultService RequireLocal() =>
        vault.Mode == VaultMode.Local && vault.Local.IsLoggedIn ? vault.Local : throw new InvalidOperationException("Unlock the local vault first.");

    /// <summary>
    /// Uploads what is still pending, switches to server mode (locking the local vault) and optionally deletes it: only
    /// when everything reached the server and no local entry lost a conflict.
    /// </summary>
    private async Task<MigrationResult> FinishAsync(MergeCounts counts, bool removeLocalVault)
    {
        RemoteVaultService remote = vault.Remote;
        try
        {
            await remote.SyncAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Signed out meanwhile: handled below.
        }
        if (!remote.IsLoggedIn)
            return MigrationResult.Fail(LoginResult.Fail(VaultError.Unauthorized, "The server ended the session during the upload; the local vault is unchanged."));
        bool uploaded = remote.PendingChanges == 0 && remote.LastError is null;
        await vault.SwitchToAsync(remote).ConfigureAwait(false);
        bool removed = false;
        if (removeLocalVault && uploaded && counts.Conflicts == 0)
        {
            try
            {
                vault.Local.DeleteVault();
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CoreLog.Warn($"Could not delete the local vault: {ex.Message}");
            }
        }
        return new MigrationResult(LoginResult.Ok, counts, uploaded, removed);
    }

    /// <exception cref="VaultException"><see cref="VaultError.ValidationFailed"/>: an entry is too large for a vault (nothing is merged).</exception>
    private static async Task<MergeCounts> MergeAsync(IVaultEditor editor, VaultData incoming)
    {
        MergeCounts counts = MergeCounts.None;
        try
        {
            await editor.EditAsync(target => counts = VaultMerge.Merge(target, incoming)).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new VaultException(VaultError.ValidationFailed, ex.Message, ex);
        }
        return counts;
    }
}
