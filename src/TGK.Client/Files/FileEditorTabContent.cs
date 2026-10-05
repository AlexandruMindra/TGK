using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blossom.Core.Visual;
using Silk.NET.Input;
using Button = TGK.Client.Controls.Button;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Input;
using TGK.Client.Main;
using TGK.Core.Agents;
using TGK.Core.Files;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

/// <summary>How a file opens in the built-in editor.</summary>
public enum EditorMode
{
    /// <summary>Editable; a log (by its name or folder) opens as <see cref="View"/>.</summary>
    Auto,
    Edit,
    /// <summary>Read only, at the end, following what is added (a log).</summary>
    View,
}

/// <summary>
/// A text file of a host (or of this computer) opened in TGK itself, beside the file pane it came from: edit a
/// configuration file and save it straight back, or read a log as it grows, without downloading anything. Large files
/// open read only at their end. Files the user can't read or write are opened or saved as root through sudo, when asked.
/// Saving checks that nobody changed the file meanwhile; closing with unsaved changes asks first.
/// </summary>
public sealed class FileEditorTabContent : TabContent, IKeyInput
{
    /// <summary>Larger files open read only, showing their end.</summary>
    public const long MaxEditableBytes = 8L * 1024 * 1024;

    private const int TailBytes = 2 * 1024 * 1024, FollowChunk = 1024 * 1024, MaxFollowLines = 200_000;
    private const float ToolbarH = 44, ButtonSize = 30, FindH = 40;
    private const long FollowEveryMs = 1500;

    private readonly IFileSystem _fs;
    private readonly string _path, _place;
    private readonly Func<CancellationToken, Task<RemoteConnection>>? _connectCommands;
    private readonly Func<FilesSession?>? _currentSession; // the file pane's connection now (after a reconnect there)
    private readonly TextEditor _editor = new();
    private readonly Label _title = new("", Theme.FontSm, Theme.TextPrimary) { Mono = true };
    private readonly IconButton _find = new("search"), _reload = new("refresh"), _follow = new("arrow-down"), _more = new("more");
    private readonly Button _save = new("Save", ButtonVariant.Primary, "save") { FontSize = Theme.FontSm };
    private readonly Button _edit = new("Edit", ButtonVariant.Secondary, "edit") { FontSize = Theme.FontSm };
    private readonly Label _message = new("", Theme.FontBase, Theme.TextSecondary) { MaxLines = 8, Align = TextAlignment.Center };
    private readonly Button _messageAction = new("", ButtonVariant.Primary), _messageOther = new("", ButtonVariant.Secondary);
    private readonly TextField _findField = new("Find") { LeadingIcon = "search", FontSize = Theme.FontSm, Mono = true };
    private readonly TextField _replaceField = new("Replace with") { FontSize = Theme.FontSm, Mono = true };
    private readonly IconButton _findPrev = new("arrow-up"), _findNext = new("arrow-down"), _findClose = new("x");
    private readonly Button _matchCase = new("Aa", ButtonVariant.Ghost) { FontSize = Theme.FontSm };
    private readonly Button _replaceOne = new("Replace", ButtonVariant.Secondary) { FontSize = Theme.FontSm };
    private readonly Button _replaceAll = new("All", ButtonVariant.Secondary) { FontSize = Theme.FontSm };
    private readonly Label _findCount = new("", Theme.FontSm, Theme.TextMuted);
    private Bar _toolbar = null!, _findBar = null!;
    private FilesSession? _session;
    private SudoFiles? _root;            // set once the file is read or saved as root
    private TextContent _content = TextContent.New("");
    private EditorMode _mode;
    private Action? _messageActionHandler, _messageOtherHandler;
    private CancellationTokenSource? _cts = new();
    private Decoder? _followDecoder;
    private long _knownSize = -1, _readTo;   // the file as last read or saved; where reading continues (follow)
    private long _shownFrom;                 // where the text shown starts in the file (only its end is shown)
    private DateTimeOffset _knownModified;
    private long _totalSize;
    private bool _partial;                   // only the end of the file is shown
    private bool _ready, _busy, _closing, _following, _matchCaseOn, _discard;
    private long _lastPoll;
    private int _lastVersion = -1;

    /// <param name="session">A host's connection (shared with its file pane), or null for this computer's files.</param>
    /// <param name="connectCommands">Opens a command connection to the host (for sudo); null for this computer.</param>
    /// <param name="currentSession">The file pane's connection as it is now, used when this one was lost.</param>
    public FileEditorTabContent(IFileSystem fs, FilesSession? session, string path, string place, EditorMode mode,
        Func<CancellationToken, Task<RemoteConnection>>? connectCommands, Func<FilesSession?>? currentSession)
    {
        _fs = fs;
        _session = session?.AddRef();
        _path = path;
        _place = place;
        _mode = mode == EditorMode.Auto ? (LooksLikeLog(path) ? EditorMode.View : EditorMode.Edit) : mode;
        _connectCommands = connectCommands;
        _currentSession = currentSession;
        ReceivesKeyboard = true;
        SetTitle(fs.NameOf(path));
    }

