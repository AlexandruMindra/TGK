using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core.Visual;
using Silk.NET.Input;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Client.Platform;
using TGK.Core.Files;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

/// <summary>
/// A file browser pane: a host's files over SFTP, or this computer's. Browse folders (back, forward, up, a path to
/// type, a name filter, hidden files on or off), create, rename, move and delete, change permissions, copy paths, edit
/// text files in TGK's editor beside the pane (see <c>FilesTabContent.Editor.cs</c>); for a host also upload and
/// download (picked or dropped on the window), open or edit files in a local program (each save is uploaded back) and
/// open a terminal in a folder. Beside other file panes (a split view) items are copied or
/// moved between them: dragged, or with F5 / F6 (see <c>FilesTabContent.Panes.cs</c>). Transfers run one after the
/// other in a panel at the bottom.
/// </summary>
/// <remarks>
/// A host's pane connects like a terminal tab (jump hosts, stored or typed passwords, host keys, one-time codes) when
/// first shown, or shares the connection of the pane it was opened from. A lost connection leaves the listing on screen
/// with a Reconnect banner. Closing the pane cancels its transfers and stops uploading saves of files opened from it.
/// </remarks>
public sealed partial class FilesTabContent : TabContent, IKeyInput
{
    private const float ToolbarH = 44, ButtonSize = 30;

    private readonly HostEntry? _host; // null: this computer
    private readonly string? _initialPath;
    private readonly SessionOverlay _overlay = new();
    private readonly FileList _list = new();
    private readonly TransferPanel _transfers = new();
    private readonly IconButton _back = new("chevron-left"), _forward = new("chevron-right"), _up = new("arrow-up"), _refresh = new("refresh");
    private readonly IconButton _upload = new("upload"), _download = new("download"), _newFolder = new("folder-plus"), _panes = new("layout-columns"), _more = new("more");
    private readonly TextField _pathField = new("Folder path") { Mono = true, FontSize = Theme.FontSm, IsTabStop = false };
    private readonly TextField _filterField = new("Filter") { LeadingIcon = "search", ShowClearButton = true, FontSize = Theme.FontSm };
    private readonly Stack<string> _history = new(), _future = new();
    private readonly Dictionary<string, string> _hopPasswords = [];
    private readonly EditedFiles _edits = new();
    private Toolbar _toolbar = null!;
    private FilesSession? _session;        // a host's connection (shared with its other panes)
    private SftpConnection? _connecting;   // the connection being opened
    private Action<string>? _onLost;
    private IFileSystem? _fs;              // what is shown: the session's files, or this computer's
    private TransferQueue? _queue;
    private CancellationTokenSource? _attempt, _listing;
    private int _generation;
    private string? _password;
    private string _address = "";
    private string? _path; // the folder shown (null until the first listing)
    private bool _started, _closing, _loading;

    /// <param name="host">A saved host (re-read from the vault on every connect) or an unsaved quick-connect entry.</param>
    /// <param name="path">The folder to open; null for the home folder.</param>
    /// <param name="password">A password to try first (e.g. the one typed for the terminal this was opened from).</param>
    /// <param name="session">An open connection to share (a second pane on the same host); null to connect.</param>
    public FilesTabContent(HostEntry host, string? path = null, string? password = null, FilesSession? session = null)
    {
        _host = host;
        _initialPath = path;
        _password = password ?? session?.Request.Password;
        _session = session?.AddRef();
        ReceivesKeyboard = true;
        SetTitle($"{host.DisplayName} · Files");
        _overlay.PrimaryClicked += () => Connect();
        _overlay.SecondaryClicked += OnOverlaySecondary;
    }

    private FilesTabContent(string? path)
    {
        _initialPath = path;
        ReceivesKeyboard = true;
        SetTitle("This computer · Files");
    }

    /// <summary>A pane for this computer's files, at <paramref name="path"/> (else the home folder).</summary>
    public static FilesTabContent Local(string? path = null) => new(path);

