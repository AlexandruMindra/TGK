using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TGK.Core.Services;

/// <summary>What "keep me signed in" stores on the device. <see cref="VaultKey"/> decrypts the vault: never log it.</summary>
public sealed record DeviceCredentials(string ServerUrl, string Username, string UserId, string SessionId, string Token, byte[] VaultKey);

/// <summary>Holds at most one <see cref="DeviceCredentials"/>. Calls are synchronous (may start a process): call off the UI thread.</summary>
public interface IDeviceCredentialStore
{
    DeviceCredentials? Load();
    void Save(DeviceCredentials credentials);
    void Clear();
}

/// <summary>
/// Windows: a DPAPI-protected (current user) file. Linux/macOS: the system keyring through libsecret's
/// <c>secret-tool</c> (secret passed on stdin); without a usable keyring, a user-only (0600) file, with a warning.
/// </summary>
public sealed class DeviceCredentialStore : IDeviceCredentialStore
{
    public const string FileName = "device.json";

    private static readonly byte[] DpapiEntropy = "tgk/device/v1"u8.ToArray();
    private static readonly TimeSpan SecretToolTimeout = TimeSpan.FromSeconds(20);
    private static bool s_warnedPlainFile;

    private readonly string? _secretTool;
    private readonly string[] _attributes;

    /// <param name="baseDirectory">Directory for the file; defaults to <see cref="AppPaths.ConfigDirectory"/>.</param>
    /// <param name="secretTool">The <c>secret-tool</c> executable, or null to use only the file (ignored on Windows).</param>
    public DeviceCredentialStore(string? baseDirectory = null, string? secretTool = "secret-tool")
    {
        string directory = Path.GetFullPath(baseDirectory ?? AppPaths.ConfigDirectory);
        FilePath = Path.Combine(directory, FileName);
        _secretTool = OperatingSystem.IsWindows() ? null : secretTool;
        _attributes = ["application", "tgk", "profile", directory];
    }

    public string FilePath { get; }

    public DeviceCredentials? Load()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ProtectedFile? file = TgkJson.ReadFile<ProtectedFile>(FilePath);
                return file is null ? null
                    : JsonSerializer.Deserialize<DeviceCredentials>(ProtectedData.Unprotect(file.Data, DpapiEntropy, DataProtectionScope.CurrentUser), TgkJson.Options);
            }
            if (RunSecretTool(["lookup", .. _attributes], null) is { Length: > 0 } secret)
                return JsonSerializer.Deserialize<DeviceCredentials>(secret, TgkJson.Options);
            return TgkJson.ReadFile<DeviceCredentials>(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            CoreLog.Warn($"Ignoring unreadable stored sign-in: {ex.Message}");
            return null;
        }
    }

    public void Save(DeviceCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (OperatingSystem.IsWindows())
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(credentials, TgkJson.Options);
            TgkJson.WriteFileAtomic(FilePath, new ProtectedFile(ProtectedData.Protect(json, DpapiEntropy, DataProtectionScope.CurrentUser)));
            CryptographicOperations.ZeroMemory(json);
            return;
        }

        string secret = JsonSerializer.Serialize(credentials, TgkJson.Options);
        if (RunSecretTool(["store", "--label=TGK sign-in", .. _attributes], secret) is not null)
        {
            DeleteFile();
            return;
        }
        if (!s_warnedPlainFile)
        {
            s_warnedPlainFile = true;
            CoreLog.Warn($"No system keyring (secret-tool) available; keeping the sign-in in {FilePath}, readable only by you.");
        }
        TgkJson.WriteFileAtomic(FilePath, credentials);
    }

    public void Clear()
    {
        RunSecretTool(["clear", .. _attributes], null);
        DeleteFile();
    }

    private void DeleteFile()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not delete the stored sign-in: {ex.Message}");
        }
    }

    /// <summary>Runs secret-tool; returns its trimmed stdout, or null when it is missing, fails or times out.</summary>
    private string? RunSecretTool(IReadOnlyList<string> arguments, string? stdin)
    {
        if (_secretTool is null)
            return null;
        var info = new ProcessStartInfo(_secretTool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(info)!;
            process.StandardInput.Write(stdin ?? "");
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SecretToolTimeout))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.Result.Trim() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null; // not installed
        }
    }

    private sealed record ProtectedFile(byte[] Data);
}
