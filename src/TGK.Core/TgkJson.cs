using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core;

/// <summary>Shared JSON settings (camelCase, indented, enums as strings) and atomic file helpers.</summary>
public static class TgkJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Reads and deserializes <paramref name="path"/>; returns default when the file does not exist.</summary>
    internal static async Task<T?> ReadFileAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
    }

    internal static T? ReadFile<T>(string path)
    {
        if (!File.Exists(path))
            return default;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    /// <summary>
    /// Writes to a temp file next to <paramref name="path"/> and renames it over the target, so readers
    /// never see a half-written file. On Unix the file is created user-only (0600).
    /// </summary>
    internal static async Task WriteFileAtomicAsync<T>(string path, T value, CancellationToken ct = default)
    {
        string temp = PrepareTempPath(path);
        try
        {
            await using (var stream = new FileStream(temp, TempFileOptions(async: true)))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    internal static void WriteFileAtomic<T>(string path, T value)
    {
        string temp = PrepareTempPath(path);
        try
        {
            using (var stream = new FileStream(temp, TempFileOptions(async: false)))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string PrepareTempPath(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static FileStreamOptions TempFileOptions(bool async)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = async ? FileOptions.Asynchronous : FileOptions.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