    /// <summary>This computer's files (no host).</summary>
    public bool IsLocal => _host is null;

    /// <summary>The folder shown, or null before the first listing.</summary>
    public string? CurrentPath => _path;

    /// <summary>The files shown, once connected (or always, for this computer).</summary>
    public IFileSystem? FileSystem => _fs;

    /// <summary>The open connection of a host's pane.</summary>
    public FilesSession? Session => _session is { IsConnected: true } session ? session : null;

    /// <summary>A short name for menus: the host's name, or "This computer".</summary>
    public string PlaceName => _host?.DisplayName ?? "This computer";

    public override VisualElement? DefaultFocus => _list;

    public override WorkspaceTab SaveState()
    {
        string? path = _path ?? _initialPath;
        if (_host is null)
            return new WorkspaceTab { Kind = WorkspaceTab.LocalFilesKind, Path = path };
        return IsSaved
            ? new WorkspaceTab { HostId = _host.Id, Kind = WorkspaceTab.FilesKind, Path = path }
            : new WorkspaceTab
            {
                Host = _host.Host, Port = _host.Port, Username = string.IsNullOrWhiteSpace(_host.Username) ? null : _host.Username,
                Kind = WorkspaceTab.FilesKind, Path = path,
            };
    }

    private bool IsSaved => _host is not null && Host.Services.Vault.Current.FindHost(_host.Id) is not null;

    private HostEntry CurrentHost => _host is null ? throw new InvalidOperationException("This computer has no host.")
        : Host.Services.Vault.Current.FindHost(_host.Id) ?? _host;

    private bool Connected => _fs is not null && (IsLocal || _session is { IsConnected: true });

    public override void OnAttached()
    {
        var toolbar = new Toolbar();
        foreach (IconButton button in new[] { _back, _forward, _up, _refresh, _upload, _download, _newFolder, _panes, _more })
            toolbar.AddChild(button);
        toolbar.AddChild(_pathField);
        toolbar.AddChild(_filterField);
        AddChild(toolbar);
        AddChild(_list);
        AddChild(_transfers);
        AddChild(_overlay);
        _toolbar = toolbar;

        _back.Clicked += GoBack;
        _forward.Clicked += GoForward;
        _up.Clicked += GoUp;
        _refresh.Clicked += Refresh;
        _upload.Clicked += () => ShowMenu(UploadMenu(), _upload);
        _download.Clicked += () => Download(_list.Selected);
        _newFolder.Clicked += NewFolder;
        _panes.Clicked += () => ShowMenu(PaneMenu(), _panes);
        _more.Clicked += () => ShowMenu(MoreMenu(), _more);
        _pathField.Submitted += () => Navigate(_pathField.Text);
        _pathField.Escaped += () =>
        {
            _pathField.Text = _path ?? "";
            Host.SetActiveKeyboardElement(_list);
        };
        _filterField.Changed += text => _list.Filter = text.Trim();
        _filterField.Submitted += () => Host.SetActiveKeyboardElement(_list);
        _filterField.Escaped += () =>
        {
            _filterField.Text = "";
            _list.Filter = "";
            Host.SetActiveKeyboardElement(_list);
        };
        _list.ShowHidden = Host.Services.Prefs.FilesShowHidden;
        _list.Activated += Open;
        _list.MenuRequested += (x, y) => Host.Menu.Show(ListMenu(), x, y, 220);
        _list.SelectionChanged += UpdateStatus;
        _list.DragMoved += OnDragMoved;
        _list.DragEnded += OnDragEnded;
        _transfers.CancelClicked += t => t.Cancel();
        _transfers.ShowClicked += t => ExternalLink.OpenFolder(t.TargetPath ?? t.Destination);
        _transfers.ClearClicked += () => _queue?.ClearFinished();
        _edits.Saved += (remote, local) => UiThread.Post(() => UploadSaved(remote, local));
        FolderChanged += OnFolderChanged;
        UiClock.Tick += OnTick;
        _address = _host is null ? "This computer" : HostFormat.Address(_host, Host.Services.Vault.Current);
        UpdateButtons();
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
    }