    /// <summary>The file shown.</summary>
    public string Path => _path;

    public IFileSystem FileSystem => _fs;

    public override VisualElement? DefaultFocus => _ready ? _editor : null;

    public override bool HasUnsavedChanges => _ready && _editor.Document.IsModified && !_discard;

    /// <summary>A log by its name (<c>*.log</c>, rotated <c>*.log.1</c>) or folder (<c>/var/log</c>).</summary>
    public static bool LooksLikeLog(string path)
    {
        string lower = path.ToLowerInvariant();
        int slash = Math.Max(lower.LastIndexOf('/'), lower.LastIndexOf('\\'));
        string name = lower[(slash + 1)..];
        return name.EndsWith(".log", StringComparison.Ordinal) || name.Contains(".log.", StringComparison.Ordinal)
            || lower.StartsWith("/var/log/", StringComparison.Ordinal) || name is "syslog" or "messages" or "journal.txt";
    }

    public override void OnAttached()
    {
        _toolbar = new Bar(bottomBorder: true);
        foreach (VisualElement child in new VisualElement[] { _title, _find, _reload, _follow, _edit, _save, _more })
            _toolbar.AddChild(child);
        _findBar = new Bar(bottomBorder: true) { Visible = false };
        foreach (VisualElement child in new VisualElement[] { _findField, _findCount, _matchCase, _findPrev, _findNext, _replaceField, _replaceOne, _replaceAll, _findClose })
            _findBar.AddChild(child);
        AddChild(_toolbar);
        AddChild(_findBar);
        AddChild(_editor);
        AddChild(_message);
        AddChild(_messageAction);
        AddChild(_messageOther);

        _find.Clicked += () => ShowFind(replace: false);
        _reload.Clicked += () => _ = ReloadAsync();
        _follow.Clicked += () => SetFollowing(!_following);
        _edit.Clicked += StartEditing;
        _save.Clicked += () => _ = SaveAsync();
        _more.Clicked += () => ShowMenu(MoreMenu(), _more);
        _messageAction.Clicked += () => _messageActionHandler?.Invoke();
        _messageOther.Clicked += () => _messageOtherHandler?.Invoke();
        _editor.MenuRequested += (x, y) => Host.Menu.Show(EditorMenu(), x, y, 220);
        _editor.Document.CaretMoved += UpdateStatus;
        _editor.Document.Changed += OnTextChanged;

        _findField.Changed += _ => OnFindChanged();
        _findField.Submitted += () => FindNext(forward: true);
        _findField.KeyPreview = k =>
        {
            if (k.IsEnter && k.Modifiers == KeyModifiers.Shift)
            {
                FindNext(forward: false);
                return true;
            }
            return false;
        };
        _findField.Escaped += HideFind;
        _replaceField.Submitted += ReplaceOne;
        _replaceField.Escaped += HideFind;
        _findPrev.Clicked += () => FindNext(forward: false);
        _findNext.Clicked += () => FindNext(forward: true);
        _findClose.Clicked += HideFind;
        _matchCase.Clicked += () =>
        {
            _matchCaseOn = !_matchCaseOn;
            _matchCase.Variant = _matchCaseOn ? ButtonVariant.Primary : ButtonVariant.Ghost;
            OnFindChanged();
        };
        _replaceOne.Clicked += ReplaceOne;
        _replaceAll.Clicked += ReplaceAll;
        UiClock.Tick += OnTick;
        UpdateButtons();
        ShowMessage("Opening…");
    }

    public override void OnShown()
    {
        if (_lastVersion == -1)
        {
            _lastVersion = 0;
            _ = LoadAsync(keepPosition: false);
        }
    }

    public override void OnClosing()
    {
        _closing = true;
        UiClock.Tick -= OnTick;
        _cts?.Cancel();
        _cts = null;
        RemoteConnection? commands = _root?.Connection;
        _root = null;
        commands?.Dispose();
        _session?.Release();
        _session = null;
    }

    public override async Task<bool> ConfirmCloseAsync()
    {
        if (!HasUnsavedChanges)
            return true;
        Host.ActivateTab(this);
        SaveChoice choice = await SaveChangesDialog.ShowAsync(Host, _fs.NameOf(_path), $"{_place}: {FileFormat.Printable(_path)}");
        switch (choice)
        {
            case SaveChoice.Save:
                return await SaveAsync();
            case SaveChoice.Discard:
                _discard = true;
                return true;
            default:
                return false;
        }
    }

