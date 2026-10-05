using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Main;
using TGK.Client.Platform;
using TGK.Core.Agents;
using TGK.Core.Files;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

// Menus, file operations, uploads, downloads and edited files of a file pane.
public sealed partial class FilesTabContent
{
    private const long LargeFileBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Raised on the UI thread when a transfer changed a folder (path: the folder, or a file in it), so every pane
    /// showing that folder of that file system refreshes.
    /// </summary>
    private static event Action<IFileSystem, string>? FolderChanged;

    // Transfers whose end was handled: the ones that refreshed the panes, and the failures reported (by the code that
    // started them or by OnTransfersChanged).
    private readonly HashSet<FileTransfer> _announced = [], _reported = [];

    // ---- menus ----

    private void ShowMenu(IReadOnlyList<MenuItem> items, IconButton anchor) =>
        Host.Menu.Show(items, anchor.Transform.Computed.X, anchor.Transform.Computed.Y + anchor.Transform.Computed.Height + 4, 240,
            anchor.Transform.Computed.Height);

    private List<MenuItem> UploadMenu() =>
    [
        new MenuItem { Text = "Upload files…", Icon = "file", Action = UploadFiles },
        new MenuItem { Text = "Upload a folder…", Icon = "folder", Action = UploadFolder },
        MenuItem.Separator,
        new MenuItem { Text = "Or drop files on the window", IsHeader = true },
    ];

