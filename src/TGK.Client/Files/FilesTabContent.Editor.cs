using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Main;
using TGK.Core.Files;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

// Text files opened in TGK's own editor, beside this pane (FileEditorTabContent); other files open in a program here.
public sealed partial class FilesTabContent
{
    // Files that are not text: they open in a program on this computer.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".psd", ".heic", ".avif",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".epub",
        ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".tar", ".zst", ".lz4", ".jar", ".war", ".deb", ".rpm", ".apk",
        ".dmg", ".iso", ".img", ".qcow2", ".vmdk", ".vdi", ".vhd", ".vhdx",
        ".mp3", ".mp4", ".mkv", ".avi", ".mov", ".wav", ".flac", ".ogg", ".webm", ".m4a", ".aac", ".opus",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".o", ".a", ".class", ".pyc", ".wasm", ".msi",
        ".ttf", ".otf", ".woff", ".woff2", ".db", ".sqlite", ".sqlite3", ".mdb", ".pcap", ".kdbx", ".p12", ".pfx", ".der", ".jks",
    };

    /// <summary>Panes and editors showing <paramref name="path"/> (or a file in it) of <paramref name="fs"/> refresh.</summary>
    internal static void NotifyChanged(IFileSystem fs, string path) => FolderChanged?.Invoke(fs, path);

    /// <summary>Whether a double-click opens the file in TGK's editor (else in a program on this computer).</summary>
    public static bool OpensInEditor(FileEntry entry) => !entry.IsDirectory && !BinaryExtensions.Contains(Path.GetExtension(entry.Name));

    /// <summary>Opens a file in TGK's editor beside this pane (or shows the editor that already has it).</summary>
    private void OpenInEditor(FileEntry entry, EditorMode mode)
    {
        if (!Connected || _fs is not { } fs || entry.IsDirectory)
            return;
        if (entry.IsBrokenLink)
        {
            Host.ShowToast($"{FileFormat.Printable(entry.Name)} points to {FileFormat.Printable(entry.LinkTarget ?? "?")}, which does not exist.", ToastKind.Error);
            return;
        }
        if (Host.Tabs.OfType<FileEditorTabContent>().FirstOrDefault(t => t.Path == entry.Path && SamePlace(t.FileSystem, fs)) is { } open)
        {
            if (open.Split is not null && open.Split == Split)
                Host.ActivateTab(open);
            else
                Host.ShowBeside(open);
            return;
        }
        var editor = new FileEditorTabContent(fs, Session, entry.Path, PlaceName, mode, IsLocal ? null : CommandConnector(), () => Session);
        // After the click (or key) that opened it has been handled: that click would make this pane active again.
        UiThread.Post(() =>
        {
            if (!_closing)
                Host.OpenBeside(this, editor);
        });
    }

    /// <summary>
    /// Opens a command connection to this pane's host, with its credentials (for a direct copy, or for sudo in the
    /// editor); its prompts (host key, one-time codes) come to this pane.
    /// </summary>
    private Func<CancellationToken, Task<RemoteConnection>> CommandConnector()
    {
        int generation = _generation;
        return async ct =>
        {
            if (Session is not { } session)
                throw new SshSessionException(SshErrorKind.ConnectionLost, $"The connection to {PlaceName} is closed.");
            var verifier = new KnownHostsVerifier(Host.Services.Vault, (info, token) =>
                UiThread.InvokeAsync(() => AskAsync(generation, _ => HostKeyDialog.ShowAsync(Host, info, token))));
            var exec = new RemoteConnection(verifier, SignInPrompts(generation));
            try
            {
                await exec.ConnectAsync(session.Request, ct);
                return exec;
            }
            catch
            {
                exec.Dispose();
                throw;
            }
        };
    }
}