    // ---- layout ----

    protected override void LayoutChildren()
    {
        float w = Transform.Computed.Width, h = Transform.Computed.Height;
        _toolbar.Transform.SetLocalFrame(0, 0, w, ToolbarH);
        float y = ToolbarH;
        if (_findBar.Visible)
        {
            _findBar.Transform.SetLocalFrame(0, y, w, FindH);
            LayoutFindBar(w);
            y += FindH;
        }
        _editor.Transform.SetLocalFrame(0, y, w, Math.Max(0, h - y));
        float messageW = Math.Min(w - 40, 520);
        float messageH = _message.MeasureHeight(messageW);
        float top = Math.Max(y + 20, (h - messageH) / 2 - 30);
        _message.Transform.SetLocalFrame((w - messageW) / 2, top, messageW, messageH);
        float actionW = _messageAction.Visible ? _messageAction.PreferredWidth : 0, otherW = _messageOther.Visible ? _messageOther.PreferredWidth : 0;
        float buttonsW = actionW + otherW + (actionW > 0 && otherW > 0 ? 8 : 0);
        float bx = (w - buttonsW) / 2;
        _messageOther.Transform.SetLocalFrame(bx, top + messageH + 16, otherW, 32);
        _messageAction.Transform.SetLocalFrame(bx + otherW + (otherW > 0 ? 8 : 0), top + messageH + 16, actionW, 32);
        LayoutToolbar(w);
    }

    private void LayoutToolbar(float w)
    {
        float y = (ToolbarH - ButtonSize) / 2f;
        float right = w - 8;
        void Place(VisualElement element, float width)
        {
            if (!element.Visible)
                return;
            right -= width;
            element.Transform.SetLocalFrame(right, y, width, ButtonSize);
            right -= 4;
        }
        Place(_more, ButtonSize);
        Place(_save, _save.PreferredWidth);
        Place(_edit, _edit.PreferredWidth);
        Place(_follow, ButtonSize);
        Place(_reload, ButtonSize);
        Place(_find, ButtonSize);
        _title.Transform.SetLocalFrame(14, y, Math.Max(0, right - 20), ButtonSize);
    }

    private void LayoutFindBar(float w)
    {
        const float H = 28;
        float y = (FindH - H) / 2f;
        bool replace = _replaceField.Visible;
        float x = 8;
        float fieldW = Math.Clamp((w - 300) / (replace ? 2 : 1), 120, 320);
        _findField.Transform.SetLocalFrame(x, y, fieldW, H);
        x += fieldW + 6;
        _matchCase.Transform.SetLocalFrame(x, y, 34, H);
        x += 36;
        _findPrev.Transform.SetLocalFrame(x, y, H, H);
        x += H + 2;
        _findNext.Transform.SetLocalFrame(x, y, H, H);
        x += H + 8;
        float countW = Math.Max(0, Gfx.Measure(_findCount.Text, Theme.FontSm) + 4);
        _findCount.Transform.SetLocalFrame(x, y, countW, H);
        x += countW + 10;
        if (replace)
        {
            _replaceField.Transform.SetLocalFrame(x, y, fieldW, H);
            x += fieldW + 6;
            _replaceOne.Transform.SetLocalFrame(x, y, _replaceOne.PreferredWidth, H);
            x += _replaceOne.PreferredWidth + 4;
            _replaceAll.Transform.SetLocalFrame(x, y, _replaceAll.PreferredWidth, H);
        }
        _findClose.Transform.SetLocalFrame(w - 8 - H, y, H, H);
    }

    // ---- loading ----

    private CancellationToken Token => _cts?.Token ?? new CancellationToken(canceled: true);