    private List<MenuItem> MoreMenu()
    {
        bool live = Connected && _path is not null;
        List<MenuItem> items =
        [
            new MenuItem { Text = "New folder…", Icon = "folder-plus", Hint = "Ctrl+Shift+N", IsEnabled = live, Action = NewFolder },
            new MenuItem { Text = "New file…", Icon = "file-plus", IsEnabled = live, Action = NewFile },
        ];
        if (!IsLocal)
        {
            items.Add(new MenuItem { Text = "Upload files…", Icon = "upload", IsEnabled = live, Action = UploadFiles });
            items.Add(new MenuItem { Text = "Upload a folder…", Icon = "folder", IsEnabled = live, Action = UploadFolder });
        }
        items.Add(new MenuItem { Text = "Open a pane beside…", Icon = "layout-columns", IsEnabled = live, Action = () => ShowMenu(PaneMenu(), _more) });
        items.Add(MenuItem.Separator);
        items.Add(new MenuItem { Text = "Show hidden files", Hint = "Ctrl+H", IsChecked = _list.ShowHidden, Action = ToggleHidden });
        if (!IsLocal)
            items.Add(new MenuItem { Text = "Open terminal here", Icon = "terminal", IsEnabled = _path is not null, Action = OpenTerminalHere });
        items.Add(new MenuItem { Text = "Copy folder path", Icon = "copy", IsEnabled = _path is not null, Action = () => CopyText(_path!) });
        items.Add(new MenuItem { Text = "Go to home folder", Icon = "user", IsEnabled = live, Action = () => Navigate("~") });
        if (!IsLocal)
        {
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem { Text = $"Download folder: {ShortLocal(DownloadFolder())}", IsHeader = true });
            items.Add(new MenuItem { Text = "Change download folder…", Icon = "download", Action = ChangeDownloadFolder });
            items.Add(MenuItem.Separator);
            items.Add(Connected
                ? new MenuItem { Text = "Disconnect", Icon = "logout", Action = Disconnect }
                : new MenuItem { Text = "Reconnect", Icon = "refresh", Action = () => Connect() });
        }
        items.Add(MenuItem.Separator);
        items.AddRange(Host.PaneMenuItems(this));
        return items;
    }

    private List<MenuItem> ListMenu()
    {
        bool live = Connected && _path is not null;
        IReadOnlyList<FileEntry> selected = _list.Selected;
        if (selected.Count == 0)
        {
            var empty = new List<MenuItem>
            {
                new() { Text = "New folder…", Icon = "folder-plus", Hint = "Ctrl+Shift+N", IsEnabled = live, Action = NewFolder },
                new() { Text = "New file…", Icon = "file-plus", IsEnabled = live, Action = NewFile },
            };
            if (!IsLocal)
            {
                empty.Add(new MenuItem { Text = "Upload files…", Icon = "upload", IsEnabled = live, Action = UploadFiles });
                empty.Add(new MenuItem { Text = "Upload a folder…", Icon = "folder", IsEnabled = live, Action = UploadFolder });
            }
            empty.Add(MenuItem.Separator);
            empty.Add(new MenuItem { Text = "Refresh", Icon = "refresh", Hint = OtherPanes().Count > 0 ? "Ctrl+R" : "F5", IsEnabled = live, Action = Refresh });
            empty.Add(new MenuItem { Text = "Show hidden files", Hint = "Ctrl+H", IsChecked = _list.ShowHidden, Action = ToggleHidden });
            if (!IsLocal)
                empty.Add(new MenuItem { Text = "Open terminal here", Icon = "terminal", IsEnabled = _path is not null, Action = OpenTerminalHere });
            empty.Add(new MenuItem { Text = "Copy folder path", Icon = "copy", IsEnabled = _path is not null, Action = () => CopyText(_path!) });
            return empty;
        }
        FileEntry first = selected[0];
        bool single = selected.Count == 1;
        var items = new List<MenuItem>();
        if (single && first.IsDirectory)
        {
            items.Add(new MenuItem { Text = "Open", Icon = "folder", Hint = "Enter", Action = () => Open(first) });
            if (!IsLocal)
                items.Add(new MenuItem { Text = "Open terminal here", Icon = "terminal", Action = () => OpenTerminalAt(first.Path) });
        }
        else if (single)
        {
            bool text = OpensInEditor(first);
            bool usable = live && !first.IsBrokenLink;
            items.Add(new MenuItem { Text = "Edit", Icon = "edit", Hint = text ? "Enter" : "F4", IsEnabled = usable, Action = () => OpenInEditor(first, EditorMode.Edit) });
            items.Add(new MenuItem { Text = "View", Icon = "eye", Hint = "F3", IsEnabled = usable, Action = () => OpenInEditor(first, EditorMode.View) });
            items.Add(new MenuItem { Text = "Open in a program", Icon = "file", Hint = text ? null : "Enter", IsEnabled = usable, Action = () => OpenFile(first, asText: false) });
            items.Add(new MenuItem { Text = "Edit in a text editor of this computer", IsEnabled = usable, Action = () => OpenFile(first, asText: true) });
        }
        items.AddRange(OtherPaneItems(selected, live));
        if (!IsLocal)
        {
            items.Add(new MenuItem { Text = $"Download to {ShortLocal(DownloadFolder())}", Icon = "download", IsEnabled = live, Action = () => Download(selected) });
            items.Add(new MenuItem { Text = "Download to…", Icon = "download", IsEnabled = live, Action = () => DownloadTo(selected) });
        }
        items.Add(MenuItem.Separator);
        if (single)
            items.Add(new MenuItem { Text = "Rename…", Icon = "edit", Hint = "F2", IsEnabled = live, Action = () => Rename(first) });
        items.Add(new MenuItem { Text = "Move to…", Icon = "folder", IsEnabled = live, Action = () => Move(selected) });
        if (_fs?.HasPermissions == true)
            items.Add(new MenuItem { Text = "Permissions…", Icon = "lock", IsEnabled = live, Action = () => ChangePermissions(selected) });
        items.Add(new MenuItem { Text = single ? "Copy path" : "Copy paths", Icon = "copy", Hint = "Ctrl+C", Action = () => CopyPaths(selected) });
        items.Add(MenuItem.Separator);
        items.Add(new MenuItem { Text = "Delete…", Icon = "trash", Hint = "Del", IsDanger = true, IsEnabled = live, Action = () => Delete(selected) });
        return items;
    }

    // ---- actions ----

    private async void NewFolder() => await CreateAsync(folder: true);

    private async void NewFile() => await CreateAsync(folder: false);

    private async Task CreateAsync(bool folder)
    {
        if (_fs is not { } fs || _path is not { } dir || !Connected)
            return;
        string? error = null, name = "";
        while (true)
        {
            name = await new PromptDialog(Host, folder ? "New folder" : "New file", FileFormat.Printable(dir), null, "Name",
                "Create", error: error, initialText: name ?? "").ShowAsync();
            if (name is null)
                return;
            name = name.Trim();
            if ((error = fs.ValidateName(name)) is not null)
                continue;
            string path = fs.Join(dir, name);
            try
            {
                if (folder)
                    await fs.CreateDirectoryAsync(path, CancellationToken.None);
                else
                    await fs.CreateFileAsync(path, CancellationToken.None);
                if (_path == dir)
                    Load(dir, select: path, keepSelection: false);
                return;
            }
            catch (FileOperationException ex)
            {
                error = ex.Message;
            }
            catch (SshSessionException)
            {
                return; // the Lost banner says it
            }
        }
    }

    private async void Rename(FileEntry entry)
    {
        if (_fs is not { } fs || !Connected)
            return;
        string dir = fs.Parent(entry.Path);
        string? error = null, name = entry.Name;
        while (true)
        {
            name = await new PromptDialog(Host, "Rename", FileFormat.Printable(entry.Path), null, "New name", "Rename",
                error: error, initialText: name ?? entry.Name).ShowAsync();
            if (name is null || name == entry.Name)
                return;
            name = name.Trim();
            if ((error = fs.ValidateName(name)) is not null)
                continue;
            string target = fs.Join(dir, name);
            try
            {
                await fs.RenameAsync(entry, target, CancellationToken.None);
                if (_path == dir)
                    Load(dir, select: target, keepSelection: false);
                FolderChanged?.Invoke(fs, dir);
                return;
            }
            catch (FileOperationException ex)
            {
                error = ex.Message;
            }
            catch (SshSessionException)
            {
                return;
            }
        }
    }

    // Moves entries into another folder of the same place (typed: absolute, ~ or relative to the folder shown).
    private async void Move(IReadOnlyList<FileEntry> entries)
    {
        if (_fs is not { } fs || _path is not { } dir || entries.Count == 0 || !Connected)
            return;
        string? error = null, typed = dir;
        while (true)
        {
            typed = await new PromptDialog(Host, entries.Count == 1 ? "Move" : $"Move {entries.Count} items", FileFormat.Describe(entries),
                $"The folder to move to, on {(IsLocal ? "this computer" : "the same host")}: a full path, ~/… or a path relative to this folder.",
                "Folder", "Move", error: error, initialText: typed ?? dir).ShowAsync();
            if (typed is null)
                return;
            string target;
            try
            {
                target = fs.Resolve(typed, dir);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                continue;
            }
            catch (SshSessionException)
            {
                return;
            }
            if (target == dir)
                return;
            if (entries.Any(e => e.Kind == FileEntryKind.Directory && fs.IsWithin(target, e.Path)))
            {
                error = "A folder can't be moved into itself.";
                continue;
            }
            try
            {
                if (await fs.StatAsync(target, CancellationToken.None) is not { IsDirectory: true })
                {
                    error = $"{target} is not a folder.";
                    continue;
                }
                foreach (FileEntry entry in entries)
                    await fs.RenameAsync(entry, fs.Join(target, entry.Name), CancellationToken.None);
                Host.ShowToast($"Moved {FileFormat.Describe(entries)} to {FileFormat.Printable(target)}.", ToastKind.Success);
                FolderChanged?.Invoke(fs, target);
            }
            catch (FileOperationException ex)
            {
                Host.ShowToast(ex.Message, ToastKind.Error);
            }
            catch (SshSessionException)
            {
                return;
            }
            if (_path == dir)
                Load(dir, select: null, keepSelection: true);
            return;
        }
    }

    private async void Delete(IReadOnlyList<FileEntry> entries)
    {
        if (_fs is not { } fs || entries.Count == 0 || !Connected)
            return;
        bool folders = entries.Any(e => e.Kind == FileEntryKind.Directory);
        string message = $"Delete {FileFormat.Describe(entries)} on {(IsLocal ? "this computer" : _address)}?"
            + (folders ? " Folders are deleted with everything in them." : "")
            + (entries.Any(e => e.Kind == FileEntryKind.Symlink) ? " Links are removed, not what they point to." : "")
            + (IsLocal ? " They don't go to the recycle bin." : "")
            + " This can't be undone.";
        if (!await ConfirmDialog.ShowAsync(Host, entries.Count == 1 ? "Delete item?" : $"Delete {entries.Count} items?", message, "Delete", danger: true))
            return;
        string? dir = _path;
        SetStatus(IsLocal ? TabStatus.None : TabStatus.Connected, $"{_address} · Deleting {FileFormat.Describe(entries)}…");
        try
        {
            foreach (FileEntry entry in entries)
                await fs.DeleteAsync(entry, null, CancellationToken.None);
        }
        catch (FileOperationException ex)
        {
            Host.ShowToast(ex.Message, ToastKind.Error);
        }
        catch (SshSessionException)
        {
            return;
        }
        if (dir is not null)
        {
            if (_path == dir)
                Load(dir, select: null, keepSelection: true);
            FolderChanged?.Invoke(fs, dir);
        }
    }

    private async void ChangePermissions(IReadOnlyList<FileEntry> entries)
    {
        if (_fs is not { HasPermissions: true } fs || entries.Count == 0 || !Connected)
            return;
        if (await PermissionsDialog.ShowAsync(Host, entries) is not { } mode)
            return;
        string? dir = _path;
        try
        {
            foreach (FileEntry entry in entries)
                await fs.SetPermissionsAsync(entry.Path, mode, CancellationToken.None);
        }
        catch (FileOperationException ex)
        {
            Host.ShowToast(ex.Message, ToastKind.Error);
        }
        catch (SshSessionException)
        {
            return;
        }
        if (dir is not null && _path == dir)
            Load(dir, select: null, keepSelection: true);
    }

    private void CopyPaths(IReadOnlyList<FileEntry> entries) =>
        CopyText(string.Join("\n", entries.Select(e => e.Path)));

    private void CopyText(string text)
    {
        Shell.SetClipboardText(text);
        Host.ShowToast(text.Contains('\n') ? "Paths copied." : $"Copied {FileFormat.Printable(text)}", ToastKind.Success);
    }

    private void ToggleHidden()
    {
        _list.ShowHidden = !_list.ShowHidden;
        bool show = _list.ShowHidden;
        Host.Services.UpdatePrefs(p => p.FilesShowHidden = show);
        UpdateStatus();
    }

    private void OpenTerminalHere()
    {
        if (_path is { } path)
            OpenTerminalAt(path);
    }

    private void OpenTerminalAt(string path)
    {
        if (_host is not null)
            Host.OpenTab(new SessionTabContent(CurrentHost, _password, $"cd -- {RemotePath.Quote(path)}\r"));
    }

    // ---- uploads and downloads ----

    private async void UploadFiles()
    {
        if (!Connected || _path is not { } dir)
            return;
        IReadOnlyList<string> paths;
        try
        {
            paths = await FilePicker.PickFilesAsync($"Upload to {PlaceName}:{dir}");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast($"{ex.Message} Drop the files on the window instead.", ToastKind.Error);
            return;
        }
        if (paths.Count > 0)
            CopyIn(paths, dir);
    }

    private async void UploadFolder()
    {
        if (!Connected || _path is not { } dir)
            return;
        string? folder;
        try
        {
            folder = await FilePicker.PickFolderAsync($"Upload a folder to {PlaceName}:{dir}");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast($"{ex.Message} Drop the folder on the window instead.", ToastKind.Error);
            return;
        }
        if (folder is not null)
            CopyIn([folder], dir);
    }

    /// <summary>Copies files and folders dropped on the window (from the system's file manager) into the folder shown.</summary>
    public void UploadDropped(IReadOnlyList<string> paths)
    {
        if (!Connected || _path is not { } dir)
        {
            Host.ShowToast("Connect first: the files go into the folder shown.", ToastKind.Error);
            return;
        }
        CopyIn(paths, dir);
    }

    // Local paths into a folder shown here: an upload to a host, a copy on this computer.
    private async void CopyIn(IReadOnlyList<string> paths, string dir)
    {
        if (_queue is not { } queue || _fs is not { } fs)
            return;
        var entries = new List<FileEntry>();
        foreach (string path in paths)
        {
            try
            {
                if (await LocalFileSystem.Instance.StatAsync(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), CancellationToken.None) is { } entry)
                    entries.Add(entry);
            }
            catch (Exception ex) when (ex is FileOperationException or ArgumentException or IOException)
            {
                Host.ShowToast(ex.Message, ToastKind.Error);
                return;
            }
        }
        if (entries.Count == 0 || _closing)
            return;
        queue.Copy(LocalFileSystem.Instance, entries, fs, dir, move: false, ResolveConflicts(dir));
    }

    private Func<IReadOnlyList<string>, Task<ConflictChoice>> ResolveConflicts(string destination) =>
        names => UiThread.InvokeAsync(() => ConflictDialog.ShowAsync(Host, names, destination));

    private void Download(IReadOnlyList<FileEntry> entries) => StartDownload(entries, DownloadFolder());

    private async void DownloadTo(IReadOnlyList<FileEntry> entries)
    {
        string? folder;
        try
        {
            folder = await FilePicker.PickFolderAsync("Download to", DownloadFolder());
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast($"{ex.Message} Use Download, which saves to {ShortLocal(DownloadFolder())}.", ToastKind.Error);
            return;
        }
        if (folder is not null)
            StartDownload(entries, folder);
    }

    private void StartDownload(IReadOnlyList<FileEntry> entries, string folder)
    {
        if (_queue is not { } queue || Session is not { } session || entries.Count == 0)
            return;
        queue.Download(entries, session.Files, folder, ResolveConflicts(folder));
    }

    private async void ChangeDownloadFolder()
    {
        string? folder;
        try
        {
            folder = await FilePicker.PickFolderAsync("Download folder", DownloadFolder());
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast(ex.Message, ToastKind.Error);
            return;
        }
        if (folder is not null)
        {
            Host.Services.UpdatePrefs(p => p.DownloadFolder = folder);
            Host.ShowToast($"Downloads now go to {ShortLocal(folder)}.", ToastKind.Success);
        }
    }

    /// <summary>The Downloads folder (or the chosen one); the home folder when there is none.</summary>
    private string DownloadFolder()
    {
        if (Host.Services.Prefs.DownloadFolder is { } chosen && Directory.Exists(chosen))
            return chosen;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    private static string ShortLocal(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !OperatingSystem.IsWindows() && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    // ---- opening and editing ----

    // A host's file opens from a private copy that is watched and uploaded back on every save; a local one directly.
    private async void OpenFile(FileEntry entry, bool asText)
    {
        if (IsLocal)
        {
            if (!ExternalLink.OpenFile(entry.Path, asText))
                Host.ShowToast($"No program could open {FileFormat.Printable(entry.Name)}.", ToastKind.Error);
            return;
        }
        if (_queue is not { } queue || Session is not { } session)
            return;
        if (_edits.LocalCopyOf(entry.Path) is { } open && File.Exists(open))
        {
            // Already opened from here: its copy may hold edits not uploaded yet, so it is opened again as it is.
            if (!ExternalLink.OpenFile(open, asText))
                Host.ShowToast($"No program could open {FileFormat.Printable(entry.Name)}.", ToastKind.Error);
            return;
        }
        if (entry.Size > LargeFileBytes && !await ConfirmDialog.ShowAsync(Host, "Open a large file?",
                $"{FileFormat.Printable(entry.Name)} is {FileFormat.Size(entry.Size)}; it is downloaded before it opens.", "Download and open"))
            return;
        string local;
        try
        {
            local = _edits.LocalPathFor(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Host.ShowToast($"Can't make a local copy: {ex.Message}", ToastKind.Error);
            return;
        }
        FileTransfer transfer = queue.DownloadFile(entry, session.Files, local);
        _reported.Add(transfer); // its failure is reported below
        _announced.Add(transfer);
        _transfers.Describe(transfer, asText ? "to edit it here" : "to open it here");
        await transfer.Completion;
        if (_closing)
            return;
        TransferProgress result = transfer.Snapshot();
        if (result.State != TransferState.Done)
        {
            if (result.State == TransferState.Failed)
                Host.ShowToast($"Couldn't open {FileFormat.Printable(entry.Name)}: {result.Error}", ToastKind.Error);
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(local, UnixFileMode.UserRead | UnixFileMode.UserWrite); // a copy to edit, never to run
            _edits.Watch(entry.Path, local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Host.ShowToast($"Can't watch the local copy: {ex.Message}", ToastKind.Error);
            return;
        }
        if (ExternalLink.OpenFile(local, asText))
            Host.ShowToast($"Opened {FileFormat.Printable(entry.Name)}. Each save is uploaded back while this tab is open.", ToastKind.Info);
        else
            Host.ShowToast($"No program could open {FileFormat.Printable(entry.Name)}. Download it instead.", ToastKind.Error);
    }

    private async void UploadSaved(string remote, string local)
    {
        if (_closing)
            return;
        string name = FileFormat.Printable(RemotePath.FileName(remote));
        if (_queue is not { } queue || Session is not { } session)
        {
            Host.ShowToast($"{name} was saved, but the connection is closed: reconnect and save it again.", ToastKind.Error);
            RequestAttention();
            return;
        }
        FileTransfer transfer = queue.UploadFile(local, session.Files, remote);
        _reported.Add(transfer); // its failure is reported below
        await transfer.Completion;
        if (_closing)
            return;
        TransferProgress result = transfer.Snapshot();
        if (result.State == TransferState.Done)
        {
            Host.ShowToast($"Saved {name} to {PlaceName}.", ToastKind.Success);
        }
        else if (result.State == TransferState.Failed)
        {
            Host.ShowToast($"Couldn't save {name} to {PlaceName}: {result.Error} Save it again to retry.", ToastKind.Error);
            RequestAttention();
        }
    }

    // ---- transfers ----

    private void OnTransfersChanged()
    {
        if (_closing)
            return;
        IReadOnlyList<FileTransfer> transfers = _queue?.Transfers ?? [];
        float before = _transfers.PreferredHeight;
        _transfers.SetTransfers(transfers);
        if (Math.Abs(before - _transfers.PreferredHeight) > 0.5f)
            InvalidateLayout();
        SetSizeText(_transfers.Summary());
        // A finished transfer refreshes the panes showing where it went (and, for a move, where it came from).
        foreach (FileTransfer done in transfers.Where(t => t.Snapshot().IsFinished && _announced.Add(t)))
        {
            if (done.Target is { } target && done.TargetPath is { } targetPath)
                FolderChanged?.Invoke(target, targetPath);
            if (done.IsMove && done.Source is { } source && done.SourcePath is { } sourcePath)
                FolderChanged?.Invoke(source, sourcePath);
        }
        foreach (FileTransfer failed in transfers.Where(t => t.Snapshot().State == TransferState.Failed && _reported.Add(t)))
            Host.ShowToast($"{Verb(failed)} of {FileFormat.Printable(failed.Title)} failed: {failed.Snapshot().Error}", ToastKind.Error);
    }

    private static string Verb(FileTransfer t) => t.IsMove ? "Move" : t.Kind switch
    {
        TransferKind.Upload => "Upload",
        TransferKind.Download => "Download",
        _ => "Copy",
    };

    // Another pane (or a transfer of this one) changed a folder: refresh when it is the one shown here.
    private void OnFolderChanged(IFileSystem fs, string path)
    {
        if (_closing || _path is null || _fs is not { } mine || !SamePlace(mine, fs))
            return;
        if (path == _path || mine.Parent(path) == _path)
            Load(_path, select: null, keepSelection: true);
    }

    // The same files: one object, or two connections to the same account on the same host.
    private static bool SamePlace(IFileSystem a, IFileSystem b) =>
        ReferenceEquals(a, b) || (a.IsLocal && b.IsLocal)
        || (a is SftpFileSystem x && b is SftpFileSystem y && x.Connection.Request is { } rx && y.Connection.Request is { } ry
            && rx.Host == ry.Host && rx.Port == ry.Port && rx.Username == ry.Username);
}
