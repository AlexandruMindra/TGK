using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Core.Files;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

// Two (or more) file panes side by side in a split view: opening one beside this one, and copying or moving items
// between them (dragged, F5 / F6, or from the context menu), through this computer or directly between two hosts.
public sealed partial class FilesTabContent
{
    private DragState? _drag;

    /// <summary>A drag of this pane's selection: what is dragged and the pane (and folder) it would drop on.</summary>
    private sealed class DragState(IReadOnlyList<FileEntry> entries)
    {
        public IReadOnlyList<FileEntry> Entries { get; } = entries;
        public FilesTabContent? Target { get; set; }
        public string? Folder { get; set; }
    }

    // ---- a second pane ----

    private List<MenuItem> PaneMenu()
    {
        var items = new List<MenuItem> { new() { Text = "Open a pane beside this one", IsHeader = true } };
        if (_host is not null)
        {
            items.Add(new MenuItem
            {
                Text = $"{_host.DisplayName} — another folder", Icon = "server", IsEnabled = Session is not null,
                Action = () => OpenBeside(new FilesTabContent(_host, _path, _password, Session)),
            });
        }
        items.Add(new MenuItem
        {
            Text = IsLocal ? "This computer — another folder" : "This computer", Icon = "monitor",
            Action = () => OpenBeside(Local(IsLocal ? _path : null)),
        });
        List<HostEntry> hosts = Host.Services.Vault.Current.Hosts
            .Where(h => _host is null || h.Id != _host.Id)
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hosts.Count > 0)
        {
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem { Text = "Another host", IsHeader = true });
            foreach (HostEntry host in hosts)
                items.Add(new MenuItem { Text = host.DisplayName, Icon = "folder", Hint = host.Host, Action = () => OpenBeside(new FilesTabContent(host)) });
        }
        return items;
    }

    private void OpenBeside(FilesTabContent pane) => Host.OpenBeside(this, pane);

    /// <summary>The other file panes of this pane's split view that are ready (connected, a folder shown).</summary>
    private List<FilesTabContent> OtherPanes() =>
        Split?.Items.OfType<FilesTabContent>().Where(p => p != this && p.Connected && p._path is not null && !p._closing).ToList() ?? [];

    // "Copy to" / "Move to" entries for each other pane (in the context menu).
    private IEnumerable<MenuItem> OtherPaneItems(IReadOnlyList<FileEntry> selected, bool live)
    {
        List<FilesTabContent> panes = OtherPanes();
        if (panes.Count == 0)
            yield break;
        yield return MenuItem.Separator;
        foreach (FilesTabContent pane in panes)
        {
            string where = $"{pane.PlaceName}: {FileFormat.Printable(pane._path!)}";
            bool first = pane == panes[0];
            yield return new MenuItem { Text = $"Copy to {where}", Icon = "copy", Hint = first ? "F5" : null, IsEnabled = live, Action = () => TransferTo(pane, selected, null, move: false) };
            yield return new MenuItem { Text = $"Move to {where}", Icon = "tab-out", Hint = first ? "F6" : null, IsEnabled = live, Action = () => TransferTo(pane, selected, null, move: true) };
        }
    }

    // F5 / F6: to the other pane (the next one when there are several).
    private void CopyToOtherPane(bool move)
    {
        List<FilesTabContent> panes = OtherPanes();
        IReadOnlyList<FileEntry> selected = _list.Selected;
        if (panes.Count == 0 || selected.Count == 0)
            return;
        TransferTo(panes[0], selected, null, move);
    }

    /// <summary>
    /// Copies or moves <paramref name="entries"/> of this pane into <paramref name="folder"/> (else the folder shown)
    /// of <paramref name="target"/>, after a confirmation that, between two hosts, also chooses the route.
    /// </summary>
    private async void TransferTo(FilesTabContent target, IReadOnlyList<FileEntry> entries, string? folder, bool move)
    {
        if (_fs is not { } source || target._fs is not { } destination || _queue is not { } queue || entries.Count == 0)
            return;
        string targetDir = folder ?? target._path!;
        if (ReferenceEquals(source, destination) && entries.All(e => source.Parent(e.Path) == targetDir))
        {
            Host.ShowToast("The items are already in that folder.", ToastKind.Info);
            return;
        }
        if (ReferenceEquals(source, destination) && entries.Any(e => e.IsDirectory && destination.IsWithin(targetDir, e.Path)))
        {
            Host.ShowToast("A folder can't be copied or moved into itself.", ToastKind.Error);
            return;
        }
        bool betweenHosts = Session is { } from && target.Session is { } to && !ReferenceEquals(from, to);
        string? jumpNote = null;
        string defaultHost = "";
        int defaultPort = 22;
        if (betweenHosts)
        {
            SshConnectRequest targetRequest = target.Session!.Request;
            defaultHost = targetRequest.Host;
            defaultPort = targetRequest.Port;
            if (targetRequest.JumpChain.Count > 0)
                jumpNote = $"This computer reaches {target.PlaceName} through jump hosts; {PlaceName} must reach it directly at the address below.";
        }
        TransferChoice? choice = await TransferDialog.ShowAsync(Host, move, entries,
            $"{PlaceName}: {FileFormat.Printable(source.Parent(entries[0].Path))}", $"{target.PlaceName}: {FileFormat.Printable(targetDir)}",
            PlaceName, target.PlaceName, betweenHosts, defaultHost, defaultPort, jumpNote);
        if (choice is null || _closing || target._closing || _queue != queue)
            return;
        if (choice.Route == TransferRoute.Direct)
            StartDirect(target, entries, targetDir, move, choice.DirectHost, choice.DirectPort);
        else
            queue.Copy(source, entries, destination, targetDir, move, ResolveConflicts($"{target.PlaceName}: {targetDir}"));
    }

    // Host A (this pane's) copies straight to host B (the target pane's), with B's credentials; see DirectCopy.
    private void StartDirect(FilesTabContent target, IReadOnlyList<FileEntry> entries, string targetDir, bool move, string host, int port)
    {
        if (Session is not { } from || target.Session is not { } to || _queue is not { } queue)
            return;
        SshConnectRequest b = to.Request;
        var directTarget = new DirectTarget(host, port, b.Username, b.Password, b.PrivateKey, b.Passphrase, to.Connection.HostKey ?? []);
        if (directTarget.Validate() is { } problem)
        {
            Host.ShowToast(problem, ToastKind.Error);
            return;
        }
        if (entries.Any(e => from.Files.Parent(e.Path) != from.Files.Parent(entries[0].Path)))
        {
            Host.ShowToast("A direct copy takes items of one folder.", ToastKind.Error);
            return;
        }
        int generation = _generation;
        // A command connection to host A (the SFTP one can't run commands); its prompts come to this pane.
        Func<CancellationToken, Task<RemoteConnection>> connectSource = async ct =>
        {
            var verifier = new KnownHostsVerifier(Host.Services.Vault, (info, token) =>
                UiThread.InvokeAsync(() => AskAsync(generation, _ => Dialogs.HostKeyDialog.ShowAsync(Host, info, token))));
            var exec = new RemoteConnection(verifier, SignInPrompts(generation));
            try
            {
                await exec.ConnectAsync(from.Request, ct);
                return exec;
            }
            catch
            {
                exec.Dispose();
                throw;
            }
        };
        DirectCopy.Enqueue(queue, from.Files, entries, to.Files, targetDir, move, directTarget, connectSource,
            ResolveConflicts($"{target.PlaceName}: {targetDir}"), $"{target.PlaceName}:{targetDir} (direct)");
    }

    // ---- dragging between panes ----

    private void OnDragMoved(float x, float y)
    {
        if (_list.Selected is not { Count: > 0 } selected)
            return;
        _drag ??= new DragState(selected);
        FilesTabContent? target = Host.FilesPaneAt(x, y);
        FileEntry? folder = target?._list.FolderAt(x, y);
        // Onto this pane's own selection (or its folder shown) nothing happens.
        if (target == this && (folder is null || _drag.Entries.Any(e => e.Path == folder.Path)))
            target = null;
        if (target is not null && !target.Connected)
            target = null;
        if (_drag.Target is { } old && old != target)
            old._list.DropTarget = null;
        _drag.Target = target;
        _drag.Folder = target is null ? null : folder?.Path;
        if (target is not null)
            target._list.DropTarget = folder?.Path ?? "";
        SetStatus(IsLocal ? TabStatus.None : TabStatus.Connected, target is null
            ? $"{_address} · Drag onto another pane (or a folder) to copy there; Shift moves, Esc cancels."
            : $"{_address} · {(WillMove(target) ? "Move" : "Copy")} {FileFormat.Describe(_drag.Entries)} to {target.PlaceName}: {FileFormat.Printable(_drag.Folder ?? target._path ?? "")}");
    }

    private void OnDragEnded(float x, float y)
    {
        DragState? drag = _drag;
        OnDragMoved(x, y);
        EndDrag();
        if (drag?.Target is { } target)
            TransferTo(target, drag.Entries, drag.Folder, WillMove(target));
    }

    // Like file managers: within the same files a drag moves, elsewhere it copies; Shift moves, Ctrl copies.
    private bool WillMove(FilesTabContent target)
    {
        KeyModifiers mods = KeyboardHub.CurrentModifiers;
        if ((mods & KeyModifiers.Shift) != 0)
            return true;
        if ((mods & KeyModifiers.Ctrl) != 0)
            return false;
        return target._fs is { } fs && _fs is { } mine && ReferenceEquals(fs, mine);
    }

    private void EndDrag()
    {
        if (_drag?.Target is { } target)
            target._list.DropTarget = null;
        _drag = null;
        _list.CancelDrag();
        UpdateStatus();
    }

    /// <summary>Whether window point (x, y) is over this pane's list (a drop target).</summary>
    internal bool ListContains(float x, float y) => _list.Contains(x, y);
}
