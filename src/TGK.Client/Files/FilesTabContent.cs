using System;
using System.Collections.Generic;
using System.IO;
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
using TGK.Core.Agents;
using TGK.Core.Models;
using TGK.Core.Sftp;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

/// <summary>
/// A file browser tab for one host over SFTP: browse folders (back, forward, up, a path to type, a name filter, hidden
/// files on or off), upload files and folders (picked or dropped on the window), download them, open or edit files
/// in a local program (each save is uploaded back), create, rename and delete, change permissions, copy paths, and
/// open a terminal in the current folder. Transfers run one after the other in a panel at the bottom.
/// </summary>
/// <remarks>
/// Connects like a terminal tab (jump hosts, stored or typed passwords, host keys, one-time codes), when first shown.
/// A lost connection leaves the listing on screen with a Reconnect banner; there is no automatic reconnect. Closing
/// the tab cancels its transfers and stops uploading saves of files opened from it.
/// </remarks>
public sealed class FilesTabContent : TabContent, IKeyInput
{
    private const float ToolbarH = 44, ButtonSize = 30;
    private const long LargeFileBytes = 100L * 1024 * 1024;

    private readonly HostEntry _host;
    private readonly string? _initialPath;
    private readonly SessionOverlay _overlay = new();
    private readonly FileList _list = new();
    private readonly TransferPanel _transfers = new();
    private readonly IconButton _back = new("chevron-left"), _forward = new("chevron-right"), _up = new("arrow-up"), _refresh = new("refresh");
    private readonly IconButton _upload = new("upload"), _download = new("download"), _newFolder = new("folder-plus"), _more = new("more");
    private readonly TextField _pathField = new("Folder path") { Mono = true, FontSize = Theme.FontSm, IsTabStop = false };
    private readonly TextField _filterField = new("Filter") { LeadingIcon = "search", ShowClearButton = true, FontSize = Theme.FontSm };
    private readonly Stack<string> _history = new(), _future = new();
    private readonly Dictionary<string, string> _hopPasswords = [];
    private readonly EditedFiles _edits = new();
    private SftpConnection? _connection;
    private SftpFileSystem? _files;
    private SftpTransferQueue? _queue;
    private CancellationTokenSource? _attempt, _listing;
    private int _generation;
    private string? _password;
    private string _address = "";
    private string? _path; // the folder shown (null until the first listing)
    private bool _started, _closing, _loading;

    /// <param name="host">A saved host (re-read from the vault on every connect) or an unsaved quick-connect entry.</param>
    /// <param name="path">The folder to open; null for the home folder.</param>
    /// <param name="password">A password to try first (e.g. the one typed for the terminal this was opened from).</param>
    public FilesTabContent(HostEntry host, string? path = null, string? password = null)
    {
        _host = host;
        _initialPath = path;
        _password = password;
        ReceivesKeyboard = true;
        SetTitle($"{host.DisplayName} · Files");
        _overlay.PrimaryClicked += () => Connect();
        _overlay.SecondaryClicked += OnOverlaySecondary;
    }

    /// <summary>The folder shown, or null before the first listing.</summary>
    public string? CurrentPath => _path;

    public override VisualElement? DefaultFocus => _list;

    public override WorkspaceTab SaveState() => IsSaved
        ? new WorkspaceTab { HostId = _host.Id, Kind = WorkspaceTab.FilesKind, Path = _path ?? _initialPath }
        : new WorkspaceTab
        {
            Host = _host.Host, Port = _host.Port, Username = string.IsNullOrWhiteSpace(_host.Username) ? null : _host.Username,
            Kind = WorkspaceTab.FilesKind, Path = _path ?? _initialPath,
        };

    private bool IsSaved => Host.Services.Vault.Current.FindHost(_host.Id) is not null;

    private HostEntry CurrentHost => Host.Services.Vault.Current.FindHost(_host.Id) ?? _host;

    private bool Connected => _connection is { IsConnected: true } && _files is not null;