    public override void OnShown()
    {
        if (_started)
            return;
        _started = true;
        UiThread.Post(() => Connect());
    }

    public override void OnClosing()
    {
        _closing = true;
        UiClock.Tick -= OnTick;
        FolderChanged -= OnFolderChanged;
        EndDrag();
        CancelAttempt();
        _listing?.Cancel();
        CloseConnection();
        _edits.Dispose();
    }

    // Cancels the transfers, lets the running one remove its partial file, then lets go of the connection (in the
    // background; the last pane of a host closes it). The pane forgets both at once.
    private void CloseConnection()
    {
        TransferQueue? queue = _queue;
        FilesSession? session = _session;
        if (session is not null && _onLost is not null)
            session.Connection.Lost -= _onLost;
        _queue = null;
        _session = null;
        _onLost = null;
        _fs = null;
        if (queue is null)
        {
            session?.Release();
            return;
        }
        _ = Task.Run(async () =>
        {
            await queue.CloseAsync(TimeSpan.FromSeconds(5));
            session?.Release();
        });
    }

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        float transfersH = Math.Min(_transfers.PreferredHeight, h * 0.45f);
        _transfers.Visible = transfersH > 0;
        _toolbar.Transform.SetLocalFrame(0, 0, w, ToolbarH);
        float bannerH = _overlay.IsBanner ? SessionOverlay.BannerHeight : 0;
        float listH = Math.Max(0, h - ToolbarH - transfersH - bannerH);
        _list.Transform.SetLocalFrame(0, ToolbarH, w, listH);
        _transfers.Transform.SetLocalFrame(0, ToolbarH + listH, w, transfersH);
        if (_overlay.IsBanner)
            _overlay.Transform.SetLocalFrame(0, h - bannerH, w, bannerH);
        else
            _overlay.Transform.SetLocalFrame(0, 0, w, h);
        LayoutToolbar(w);
    }

    // Back, forward, up and refresh on the left, the path, the filter, then the action buttons. A narrow pane (a split
    // view of three) drops the filter, then the buttons whose actions are also in the menus, so the path keeps room.
    private void LayoutToolbar(float w)
    {
        const float Step = ButtonSize + 2, MinPath = 90;
        float y = (ToolbarH - ButtonSize) / 2f;
        IconButton[] left = [_back, _forward, _up, _refresh];
        IconButton[] actions = [_upload, _download, _newFolder, _panes, _more];
        var shown = new HashSet<IconButton>(left.Concat(actions).Where(b => b != _upload && b != _download || !IsLocal));
        // Dropped first to last when there is no room (their actions stay in the menus).
        IconButton[] optional = [_upload, _download, _newFolder, _forward, _panes, _refresh];
        foreach (IconButton button in optional)
        {
            if (8 + shown.Count * Step + 12 + MinPath <= w)
                break;
            shown.Remove(button);
        }
        float x = 8;
        foreach (IconButton button in left)
        {
            button.Visible = shown.Contains(button);
            if (!button.Visible)
                continue;
            button.Transform.SetLocalFrame(x, y, ButtonSize, ButtonSize);
            x += Step;
        }
        float right = w - 8;
        foreach (IconButton button in actions.Reverse())
        {
            button.Visible = shown.Contains(button);
            if (!button.Visible)
                continue;
            right -= ButtonSize;
            button.Transform.SetLocalFrame(right, y, ButtonSize, ButtonSize);
            right -= 2;
        }
        right -= 6;
        float filterW = Math.Clamp((right - x) * 0.3f, 0, 220);
        _filterField.Visible = filterW >= 110;
        if (_filterField.Visible)
        {
            _filterField.Transform.SetLocalFrame(right - filterW, y, filterW, ButtonSize);
            right -= filterW + 8;
        }
        _pathField.Transform.SetLocalFrame(x + 6, y, Math.Max(40, right - x - 6), ButtonSize);
    }

    // ---- connecting ----

    private async void Connect()
    {
        if (_closing)
            return;
        if (_host is null)
        {
            // This computer: nothing to connect.
            _fs = LocalFileSystem.Instance;
            _queue ??= NewQueue();
            OnReady();
            return;
        }
        if (_session is { IsConnected: true } shared && _fs is null)
        {
            // A pane opened beside another one of the same host: its connection is ready.
            AttachSession(shared);
            OnReady();
            return;
        }

        CancelAttempt();
        _listing?.Cancel();
        CloseConnection();
        int generation = ++_generation;
        _attempt = new CancellationTokenSource();
        CancellationToken ct = _attempt.Token;

        var vault = Host.Services.Vault;
        HostEntry host = CurrentHost;
        _address = HostFormat.Address(host, vault.Current);
        SetTitle($"{host.DisplayName} · Files");
        ShowConnecting("Opening connection…");

        SshConnectRequest request;
        SftpConnection connection;
        try
        {
            // A file browser needs neither the terminal's tunnels nor its startup command or environment.
            request = SshConnectRequest.ForHost(vault.Current, host) with { Tunnels = [], StartupCommand = null, Environment = [] };
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                string? user = await AskAsync(generation, guard => new PromptDialog(Host, "Username required", _address, null,
                    "Username", "Continue", guardEnter: guard).ShowAsync(ct));
                if (generation != _generation || _closing)
                    return;
                if (string.IsNullOrWhiteSpace(user))
                {
                    ShowNotConnected("No username was entered.");
                    return;
                }
                request = request with { Username = user.Trim() };
                _address = $"{request.Username}@{_address}";
            }
            for (int i = 0; i < request.JumpChain.Count; i++)
            {
                SshConnectRequest? hop = await CompleteHopAsync(generation, request.JumpChain[i], null, ct);
                if (generation != _generation || _closing)
                    return;
                if (hop is null)
                {
                    ShowNotConnected($"No password was entered for jump host {HopName(request.JumpChain[i])}.");
                    return;
                }
                request = request with { JumpChain = request.JumpChain.Select((h, n) => n == i ? hop : h).ToList() };
            }

            bool usesKey = !string.IsNullOrWhiteSpace(request.PrivateKey);
            string? password = usesKey ? null : _password ?? request.Password;
            string? error = null;
            while (true)
            {
                if (!usesKey)
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        password = await AskAsync(generation, guard => PasswordPromptDialog.ShowAsync(Host, _address, error, guard, ct));
                        if (generation != _generation || _closing)
                            return;
                        if (password is null)
                        {
                            ShowNotConnected("No password was entered.");
                            return;
                        }
                        _password = password;
                    }
                    request = request with { Password = password };
                }

                connection = CreateConnection(generation);
                try
                {
                    await connection.ConnectAsync(request, ct);
                    break;
                }
                catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && !ct.IsCancellationRequested
                    && ex.JumpHostIndex is int hopIndex && hopIndex < request.JumpChain.Count
                    && string.IsNullOrWhiteSpace(request.JumpChain[hopIndex].PrivateKey))
                {
                    connection.Dispose();
                    SshConnectRequest? hop = await CompleteHopAsync(generation, request.JumpChain[hopIndex], ex.Message, ct);
                    if (generation != _generation || _closing)
                        return;
                    if (hop is null)
                    {
                        ShowNotConnected($"No password was entered for jump host {HopName(request.JumpChain[hopIndex])}.");
                        return;
                    }
                    request = request with { JumpChain = request.JumpChain.Select((h, n) => n == hopIndex ? hop : h).ToList() };
                }
                catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && ex.JumpHostIndex is null && !usesKey && !ct.IsCancellationRequested)
                {
                    connection.Dispose();
                    password = _password = null;
                    error = ex.Message;
                    ShowConnecting("Authentication failed");
                }
            }
        }
        catch (SshSessionException ex)
        {
            if (generation == _generation && !_closing)
                ShowFailed(ex.Message);
            return;
        }
        catch (SftpUnavailableException ex)
        {
            if (generation == _generation && !_closing)
                ShowFailed($"{ex.Message} Its files can't be browsed here; the terminal still works.");
            return;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation && !_closing)
                ShowNotConnected("The connection attempt was cancelled.");
            return;
        }
        _connecting = null;
        if (generation != _generation || _closing)
        {
            connection.Dispose();
            return;
        }
        _attempt = null;
        AttachSession(new FilesSession(host, connection, request));
        if (IsSaved)
            Host.RunVault(() => Host.Services.Vault.TouchHostAsync(host.Id));
        OnReady();
    }

    private SftpConnection CreateConnection(int generation)
    {
        var verifier = new KnownHostsVerifier(Host.Services.Vault, (info, ct) =>
            UiThread.InvokeAsync(() => AskAsync(generation, _ => HostKeyDialog.ShowAsync(Host, info, ct))));
        var connection = new SftpConnection(verifier, SignInPrompts(generation));
        _connecting = connection;
        return connection;
    }

    private InteractivePromptHandler SignInPrompts(int generation) => (prompt, ct) =>
        UiThread.InvokeAsync(() => AskAsync(generation, guard => SignInPromptDialog.ShowAsync(Host,
            $"{prompt.Username}@{(prompt.Port == 22 ? prompt.Host : $"{prompt.Host}:{prompt.Port}")}", prompt, guard, ct)));

    private void AttachSession(FilesSession session)
    {
        _session = session;
        _fs = session.Files;
        _onLost = reason => UiThread.Post(() => OnLost(session, reason));
        session.Connection.Lost += _onLost;
        _queue = NewQueue();
        if (!session.IsConnected)
            UiThread.Post(() => OnLost(session, "The connection was lost."));
    }

    private TransferQueue NewQueue()
    {
        var queue = new TransferQueue();
        queue.Changed += () => UiThread.Post(OnTransfersChanged);
        return queue;
    }

    // Prompts belong to this pane: it comes to the front first, and when it was in the background the prompt's Enter
    // waits until keys meant for the other tab have stopped.
    private Task<T> AskAsync<T>(int generation, Func<bool, Task<T>> show)
    {
        if (generation != _generation)
            throw new OperationCanceledException("The connection attempt was replaced.");
        bool background = Host.ActiveTab != this;
        if (background)
            Host.ActivateTab(this);
        return show(background);
    }

    private async Task<SshConnectRequest?> CompleteHopAsync(int generation, SshConnectRequest hop, string? error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hop.Username))
        {
            string? user = await AskAsync(generation, guard => new PromptDialog(Host, "Username required",
                $"Jump host {HopName(hop)}", null, "Username", "Continue", guardEnter: guard).ShowAsync(ct));
            if (string.IsNullOrWhiteSpace(user))
                return null;
            hop = hop with { Username = user.Trim() };
        }
        if (!string.IsNullOrWhiteSpace(hop.PrivateKey))
            return hop;
        string key = $"{hop.Username}@{hop.Host}:{hop.Port}";
        if (error is null && !string.IsNullOrEmpty(hop.Password))
            return hop;
        if (error is null && _hopPasswords.TryGetValue(key, out string? remembered))
            return hop with { Password = remembered };
        _hopPasswords.Remove(key);
        string target = $"{hop.Username}@{(hop.Port == 22 ? hop.Host : $"{hop.Host}:{hop.Port}")} (jump host)";
        string? password = await AskAsync(generation, guard => PasswordPromptDialog.ShowAsync(Host, target, error, guard, ct));
        if (password is null)
            return null;
        _hopPasswords[key] = password;
        return hop with { Password = password };
    }

    private static string HopName(SshConnectRequest hop) => string.IsNullOrWhiteSpace(hop.Name) ? hop.Host : hop.Name.Trim();

    // Connected (or local): show the folder.
    private void OnReady()
    {
        OnTransfersChanged(); // the transfers of a previous connection are gone
        _overlay.Hide();
        InvalidateLayout();
        string start = _path ?? _initialPath ?? _fs!.Home;
        _history.Clear();
        _future.Clear();
        Load(start, select: null, keepSelection: _path is not null, fallbackHome: true);
        if (Host.ActiveTab == this && !Host.HasModal)
            Host.SetActiveKeyboardElement(_list);
    }

    private void OnLost(FilesSession session, string reason)
    {
        if (session != _session || _closing)
            return;
        _listing?.Cancel();
        _loading = false;
        SetStatus(TabStatus.Failed, $"{_address} · Connection lost");
        _overlay.ShowEnded($"{reason} Press Enter or click Reconnect.", true);
        UpdateButtons();
        InvalidateLayout();
        RequestAttention();
    }

    private void ShowConnecting(string detail)
    {
        SetStatus(TabStatus.Connecting, $"{_address} · Connecting…");
        _overlay.ShowConnecting($"Opening files on {_address}", detail);
        UpdateButtons();
        InvalidateLayout();
    }

    private void ShowFailed(string message)
    {
        SetStatus(TabStatus.Failed, $"{_address} · Failed");
        _overlay.ShowFailed("Couldn't open the files", message, IsSaved ? "Edit host…" : null);
        UpdateButtons();
        InvalidateLayout();
    }

    private void ShowNotConnected(string message)
    {
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
        _overlay.ShowFailed("Not connected", message, IsSaved ? "Edit host…" : null, error: false);
        UpdateButtons();
        InvalidateLayout();
    }

    private void OnOverlaySecondary()
    {
        if (_overlay.Mode == SessionOverlay.OverlayMode.Connecting)
            CancelAttempt();
        else if (_host is not null && Host.Services.Vault.Current.FindHost(_host.Id) is { } saved)
            Host.EditHost(saved);
    }

    private void CancelAttempt()
    {
        _attempt?.Cancel();
        _attempt = null;
        _connecting?.Dispose();
        _connecting = null;
    }

    private void Disconnect()
    {
        _listing?.Cancel();
        CloseConnection();
        SetStatus(TabStatus.Closed, $"{_address} · Disconnected");
        _overlay.ShowEnded("Disconnected — press Enter or click Reconnect.", false);
        OnTransfersChanged();
        UpdateButtons();
        InvalidateLayout();
    }

    // ---- browsing ----

    /// <summary>Opens a folder typed or picked: absolute, <c>~</c>-relative or relative to the current one.</summary>
    public void Navigate(string path)
    {
        if (!Connected || _fs is not { } fs || string.IsNullOrWhiteSpace(path))
            return;
        string target;
        try
        {
            target = fs.Resolve(path, _path ?? fs.Home);
        }
        catch (ArgumentException ex)
        {
            Host.ShowToast(ex.Message, ToastKind.Error);
            return;
        }
        catch (SshSessionException)
        {
            return; // lost meanwhile: the banner says it
        }
        if (target == _path)
        {
            Refresh();
            return;
        }
        if (_path is not null)
            _history.Push(_path);
        _future.Clear();
        Load(target, select: null, keepSelection: false);
        Host.SetActiveKeyboardElement(_list);
    }

    private void Open(FileEntry entry)
    {
        if (!Connected)
            return;
        if (entry.IsDirectory)
            Navigate(entry.Path);
        else if (entry.IsBrokenLink)
            Host.ShowToast($"{FileFormat.Printable(entry.Name)} points to {FileFormat.Printable(entry.LinkTarget ?? "?")}, which does not exist.", ToastKind.Error);
        else if (OpensInEditor(entry))
            OpenInEditor(entry, EditorMode.Auto);
        else
            OpenFile(entry, asText: false);
    }

    private void GoUp()
    {
        if (_path is null || _fs is not { } fs || fs.Parent(_path) == _path)
            return;
        string child = _path;
        _history.Push(_path);
        _future.Clear();
        Load(fs.Parent(_path), select: child, keepSelection: false);
    }

    private void GoBack()
    {
        if (_history.Count == 0 || _path is null || _fs is not { } fs)
            return;
        string current = _path;
        string previous = _history.Pop();
        _future.Push(current);
        // Coming back up selects the folder one came from.
        string? select = null;
        for (string p = current; fs.Parent(p) != p; p = fs.Parent(p))
        {
            if (fs.Parent(p) == previous)
            {
                select = p;
                break;
            }
        }
        Load(previous, select, keepSelection: false);
    }

    private void GoForward()
    {
        if (_future.Count == 0 || _path is null)
            return;
        _history.Push(_path);
        Load(_future.Pop(), select: null, keepSelection: false);
    }

    /// <summary>Lists the current folder again, keeping the selection.</summary>
    public void Refresh()
    {
        if (_path is not null)
            Load(_path, select: null, keepSelection: true);
    }

    private async void Load(string path, string? select, bool keepSelection, bool fallbackHome = false)
    {
        if (_fs is not { } fs)
            return;
        _listing?.Cancel();
        var cts = _listing = new CancellationTokenSource();
        _loading = true;
        bool sameFolder = path == _path;
        if (!sameFolder)
            _list.Message = "Loading…";
        _pathField.Text = path;
        UpdateStatus();
        UpdateButtons();
        try
        {
            IReadOnlyList<FileEntry> entries = await fs.ListAsync(path, cts.Token);
            if (cts.IsCancellationRequested || _closing)
                return;
            _path = path;
            _list.SetEntries(entries, select, keepSelection && sameFolder);
            Host.WorkspaceChanged();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is FileOperationException or SshSessionException)
        {
            if (cts.IsCancellationRequested || _closing)
                return;
            if (fallbackHome && path != fs.Home && ex is FileOperationException)
            {
                // A saved folder that is gone (or no longer readable): start at home instead.
                Load(fs.Home, null, false);
                return;
            }
            if (ex is SshSessionException)
                return; // the Lost banner says it
            if (_path is null)
            {
                // A failed first listing shows why in the list.
                _path = path;
                _list.SetEntries([]);
                _list.Message = ex.Message;
            }
            else
            {
                // Stay where we were.
                if (!sameFolder && _history.Count > 0 && _history.Peek() == _path)
                    _history.Pop();
                _list.Message = null;
                Host.ShowToast(ex.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            // Never out of this async void: an unexpected failure is logged and shown.
            Log.Error($"Listing {path} failed: {ex}");
            if (!cts.IsCancellationRequested && !_closing)
                Host.ShowToast($"Couldn't list {FileFormat.Printable(path)}: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            if (_listing == cts)
            {
                _loading = false;
                _pathField.Text = _path ?? path;
                UpdateStatus();
                UpdateButtons();
            }
        }
    }

    private void UpdateStatus()
    {
        if (_fs is null || _overlay.Mode is SessionOverlay.OverlayMode.Ended)
            return;
        string where = _path ?? "";
        string text = _loading ? "Loading…" : FileFormat.Summary(_list.Rows);
        IReadOnlyList<FileEntry> selected = _list.Selected;
        if (selected.Count == 1)
        {
            FileEntry e = selected[0];
            text = $"{FileFormat.Printable(e.Name)}{(e.IsDirectory ? "" : " · " + FileFormat.Size(e.Size))} · {e.Permissions} · {FileFormat.Date(e.Modified, DateTimeOffset.Now)}";
            if (e.Kind == FileEntryKind.Symlink)
                text += $" · → {FileFormat.Printable(e.LinkTarget ?? "?")}";
        }
        else if (selected.Count > 1)
        {
            long bytes = selected.Where(e => !e.IsDirectory).Sum(e => e.Size);
            text = $"{FileFormat.Count(selected.Count, "item")} selected · {FileFormat.Size(bytes)}";
        }
        SetStatus(IsLocal ? TabStatus.None : TabStatus.Connected, $"{_address} · {FileFormat.Printable(where)} · {text}");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool live = Connected && _path is not null;
        _back.Enabled = live && _history.Count > 0;
        _forward.Enabled = live && _future.Count > 0;
        _up.Enabled = live && _fs is { } fs && fs.Parent(_path!) != _path;
        _refresh.Enabled = live;
        _upload.Enabled = live;
        _newFolder.Enabled = live;
        _download.Enabled = live && _list.Selected.Count > 0;
        _panes.Enabled = live;
        _pathField.Enabled = live;
        _filterField.Enabled = _path is not null;
    }

    // ---- keyboard ----

    public bool OnKey(KeyStroke k)
    {
        if (k.IsEnter && k.Modifiers == KeyModifiers.None && !k.IsRepeat && _overlay.Mode is SessionOverlay.OverlayMode.Failed or SessionOverlay.OverlayMode.Ended)
        {
            Connect();
            return true;
        }
        if (k.Is(Key.Escape) && _overlay.Mode == SessionOverlay.OverlayMode.Connecting)
        {
            CancelAttempt();
            ShowNotConnected("The connection attempt was cancelled.");
            return true;
        }
        if (k.Is(Key.Escape) && _drag is not null)
        {
            EndDrag();
            return true;
        }
        if (!Connected || k.IsRepeat && k.Key is not Key.Backspace)
            return false;
        switch (k.Key)
        {
            case Key.Backspace when k.Modifiers == KeyModifiers.None:
                GoUp();
                return true;
            case Key.F5 when k.Modifiers == KeyModifiers.None && _list.Selected.Count > 0 && OtherPanes().Count > 0:
                CopyToOtherPane(move: false);
                return true;
            case Key.F6 when k.Modifiers == KeyModifiers.None && _list.Selected.Count > 0 && OtherPanes().Count > 0:
                CopyToOtherPane(move: true);
                return true;
            case Key.F5 when k.Modifiers == KeyModifiers.None:
            case Key.R when k.Modifiers == KeyModifiers.Ctrl:
                Refresh();
                return true;
            case Key.F3 when k.Modifiers == KeyModifiers.None && _list.Selected is [{ IsDirectory: false } viewed]:
                OpenInEditor(viewed, EditorMode.View);
                return true;
            case Key.F4 when k.Modifiers == KeyModifiers.None && _list.Selected is [{ IsDirectory: false } edited]:
                OpenInEditor(edited, EditorMode.Edit);
                return true;
            case Key.F2 when k.Modifiers == KeyModifiers.None && _list.Selected is [{ } one]:
                Rename(one);
                return true;
            case Key.Delete when k.Modifiers == KeyModifiers.None && _list.Selected.Count > 0:
                Delete(_list.Selected);
                return true;
            case Key.H when k.Modifiers == KeyModifiers.Ctrl:
                ToggleHidden();
                return true;
            case Key.F when k.Modifiers == KeyModifiers.Ctrl && _filterField.Visible:
                Host.SetActiveKeyboardElement(_filterField);
                _filterField.SelectAll();
                return true;
            case Key.C when k.Modifiers == KeyModifiers.Ctrl && _list.Selected.Count > 0:
                CopyPaths(_list.Selected);
                return true;
            case Key.N when k.Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Shift):
                NewFolder();
                return true;
            default:
                return false;
        }
    }

    public void OnText(string text) { }

    private void OnTick()
    {
        if (_transfers.IsBusy)
        {
            _transfers.Refresh();
            SetSizeText(_transfers.Summary());
        }
    }

    /// <summary>The toolbar's background and bottom border.</summary>
    private sealed class Toolbar : Control
    {
        public Toolbar() => Style = new ElementStyle { BackColor = Theme.Surface };

        protected override void Paint(SkiaSharp.SKCanvas c) => Gfx.Line(c, 0, H - 0.5f, W, H - 0.5f, Theme.Border);
    }
}