    private async Task LoadAsync(bool keepPosition)
    {
        if (_closing || _busy)
            return;
        _busy = true;
        SetFollowing(false);
        TextPos caret = _editor.Document.Caret;
        (int firstLine, _) = _editor.VisibleLines;
        if (!_ready)
            ShowMessage("Opening…");
        try
        {
            CancellationToken ct = Token;
            FileEntry? entry = null;
            try
            {
                entry = await _fs.StatAsync(_path, ct);
            }
            catch (FileOperationException) when (_root is not null)
            {
                // Its folder may be closed to this user too: root reads it below.
            }
            if (entry is null && _root is null)
                throw new FileOperationException($"{_path} does not exist (any more).");
            if (entry is { IsDirectory: true })
                throw new FileOperationException($"{_path} is a folder.");
            long size = _root is not null ? await _root.SizeAsync(_path, ct) : entry!.Size;
            bool tail = size > MaxEditableBytes || (_mode == EditorMode.View && size > TailBytes);
            long offset = tail ? Math.Max(0, size - TailBytes) : 0;
            int count = (int)Math.Min(int.MaxValue - 1, size - offset + 64 * 1024); // a little more: it may have grown
            byte[] bytes = await ReadAsync(offset, count, ct);
            if (_closing)
                return;
            long readTo = offset + bytes.Length;
            long shownFrom = offset;
            if (offset > 0)
            {
                // Starts mid-file: from the first whole line.
                int newline = Array.IndexOf(bytes, (byte)'\n');
                bytes = newline >= 0 ? bytes[(newline + 1)..] : [];
                shownFrom = readTo - bytes.Length;
            }
            TextContent? content = TextContent.Decode(bytes);
            if (content is null)
            {
                ShowMessage($"{_fs.NameOf(_path)} is not a text file ({FileFormat.Size(size)}).", null, null, "Close", () => Host.CloseTab(this));
                return;
            }
            _content = content;
            _partial = offset > 0;
            _totalSize = size;
            _readTo = readTo;
            _shownFrom = shownFrom;
            _knownSize = entry?.Size ?? size;
            _knownModified = entry?.Modified ?? default;
            _followDecoder = (content.IsLatin1 ? Encoding.Latin1 : Encoding.UTF8).GetDecoder();
            TextDocument doc = _editor.Document;
            doc.SetText(content.Text);
            doc.ReadOnly = _mode == EditorMode.View || _partial;
            _ready = true;
            HideMessage();
            if (keepPosition)
            {
                doc.MoveTo(caret);
                _editor.RevealLine(firstLine);
            }
            else if (_mode == EditorMode.View)
            {
                doc.MoveTo(doc.End);
                _editor.ScrollToEnd();
            }
            if (Host.ActiveTab == this)
                Host.SetActiveKeyboardElement(_editor);
            if (_mode == EditorMode.View && !keepPosition)
                SetFollowing(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is FileOperationException or SshSessionException)
        {
            if (_closing)
                return;
            bool denied = ex.Message.StartsWith("Permission denied", StringComparison.Ordinal);
            if (denied && _root is null && _connectCommands is not null)
            {
                ShowMessage($"You can't read {FileFormat.Printable(_path)} as {UserName}.", "Open as root (sudo)", () => _ = OpenAsRootAsync(), "Close", () => Host.CloseTab(this));
            }
            else if (_ready)
            {
                Host.ShowToast($"Couldn't reload {_fs.NameOf(_path)}: {ex.Message}", ToastKind.Error);
            }
            else
            {
                ShowMessage($"Couldn't open {FileFormat.Printable(_path)}: {ex.Message}", "Try again", () => _ = LoadAsync(keepPosition: false), "Close", () => Host.CloseTab(this));
            }
        }
        finally
        {
            _busy = false;
            UpdateButtons();
            UpdateStatus();
        }
    }

    private string UserName => _session?.Request.Username is { Length: > 0 } user ? user : "this user";

    private async Task ReloadAsync()
    {
        if (!_ready)
        {
            await LoadAsync(keepPosition: false);
            return;
        }
        if (_editor.Document.IsModified && !await ConfirmDialog.ShowAsync(Host, "Discard your changes?",
                $"Reloading {_fs.NameOf(_path)} discards the changes not saved yet.", "Reload", danger: true))
            return;
        await LoadAsync(keepPosition: true);
    }

    private async Task OpenAsRootAsync()
    {
        if (await RootAsync() is null)
            return;
        await LoadAsync(keepPosition: _ready);
    }

    // The command connection for sudo, opened once (the host's prompts come up as for the file pane).
    private async Task<SudoFiles?> RootAsync()
    {
        if (_root is not null)
            return _root;
        if (_connectCommands is null)
            return null;
        ShowBusyStatus("Connecting for sudo…");
        try
        {
            RemoteConnection commands = await _connectCommands(Token);
            if (_closing)
            {
                commands.Dispose();
                return null;
            }
            _root = new SudoFiles(commands, AskSudoPasswordAsync);
            return _root;
        }
        catch (Exception ex) when (ex is SshSessionException or OperationCanceledException or InvalidOperationException)
        {
            if (!_closing && ex is not OperationCanceledException)
                Host.ShowToast($"Couldn't connect for sudo: {ex.Message}", ToastKind.Error);
            return null;
        }
        finally
        {
            UpdateStatus();
        }
    }

    private bool _triedSavedPassword;

    // The account's password is usually the sudo password: the saved (or typed) one is tried first, once.
    private Task<string?> AskSudoPasswordAsync(string? error, CancellationToken ct)
    {
        if (error is null && !_triedSavedPassword && _session?.Request.Password is { Length: > 0 } saved)
        {
            _triedSavedPassword = true;
            return Task.FromResult<string?>(saved);
        }
        SshConnectRequest? request = _session?.Request;
        string target = request is null ? _place : $"{request.Username}@{(request.Port == 22 ? request.Host : $"{request.Host}:{request.Port}")}";
        return UiThread.InvokeAsync(() => new PromptDialog(Host, "sudo password", target,
            $"Enter the sudo password of {request?.Username ?? "this user"} to open and save {_fs.NameOf(_path)} as root. It is kept while this tab is open.",
            "Password", "Continue", isPassword: true, error: error, guardEnter: true).ShowAsync(ct));
    }

    private Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) =>
        _root is { } root ? root.ReadAsync(_path, offset, count, ct) : Files().ReadAsync(_path, offset, count, ct);