    public override void OnAttached()
    {
        var toolbar = new Toolbar();
        foreach (IconButton button in new[] { _back, _forward, _up, _refresh, _upload, _download, _newFolder, _more })
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
        _list.MenuRequested += (x, y) => Host.Menu.Show(ListMenu(), x, y, 200);
        _list.SelectionChanged += UpdateStatus;
        _transfers.CancelClicked += t => t.Cancel();
        _transfers.ShowClicked += t => ExternalLink.OpenFolder(t.Destination);
        _transfers.ClearClicked += () => _queue?.ClearFinished();
        _edits.Saved += (remote, local) => UiThread.Post(() => UploadSaved(remote, local));
        UiClock.Tick += OnTick;
        _address = HostFormat.Address(_host, Host.Services.Vault.Current);
        UpdateButtons();
        SetStatus(TabStatus.Closed, $"{_address} · Not connected");
    }

    private Toolbar _toolbar = null!;

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
        CancelAttempt();
        _listing?.Cancel();
        CloseConnection();
        _edits.Dispose();
    }

    // Cancels the transfers, lets the running one remove its partial file, then closes the connection (in the
    // background). The tab forgets both at once.
    private void CloseConnection()
    {
        SftpTransferQueue? queue = _queue;
        SftpConnection? connection = _connection;
        _queue = null;
        _connection = null;
        _files = null;
        if (queue is null)
        {
            connection?.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            await queue.CloseAsync(TimeSpan.FromSeconds(5));
            connection?.Dispose();
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

    private void LayoutToolbar(float w)
    {
        float y = (ToolbarH - ButtonSize) / 2f, x = 8;
        foreach (IconButton button in new[] { _back, _forward, _up, _refresh })
        {
            button.Transform.SetLocalFrame(x, y, ButtonSize, ButtonSize);
            x += ButtonSize + 2;
        }
        float right = w - 8;
        foreach (IconButton button in new[] { _more, _newFolder, _download, _upload })
        {
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
        if (generation != _generation || _closing)
        {
            connection.Dispose();
            return;
        }
        _attempt = null;
        OnConnected(host, connection);
    }

    private SftpConnection CreateConnection(int generation)
    {
        var verifier = new KnownHostsVerifier(Host.Services.Vault, (info, ct) =>
            UiThread.InvokeAsync(() => AskAsync(generation, _ => HostKeyDialog.ShowAsync(Host, info, ct))));
        var connection = new SftpConnection(verifier, (prompt, ct) =>
            UiThread.InvokeAsync(() => AskAsync(generation, guard => SignInPromptDialog.ShowAsync(Host,
                $"{prompt.Username}@{(prompt.Port == 22 ? prompt.Host : $"{prompt.Host}:{prompt.Port}")}", prompt, guard, ct))));
        connection.Lost += reason => UiThread.Post(() => OnLost(connection, reason));
        _connection = connection;
        return connection;
    }

    // Prompts belong to this tab: it comes to the front first, and when it was in the background the prompt's Enter
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

    private void OnConnected(HostEntry host, SftpConnection connection)
    {
        _files = new SftpFileSystem(connection);
        _queue = new SftpTransferQueue(_files);
        _queue.Changed += () => UiThread.Post(OnTransfersChanged);
        OnTransfersChanged(); // the transfers of a previous connection are gone
        _overlay.Hide();
        InvalidateLayout();
        if (IsSaved)
            Host.RunVault(() => Host.Services.Vault.TouchHostAsync(host.Id));
        string start = _path ?? _initialPath ?? _files.Home;
        _history.Clear();
        _future.Clear();
        Load(start, select: null, keepSelection: _path is not null, fallbackHome: true);
        if (Host.ActiveTab == this && !Host.HasModal)
            Host.SetActiveKeyboardElement(_list);
    }

    private void OnLost(SftpConnection connection, string reason)
    {
        if (connection != _connection || _closing)
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
        else if (Host.Services.Vault.Current.FindHost(_host.Id) is { } saved)
            Host.EditHost(saved);
    }

    private void CancelAttempt()
    {
        _attempt?.Cancel();
        _attempt = null;
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
        if (!Connected || _files is null || string.IsNullOrWhiteSpace(path))
            return;
        string target;
        try
        {
            target = RemotePath.Resolve(_files.Home, path.Trim().StartsWith('/') || path.Trim().StartsWith('~') || _path is null
                ? path.Trim()
                : SftpFileSystem.Join(_path, path.Trim()));
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

    private void Open(SftpEntry entry)
    {
        if (!Connected)
            return;
        if (entry.IsDirectory)
            Navigate(entry.Path);
        else if (entry.IsBrokenLink)
            Host.ShowToast($"{FileFormat.Printable(entry.Name)} points to {FileFormat.Printable(entry.LinkTarget ?? "?")}, which does not exist.", ToastKind.Error);
        else
            OpenFile(entry, asText: false);
    }

    private void GoUp()
    {
        if (_path is null || _path == "/")
            return;
        string child = _path;
        _history.Push(_path);
        _future.Clear();
        Load(FileFormat.Parent(_path), select: child, keepSelection: false);
    }

    private void GoBack()
    {
        if (_history.Count == 0 || _path is null)
            return;
        string current = _path;
        string previous = _history.Pop();
        _future.Push(current);
        Load(previous, select: current.StartsWith(previous.TrimEnd('/') + "/", StringComparison.Ordinal) ? ChildOf(previous, current) : null, keepSelection: false);
    }

    private void GoForward()
    {
        if (_future.Count == 0 || _path is null)
            return;
        _history.Push(_path);
        Load(_future.Pop(), select: null, keepSelection: false);
    }

    // The entry of `ancestor` on the way to `descendant` (to select the folder one came back from).
    private static string ChildOf(string ancestor, string descendant)
    {
        string rest = descendant[(ancestor.TrimEnd('/').Length + 1)..];
        int slash = rest.IndexOf('/');
        return SftpFileSystem.Join(ancestor, slash < 0 ? rest : rest[..slash]);
    }

    /// <summary>Lists the current folder again, keeping the selection.</summary>
    public void Refresh()
    {
        if (_path is not null)
            Load(_path, select: null, keepSelection: true);
    }

    private async void Load(string path, string? select, bool keepSelection, bool fallbackHome = false)
    {
        if (_files is not { } files)
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
            IReadOnlyList<SftpEntry> entries = await files.ListAsync(path, cts.Token);
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
        catch (Exception ex) when (ex is SftpOperationException or SshSessionException)
        {
            if (cts.IsCancellationRequested || _closing)
                return;
            if (fallbackHome && path != files.Home && ex is SftpOperationException)
            {
                // A saved folder that is gone (or no longer readable): start at home instead.
                Load(files.Home, null, false);
                return;
            }
            if (ex is SshSessionException)
                return; // the Lost banner says it
            if (_path is null || !sameFolder && _path != path)
            {
                // Stay where we were; a failed first listing shows why in the list.
                if (_path is null)
                {
                    _path = path;
                    _list.SetEntries([]);
                    _list.Message = ex.Message;
                }
                else
                {
                    if (_history.Count > 0 && _history.Peek() == _path)
                        _history.Pop();
                    _list.Message = null;
                    Host.ShowToast(ex.Message, ToastKind.Error);
                }
            }
            else
            {
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
        if (_connection is null || _files is null || _overlay.Mode is SessionOverlay.OverlayMode.Ended)
            return;
        string where = _path ?? "";
        string text = _loading ? "Loading…" : FileFormat.Summary(_list.Rows);
        IReadOnlyList<SftpEntry> selected = _list.Selected;
        if (selected.Count == 1)
        {
            SftpEntry e = selected[0];
            text = $"{FileFormat.Printable(e.Name)}{(e.IsDirectory ? "" : " · " + FileFormat.Size(e.Size))} · {e.Permissions} · {FileFormat.Date(e.Modified, DateTimeOffset.Now)}";
            if (e.Kind == SftpEntryKind.Symlink)
                text += $" · → {FileFormat.Printable(e.LinkTarget ?? "?")}";
        }
        else if (selected.Count > 1)
        {
            long bytes = selected.Where(e => !e.IsDirectory).Sum(e => e.Size);
            text = $"{FileFormat.Count(selected.Count, "item")} selected · {FileFormat.Size(bytes)}";
        }
        SetStatus(TabStatus.Connected, $"{_address} · {FileFormat.Printable(where)} · {text}");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool live = Connected && _path is not null;
        _back.Enabled = live && _history.Count > 0;
        _forward.Enabled = live && _future.Count > 0;
        _up.Enabled = live && _path != "/";
        _refresh.Enabled = live;
        _upload.Enabled = live;
        _newFolder.Enabled = live;
        _download.Enabled = live && _list.Selected.Count > 0;
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
            return true;
        }
        if (!Connected || k.IsRepeat && k.Key is not (Key.Backspace))
            return false;
        switch (k.Key)
        {
            case Key.Backspace when k.Modifiers == KeyModifiers.None:
                GoUp();
                return true;
            case Key.F5 when k.Modifiers == KeyModifiers.None:
                Refresh();
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

    // ---- menus ----

    private void ShowMenu(IReadOnlyList<MenuItem> items, IconButton anchor) =>
        Host.Menu.Show(items, anchor.Transform.Computed.X, anchor.Transform.Computed.Y + anchor.Transform.Computed.Height + 4, 220,
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
            MenuItem.Separator,
            new MenuItem { Text = "Show hidden files", Hint = "Ctrl+H", IsChecked = _list.ShowHidden, Action = ToggleHidden },
            new MenuItem { Text = "Open terminal here", Icon = "terminal", IsEnabled = _path is not null, Action = OpenTerminalHere },
            new MenuItem { Text = "Copy folder path", Icon = "copy", IsEnabled = _path is not null, Action = () => CopyText(_path!) },
            new MenuItem { Text = "Go to home folder", Icon = "user", IsEnabled = live, Action = () => Navigate("~") },
            MenuItem.Separator,
            new MenuItem { Text = $"Download folder: {ShortLocal(DownloadFolder())}", IsHeader = true },
            new MenuItem { Text = "Change download folder…", Icon = "download", Action = ChangeDownloadFolder },
            MenuItem.Separator,
        ];
        items.Add(Connected
            ? new MenuItem { Text = "Disconnect", Icon = "logout", Action = Disconnect }
            : new MenuItem { Text = "Reconnect", Icon = "refresh", Action = () => Connect() });
        items.Add(MenuItem.Separator);
        items.AddRange(Host.PaneMenuItems(this));
        return items;
    }

    private List<MenuItem> ListMenu()
    {
        bool live = Connected && _path is not null;
        IReadOnlyList<SftpEntry> selected = _list.Selected;
        if (selected.Count == 0)
        {
            return
            [
                new MenuItem { Text = "New folder…", Icon = "folder-plus", Hint = "Ctrl+Shift+N", IsEnabled = live, Action = NewFolder },
                new MenuItem { Text = "New file…", Icon = "file-plus", IsEnabled = live, Action = NewFile },
                new MenuItem { Text = "Upload files…", Icon = "upload", IsEnabled = live, Action = UploadFiles },
                new MenuItem { Text = "Upload a folder…", Icon = "folder", IsEnabled = live, Action = UploadFolder },
                MenuItem.Separator,
                new MenuItem { Text = "Refresh", Icon = "refresh", Hint = "F5", IsEnabled = live, Action = Refresh },
                new MenuItem { Text = "Show hidden files", Hint = "Ctrl+H", IsChecked = _list.ShowHidden, Action = ToggleHidden },
                new MenuItem { Text = "Open terminal here", Icon = "terminal", IsEnabled = _path is not null, Action = OpenTerminalHere },
                new MenuItem { Text = "Copy folder path", Icon = "copy", IsEnabled = _path is not null, Action = () => CopyText(_path!) },
            ];
        }
        SftpEntry first = selected[0];
        bool single = selected.Count == 1;
        var items = new List<MenuItem>();
        if (single && first.IsDirectory)
        {
            items.Add(new MenuItem { Text = "Open", Icon = "folder", Hint = "Enter", Action = () => Open(first) });
            items.Add(new MenuItem { Text = "Open terminal here", Icon = "terminal", Action = () => OpenTerminalAt(first.Path) });
        }
        else if (single)
        {
            items.Add(new MenuItem { Text = "Open", Icon = "file", Hint = "Enter", IsEnabled = live && !first.IsBrokenLink, Action = () => Open(first) });
            items.Add(new MenuItem { Text = "Edit as text", Icon = "edit", IsEnabled = live && !first.IsBrokenLink, Action = () => OpenFile(first, asText: true) });
        }
        items.Add(new MenuItem { Text = $"Download to {ShortLocal(DownloadFolder())}", Icon = "download", IsEnabled = live, Action = () => Download(selected) });
        items.Add(new MenuItem { Text = "Download to…", Icon = "download", IsEnabled = live, Action = () => DownloadTo(selected) });
        items.Add(MenuItem.Separator);
        if (single)
            items.Add(new MenuItem { Text = "Rename…", Icon = "edit", Hint = "F2", IsEnabled = live, Action = () => Rename(first) });
        items.Add(new MenuItem { Text = "Move to…", Icon = "folder", IsEnabled = live, Action = () => Move(selected) });
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
        if (_files is not { } files || _path is not { } dir)
            return;
        string? error = null, name = "";
        while (true)
        {
            name = await new PromptDialog(Host, folder ? "New folder" : "New file", FileFormat.Printable(dir), null, "Name",
                "Create", error: error, initialText: name ?? "").ShowAsync();
            if (name is null)
                return;
            name = name.Trim();
            if ((error = SftpFileSystem.ValidateName(name)) is not null)
                continue;
            string path = SftpFileSystem.Join(dir, name);
            try
            {
                if (folder)
                    await files.CreateDirectoryAsync(path, CancellationToken.None);
                else
                    await files.CreateFileAsync(path, CancellationToken.None);
                if (_path == dir)
                    Load(dir, select: path, keepSelection: false);
                return;
            }
            catch (SftpOperationException ex)
            {
                error = ex.Message;
            }
            catch (SshSessionException)
            {
                return; // the Lost banner says it
            }
        }
    }

    private async void Rename(SftpEntry entry)
    {
        if (_files is not { } files)
            return;
        string dir = RemotePath.Directory(entry.Path);
        string? error = null, name = entry.Name;
        while (true)
        {
            name = await new PromptDialog(Host, "Rename", FileFormat.Printable(entry.Path), null, "New name", "Rename",
                error: error, initialText: name ?? entry.Name).ShowAsync();
            if (name is null || name == entry.Name)
                return;
            name = name.Trim();
            if ((error = SftpFileSystem.ValidateName(name)) is not null)
                continue;
            string target = SftpFileSystem.Join(dir, name);
            try
            {
                await files.RenameAsync(entry, target, CancellationToken.None);
                if (_path == dir)
                    Load(dir, select: target, keepSelection: false);
                return;
            }
            catch (SftpOperationException ex)
            {
                error = ex.Message;
            }
            catch (SshSessionException)
            {
                return;
            }
        }
    }

    // Moves entries into another folder on the same host (typed: absolute, ~ or relative to the folder shown).
    private async void Move(IReadOnlyList<SftpEntry> entries)
    {
        if (_files is not { } files || _path is not { } dir || entries.Count == 0)
            return;
        string? error = null, typed = dir;
        while (true)
        {
            typed = await new PromptDialog(Host, entries.Count == 1 ? "Move" : $"Move {entries.Count} items", FileFormat.Describe(entries),
                "The folder to move to, on the same host: a full path, ~/… or a path relative to this folder.", "Folder", "Move",
                error: error, initialText: typed ?? dir).ShowAsync();
            if (typed is null)
                return;
            string target;
            try
            {
                target = RemotePath.Resolve(files.Home, typed.Trim().StartsWith('/') || typed.Trim().StartsWith('~') ? typed.Trim() : SftpFileSystem.Join(dir, typed.Trim()));
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
            if (entries.Any(e => e.Kind == SftpEntryKind.Directory && (target == e.Path || target.StartsWith(e.Path + "/", StringComparison.Ordinal))))
            {
                error = "A folder can't be moved into itself.";
                continue;
            }
            try
            {
                if (await files.StatAsync(target, CancellationToken.None) is not { IsDirectory: true })
                {
                    error = $"{target} is not a folder.";
                    continue;
                }
                foreach (SftpEntry entry in entries)
                    await files.RenameAsync(entry, SftpFileSystem.Join(target, entry.Name), CancellationToken.None);
                Host.ShowToast($"Moved {FileFormat.Describe(entries)} to {FileFormat.Printable(target)}.", ToastKind.Success);
            }
            catch (SftpOperationException ex)
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

    private async void Delete(IReadOnlyList<SftpEntry> entries)
    {
        if (_files is not { } files || entries.Count == 0)
            return;
        bool folders = entries.Any(e => e.Kind == SftpEntryKind.Directory);
        string message = $"Delete {FileFormat.Describe(entries)} on {_address}?"
            + (folders ? " Folders are deleted with everything in them." : "")
            + (entries.Any(e => e.Kind == SftpEntryKind.Symlink) ? " Links are removed, not what they point to." : "")
            + " This can't be undone.";
        if (!await ConfirmDialog.ShowAsync(Host, entries.Count == 1 ? "Delete item?" : $"Delete {entries.Count} items?", message, "Delete", danger: true))
            return;
        string? dir = _path;
        int removed = 0;
        var progress = new Progress<string>(_ => removed++);
        SetStatus(TabStatus.Connected, $"{_address} · Deleting {FileFormat.Describe(entries)}…");
        try
        {
            foreach (SftpEntry entry in entries)
                await files.DeleteAsync(entry, progress, CancellationToken.None);
        }
        catch (SftpOperationException ex)
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

    private async void ChangePermissions(IReadOnlyList<SftpEntry> entries)
    {
        if (_files is not { } files || entries.Count == 0)
            return;
        if (await PermissionsDialog.ShowAsync(Host, entries) is not { } mode)
            return;
        string? dir = _path;
        try
        {
            foreach (SftpEntry entry in entries)
                await files.SetPermissionsAsync(entry.Path, mode, CancellationToken.None);
        }
        catch (SftpOperationException ex)
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

    private void CopyPaths(IReadOnlyList<SftpEntry> entries) =>
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

    private void OpenTerminalAt(string path) =>
        Host.OpenTab(new SessionTabContent(CurrentHost, _password, $"cd -- {RemotePath.Quote(path)}\r"));

    // ---- transfers ----

    private async void UploadFiles()
    {
        if (!Connected || _path is not { } dir)
            return;
        IReadOnlyList<string> paths;
        try
        {
            paths = await FilePicker.PickFilesAsync($"Upload to {_host.DisplayName}:{dir}");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast($"{ex.Message} Drop the files on the window instead.", ToastKind.Error);
            return;
        }
        if (paths.Count > 0)
            Upload(paths, dir);
    }

    private async void UploadFolder()
    {
        if (!Connected || _path is not { } dir)
            return;
        string? folder;
        try
        {
            folder = await FilePicker.PickFolderAsync($"Upload a folder to {_host.DisplayName}:{dir}");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast($"{ex.Message} Drop the folder on the window instead.", ToastKind.Error);
            return;
        }
        if (folder is not null)
            Upload([folder], dir);
    }

    /// <summary>Uploads files and folders dropped on the window into the folder shown.</summary>
    public void UploadDropped(IReadOnlyList<string> paths)
    {
        if (!Connected || _path is not { } dir)
        {
            Host.ShowToast("Connect first: the files go into the folder shown.", ToastKind.Error);
            return;
        }
        Upload(paths, dir);
    }

    private void Upload(IReadOnlyList<string> paths, string dir)
    {
        if (_queue is not { } queue)
            return;
        queue.Upload(paths, dir, names => UiThread.InvokeAsync(() => ConflictDialog.ShowAsync(Host, names, dir)));
    }

    private void Download(IReadOnlyList<SftpEntry> entries) => StartDownload(entries, DownloadFolder());

    private async void DownloadTo(IReadOnlyList<SftpEntry> entries)
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

    private void StartDownload(IReadOnlyList<SftpEntry> entries, string folder)
    {
        if (_queue is not { } queue || entries.Count == 0)
            return;
        queue.Download(entries, folder, names => UiThread.InvokeAsync(() => ConflictDialog.ShowAsync(Host, names, folder)));
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

    // Opens a file in a local program: downloaded into a private folder, watched, and uploaded back on every save.
    private async void OpenFile(SftpEntry entry, bool asText)
    {
        if (_queue is not { } queue)
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
        SftpTransfer transfer = queue.DownloadFile(entry, local);
        _reported.Add(transfer); // its failure is reported below
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
        if (!Connected || _queue is not { } queue)
        {
            Host.ShowToast($"{name} was saved, but the connection is closed: reconnect and save it again.", ToastKind.Error);
            RequestAttention();
            return;
        }
        SftpTransfer transfer = queue.UploadFile(local, remote);
        _reported.Add(transfer); // its failure is reported below
        await transfer.Completion;
        if (_closing)
            return;
        TransferProgress result = transfer.Snapshot();
        if (result.State == TransferState.Done)
        {
            Host.ShowToast($"Saved {name} to {_host.DisplayName}.", ToastKind.Success);
            if (_path == RemotePath.Directory(remote))
                Load(_path, select: null, keepSelection: true);
        }
        else if (result.State == TransferState.Failed)
        {
            Host.ShowToast($"Couldn't save {name} to {_host.DisplayName}: {result.Error} Save it again to retry.", ToastKind.Error);
            RequestAttention();
        }
    }

    private void OnTransfersChanged()
    {
        if (_closing)
            return;
        IReadOnlyList<SftpTransfer> transfers = _queue?.Transfers ?? [];
        float before = _transfers.PreferredHeight;
        _transfers.SetTransfers(transfers);
        if (Math.Abs(before - _transfers.PreferredHeight) > 0.5f)
            InvalidateLayout();
        SetSizeText(_transfers.Summary());
        // Uploads into the folder shown appear in it as soon as they are done.
        if (_path is { } dir && transfers.Any(t => t.Direction == TransferDirection.Upload && t.Snapshot().State == TransferState.Done
                && (t.Destination == dir || RemotePath.Directory(t.Destination) == dir) && _refreshedAfter.Add(t)))
            Load(dir, select: null, keepSelection: true);
        foreach (SftpTransfer failed in transfers.Where(t => t.Snapshot().State == TransferState.Failed && _reported.Add(t)))
            Host.ShowToast($"{(failed.Direction == TransferDirection.Upload ? "Upload" : "Download")} of {FileFormat.Printable(failed.Title)} failed: {failed.Snapshot().Error}", ToastKind.Error);
    }

    // Transfers whose end was handled (the uploads that refreshed the folder; failures reported, or reported by the
    // code that started them).
    private readonly HashSet<SftpTransfer> _refreshedAfter = [], _reported = [];

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