    // The pane's files: after a reconnect of the file pane its new connection is taken over.
    private IFileSystem Files()
    {
        if (_session is null || _session.IsConnected)
            return _session?.Files ?? _fs;
        if (_currentSession?.Invoke() is { IsConnected: true } current && current != _session)
        {
            FilesSession old = _session;
            _session = current.AddRef();
            old.Release();
            return current.Files;
        }
        throw new FileOperationException($"The connection to {_place} was lost. Reconnect its file pane (Reconnect at the bottom), then try again; your changes stay here.");
    }

    // ---- saving ----

    private async Task<bool> SaveAsync(bool asRoot = false)
    {
        TextDocument doc = _editor.Document;
        if (!_ready || doc.ReadOnly || _busy || _closing)
            return false;
        _busy = true;
        UpdateButtons();
        ShowBusyStatus("Saving…");
        int version = doc.Version;
        bool saved = false;
        try
        {
            CancellationToken ct = Token;
            byte[] bytes = _content.With(_content.ToFileNewlines(doc.Text)).Encode();
            FileEntry? now = null;
            try
            {
                now = await Files().StatAsync(_path, ct);
            }
            catch (FileOperationException) when (_root is not null || asRoot)
            {
            }
            if (now is not null && _knownSize >= 0 && (now.Size != _knownSize || now.Modified != _knownModified)
                && !await ConfirmDialog.ShowAsync(Host, "The file changed meanwhile",
                    $"{FileFormat.Printable(_path)} was changed on {_place} after you opened it ({FileFormat.Size(now.Size)}, {now.Modified.ToLocalTime():g}). Saving replaces those changes with yours.",
                    "Save anyway", danger: true))
                return false;
            if (asRoot || _root is not null)
            {
                SudoFiles? root = await RootAsync();
                if (root is null)
                    return false;
                await root.WriteAsync(_path, bytes, ct);
            }
            else
            {
                await Files().WriteAsync(_path, bytes, ct);
            }
            try
            {
                FileEntry? after = await Files().StatAsync(_path, ct);
                _knownSize = after?.Size ?? bytes.Length;
                _knownModified = after?.Modified ?? default;
            }
            catch (FileOperationException)
            {
                _knownSize = -1; // can't tell: no check next time
            }
            if (doc.Version == version)
                doc.MarkSaved();
            saved = true;
            FilesTabContent.NotifyChanged(_fs, _path);
            Host.ShowToast($"Saved {_fs.NameOf(_path)}{(_root is not null ? " as root" : "")}.", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is FileOperationException or SshSessionException)
        {
            if (_closing)
                return false;
            bool denied = ex.Message.StartsWith("Permission denied", StringComparison.Ordinal);
            if (denied && _root is null && _connectCommands is not null)
            {
                _busy = false;
                if (await ConfirmDialog.ShowAsync(Host, "Save as root?",
                        $"{UserName} can't write {FileFormat.Printable(_path)} on {_place}. Save it as root with sudo? It keeps its owner and permissions.",
                        "Save as root"))
                    return await SaveAsync(asRoot: true);
                return false;
            }
            Host.ShowToast($"Couldn't save {_fs.NameOf(_path)}: {ex.Message}", ToastKind.Error);
            RequestAttention();
        }
        finally
        {
            _busy = false;
            if (!_closing)
            {
                UpdateButtons();
                UpdateStatus();
                UpdateTitle();
            }
        }
        return saved;
    }

    // ---- view mode and following ----

    private void StartEditing()
    {
        if (!_ready || _partial)
            return;
        SetFollowing(false);
        _mode = EditorMode.Edit;
        _editor.Document.ReadOnly = false;
        Host.SetActiveKeyboardElement(_editor);
        UpdateButtons();
        UpdateStatus();
    }

    private void SetFollowing(bool on)
    {
        on &= _ready && _editor.Document.ReadOnly;
        if (_following == on)
            return;
        _following = on;
        _follow.Active = on;
        _lastPoll = UiClock.NowMs;
        if (on)
        {
            _editor.Document.MoveTo(_editor.Document.End);
            _editor.ScrollToEnd();
        }
        UpdateStatus();
    }

    private void OnTick()
    {
        if (!_following || _busy || _closing || UiClock.NowMs - _lastPoll < FollowEveryMs)
            return;
        _lastPoll = UiClock.NowMs;
        _ = PollAsync();
    }

    // Reads what was added to the file since the last read; a file that shrank (rotated, truncated) is read again.
    private async Task PollAsync()
    {
        _busy = true;
        try
        {
            CancellationToken ct = Token;
            long size = _root is not null ? await _root.SizeAsync(_path, ct) : (await Files().StatAsync(_path, ct))?.Size ?? -1;
            if (_closing || !_following)
                return;
            if (size < 0)
            {
                SetFollowing(false);
                Host.ShowToast($"{_fs.NameOf(_path)} is gone.", ToastKind.Error);
                return;
            }
            if (size < _readTo)
            {
                _busy = false;
                await LoadAsync(keepPosition: false);
                return;
            }
            if (size == _readTo)
                return;
            byte[] bytes = await ReadAsync(_readTo, (int)Math.Min(FollowChunk, size - _readTo), ct);
            if (_closing || bytes.Length == 0)
                return;
            _readTo += bytes.Length;
            _totalSize = Math.Max(size, _readTo);
            bool atEnd = _editor.IsAtEnd;
            Decoder decoder = _followDecoder ??= Encoding.UTF8.GetDecoder();
            char[] chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length)];
            int n = decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
            TextDocument doc = _editor.Document;
            doc.Append(new string(chars, 0, n));
            if (doc.LineCount > MaxFollowLines)
            {
                int dropped = doc.LineCount - MaxFollowLines;
                for (int line = 0; line < dropped; line++)
                    _shownFrom += Encoding.UTF8.GetByteCount(doc[line]) + 1;
                doc.RemoveFirstLines(dropped);
                _partial = true;
            }
            if (atEnd)
            {
                doc.MoveTo(doc.End);
                _editor.ScrollToEnd();
            }
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is FileOperationException or SshSessionException)
        {
            if (!_closing)
            {
                SetFollowing(false);
                Host.ShowToast($"Stopped following {_fs.NameOf(_path)}: {ex.Message}", ToastKind.Error);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- find and replace ----

    private void ShowFind(bool replace)
    {
        if (!_ready)
            return;
        TextDocument doc = _editor.Document;
        if (doc.HasSelection && doc.SelectionStart.Line == doc.SelectionEnd.Line)
            _findField.Text = doc.SelectedText;
        replace &= !doc.ReadOnly;
        bool layout = !_findBar.Visible || _replaceField.Visible != replace;
        _findBar.Visible = true;
        _replaceField.Visible = _replaceOne.Visible = _replaceAll.Visible = replace;
        if (layout)
            InvalidateLayout();
        Host.SetActiveKeyboardElement(_findField);
        _findField.SelectAll();
        OnFindChanged();
    }

    private void HideFind()
    {
        if (!_findBar.Visible)
            return;
        _findBar.Visible = false;
        _editor.SetHighlight(null, false);
        InvalidateLayout();
        Host.SetActiveKeyboardElement(_editor);
    }

    private void OnFindChanged()
    {
        string query = _findField.Text;
        _editor.SetHighlight(query, _matchCaseOn);
        int count = _editor.Document.CountMatches(query, _matchCaseOn);
        _findCount.Text = query.Length == 0 ? "" : count == 0 ? "No results" : FileFormat.Count(count, "match").Replace("matchs", "matches");
        _findCount.Color = query.Length > 0 && count == 0 ? Theme.Danger : Theme.TextMuted;
        if (query.Length > 0 && count > 0)
            Select(_editor.Document.Find(query, _editor.Document.SelectionStart, forward: true, _matchCaseOn));
        InvalidateLayout();
    }

    private void FindNext(bool forward)
    {
        TextDocument doc = _editor.Document;
        string query = _findField.Text;
        if (query.Length == 0)
        {
            ShowFind(replace: false);
            return;
        }
        Select(doc.Find(query, forward ? doc.SelectionEnd : doc.SelectionStart, forward, _matchCaseOn));
    }

    private void Select((TextPos Start, TextPos End)? match)
    {
        if (match is not { } m)
            return;
        _editor.Document.Select(m.Start, m.End);
        _editor.RevealLine(m.Start.Line);
    }

    private void ReplaceOne()
    {
        TextDocument doc = _editor.Document;
        string query = _findField.Text;
        if (doc.ReadOnly || query.Length == 0)
            return;
        if (string.Equals(doc.SelectedText, query, _matchCaseOn ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            doc.Insert(_replaceField.Text);
        FindNext(forward: true);
        OnFindCountOnly();
    }

    private void ReplaceAll()
    {
        int count = _editor.Document.ReplaceAll(_findField.Text, _replaceField.Text, _matchCaseOn);
        if (count > 0)
            Host.ShowToast($"Replaced {FileFormat.Count(count, "occurrence")}.", ToastKind.Info);
        OnFindCountOnly();
    }

    private void OnFindCountOnly()
    {
        int count = _editor.Document.CountMatches(_findField.Text, _matchCaseOn);
        _findCount.Text = count == 0 ? "No results" : FileFormat.Count(count, "match").Replace("matchs", "matches");
        InvalidateLayout();
    }

    private async void GoToLine()
    {
        if (!_ready)
            return;
        TextDocument doc = _editor.Document;
        string? typed = await new PromptDialog(Host, "Go to line", _fs.NameOf(_path), $"A line from 1 to {doc.LineCount:N0}.", "Line", "Go",
            initialText: (doc.Caret.Line + 1).ToString()).ShowAsync();
        if (typed is null || !int.TryParse(typed.Trim().Replace(",", "").Replace(".", ""), out int line))
            return;
        line = Math.Clamp(line, 1, doc.LineCount) - 1;
        doc.MoveTo(new TextPos(line, 0));
        _editor.RevealLine(line);
        Host.SetActiveKeyboardElement(_editor);
    }

    // ---- keyboard ----

    public bool OnKey(KeyStroke k)
    {
        switch (k.Key)
        {
            case Key.S when k.Modifiers == KeyModifiers.Ctrl:
                _ = SaveAsync();
                return true;
            case Key.F when k.Modifiers == KeyModifiers.Ctrl:
                ShowFind(replace: false);
                return true;
            case Key.H when k.Modifiers == KeyModifiers.Ctrl:
                ShowFind(replace: true);
                return true;
            case Key.G when k.Modifiers == KeyModifiers.Ctrl:
                GoToLine();
                return true;
            case Key.F3 when k.Modifiers is KeyModifiers.None or KeyModifiers.Shift:
                FindNext(forward: !k.Shift);
                return true;
            case Key.F5 when k.Modifiers == KeyModifiers.None:
                _ = ReloadAsync();
                return true;
            case Key.Escape when _findBar.Visible:
                HideFind();
                return true;
            default:
                return false;
        }
    }

    public void OnText(string text) { }

    // ---- menus ----

    private void ShowMenu(IReadOnlyList<MenuItem> items, IconButton anchor) =>
        Host.Menu.Show(items, anchor.Transform.Computed.X + anchor.Transform.Computed.Width - 240, anchor.Transform.Computed.Y + anchor.Transform.Computed.Height + 4, 240,
            anchor.Transform.Computed.Height);

    private List<MenuItem> MoreMenu()
    {
        TextDocument doc = _editor.Document;
        var items = new List<MenuItem>
        {
            new() { Text = "Find…", Icon = "search", Hint = "Ctrl+F", IsEnabled = _ready, Action = () => ShowFind(replace: false) },
            new() { Text = "Replace…", Hint = "Ctrl+H", IsEnabled = _ready && !doc.ReadOnly, Action = () => ShowFind(replace: true) },
            new() { Text = "Go to line…", Hint = "Ctrl+G", IsEnabled = _ready, Action = GoToLine },
            MenuItem.Separator,
            new() { Text = "Reload from the file", Icon = "refresh", Hint = "F5", Action = () => _ = ReloadAsync() },
        };
        if (_connectCommands is not null && _root is null)
            items.Add(new MenuItem { Text = doc.ReadOnly ? "Open as root (sudo)" : "Save as root (sudo)", Icon = "shield", IsEnabled = _ready,
                Action = () => _ = doc.ReadOnly ? OpenAsRootAsync() : SaveAsync(asRoot: true) });
        if (_ready && doc.ReadOnly && !_partial)
            items.Add(new MenuItem { Text = "Edit", Icon = "edit", Action = StartEditing });
        items.Add(MenuItem.Separator);
        items.Add(new MenuItem { Text = "Copy path", Icon = "copy", Action = () => Blossom.Shell.SetClipboardText(_path) });
        return items;
    }

    private List<MenuItem> EditorMenu()
    {
        TextDocument doc = _editor.Document;
        bool editable = !doc.ReadOnly;
        return
        [
            new() { Text = "Undo", Hint = "Ctrl+Z", IsEnabled = editable && doc.CanUndo, Action = () => _editor.RunCommand("undo") },
            new() { Text = "Redo", Hint = "Ctrl+Y", IsEnabled = editable && doc.CanRedo, Action = () => _editor.RunCommand("redo") },
            MenuItem.Separator,
            new() { Text = "Cut", Hint = "Ctrl+X", IsEnabled = editable, Action = () => _editor.RunCommand("cut") },
            new() { Text = "Copy", Icon = "copy", Hint = "Ctrl+C", Action = () => _editor.RunCommand("copy") },
            new() { Text = "Paste", Icon = "clipboard", Hint = "Ctrl+V", IsEnabled = editable, Action = () => _editor.RunCommand("paste") },
            new() { Text = "Select all", Hint = "Ctrl+A", Action = () => _editor.RunCommand("select-all") },
            MenuItem.Separator,
            new() { Text = "Find…", Icon = "search", Hint = "Ctrl+F", Action = () => ShowFind(replace: false) },
            new() { Text = "Go to line…", Hint = "Ctrl+G", Action = GoToLine },
        ];
    }

    // ---- state shown ----

    private void OnTextChanged()
    {
        if (_editor.Document.Version != _lastVersion)
        {
            _lastVersion = _editor.Document.Version;
            UpdateTitle();
            if (_findBar.Visible)
                OnFindCountOnly();
        }
    }

    private void UpdateTitle()
    {
        string name = _fs.NameOf(_path);
        SetTitle(_editor.Document.IsModified ? $"● {name}" : name);
        _title.Text = $"{(_editor.Document.IsModified ? "● " : "")}{_place}: {FileFormat.Printable(_path)}";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        TextDocument doc = _editor.Document;
        bool view = _ready && doc.ReadOnly;
        _follow.Visible = view;
        _edit.Visible = view && !_partial;
        _save.Visible = _ready && !doc.ReadOnly;
        _save.Enabled = _ready && !_busy && doc.IsModified;
        _find.Enabled = _reload.Enabled = _ready;
        if (_title.Text.Length == 0)
            _title.Text = $"{_place}: {FileFormat.Printable(_path)}";
        InvalidateLayout();
    }

    private void UpdateStatus()
    {
        if (_closing)
            return;
        TextDocument doc = _editor.Document;
        var sb = new StringBuilder($"{_place}: {FileFormat.Printable(_path)}");
        if (_ready)
        {
            sb.Append($" · Ln {doc.Caret.Line + 1}, Col {doc.Caret.Col + 1}");
            if (doc.HasSelection)
                sb.Append($" ({doc.SelectedText.Length:N0} selected)");
            sb.Append(_content.IsLatin1 ? " · Latin-1" : _content.HasBom ? " · UTF-8 BOM" : " · UTF-8");
            sb.Append(_content.Newline == "\r\n" ? " · CRLF" : " · LF");
            if (_root is not null)
                sb.Append(" · as root");
            if (_partial)
                sb.Append($" · its last {FileFormat.Size(_readTo - _shownFrom)} of {FileFormat.Size(_totalSize)}");
            if (doc.ReadOnly)
                sb.Append(_following ? " · following" : " · read only");
        }
        SetStatus(_session is null ? TabStatus.None : TabStatus.Connected, sb.ToString());
    }

    private void ShowBusyStatus(string text) =>
        SetStatus(_session is null ? TabStatus.None : TabStatus.Connected, $"{_place}: {FileFormat.Printable(_path)} · {text}");

    private void ShowMessage(string text, string? action = null, Action? onAction = null, string? other = null, Action? onOther = null)
    {
        _editor.Visible = false;
        _message.Visible = true;
        _message.Text = text;
        _messageAction.Text = action ?? "";
        _messageAction.Visible = action is not null;
        _messageActionHandler = onAction;
        _messageOther.Text = other ?? "";
        _messageOther.Visible = other is not null;
        _messageOtherHandler = onOther;
        InvalidateLayout();
    }

    private void HideMessage()
    {
        _message.Visible = _messageAction.Visible = _messageOther.Visible = false;
        _editor.Visible = true;
        UpdateTitle();
        InvalidateLayout();
    }

    /// <summary>A strip with the surface color (the toolbar, the find bar).</summary>
    private sealed class Bar : Control
    {
        private readonly bool _bottomBorder;

        public Bar(bool bottomBorder)
        {
            _bottomBorder = bottomBorder;
            Style = new ElementStyle { BackColor = Theme.Surface };
        }

        protected override void Paint(SkiaSharp.SKCanvas c)
        {
            if (_bottomBorder)
                Gfx.Line(c, 0, H - 0.5f, W, H - 0.5f, Theme.Border);
        }
    }
}
