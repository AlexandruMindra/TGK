using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core.Visual;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Core.Ssh;

namespace TGK.Client.Dialogs;

/// <summary>"Keys &amp; identities": the list of saved identities on the left, an editor for the selected one on the right.</summary>
public sealed class IdentitiesDialog : DialogBase
{
    private const float ListW = 220, BodyH = 392, Gap = 16, KeyBoxH = 58;
    private const int MaxKeyFileBytes = 64 * 1024;

    private readonly IVaultService _vault;
    private readonly IdentityList _list;
    private readonly Button _new;
    private readonly Label _nameCaption, _userCaption, _authCaption, _passwordCaption, _keyCaption, _passphraseCaption;
    private readonly TextField _name, _user, _password, _passphrase;
    private readonly SegmentedControl _auth;
    private readonly KeyStatusBox _keyBox;
    private readonly Button _paste, _import;
    private readonly Label _error;
    private readonly Button _delete, _save;
    private Identity _draft = new();
    private bool _isNew;
    private int _inspectGeneration;

    public IdentitiesDialog(TgkView view) : base(view, "Keys & identities", 780)
    {
        _vault = view.Services.Vault;
        Subtitle = "Credentials that hosts can use. They sync to all your devices.";
        _list = AddBody(new IdentityList());
        _list.Picked += Select;
        _new = AddBody(new Button("New identity", ButtonVariant.Secondary, "plus"));
        _new.Clicked += StartNew;

        _nameCaption = AddBody(Form.Caption("Name"));
        _name = AddBody(new TextField("e.g. Deploy key"));
        _name.Changed += _ =>
        {
            _name.HasError = false;
            if (_isNew)
                RefreshList();
        };
        _userCaption = AddBody(Form.Caption("Username"));
        _user = AddBody(new TextField("optional"));
        _authCaption = AddBody(Form.Caption("Authentication"));
        _auth = AddBody(new SegmentedControl("Password", "Private key"));
        _auth.SelectionChanged += _ => UpdateAuthVisibility();
        _passwordCaption = AddBody(Form.Caption("Password"));
        _password = AddBody(new TextField("Leave empty to be asked when connecting") { IsPassword = true });
        _keyCaption = AddBody(Form.Caption("Private key"));
        _keyBox = AddBody(new KeyStatusBox());
        _paste = AddBody(new Button("Paste from clipboard", ButtonVariant.Secondary, "clipboard"));
        _paste.Clicked += PasteKey;
        _import = AddBody(new Button("Import file…", ButtonVariant.Secondary, "file"));
        _import.Clicked += ImportKey;
        _passphraseCaption = AddBody(Form.Caption("Passphrase"));
        _passphrase = AddBody(new TextField("Only for encrypted keys") { IsPassword = true });
        _passphrase.Changed += _ =>
        {
            _draft.KeyType = _draft.Fingerprint = null; // must be re-verified with the new passphrase
            InspectKey(delayMs: 350);
        };
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 2, Visible = false });

        _delete = AddLeftButton("Delete", ButtonVariant.Ghost, Delete);
        _delete.Icon = "trash";
        AddButton("Close", ButtonVariant.Secondary, Cancel);
        _save = AddButton("Save", ButtonVariant.Primary, Accept);

        List<Identity> identities = Sorted();
        if (identities.Count > 0)
            Select(identities[0]);
        else
            StartNew();
    }

    protected override VisualElement? InitialFocus => _name;

    private List<Identity> Sorted() => _vault.Current.Identities.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private void RefreshList() => _list.SetItems(Sorted(), _isNew ? null : _draft.Id, _isNew ? _name.Text.Trim() : null);

    private void Select(Identity identity)
    {
        _isNew = false;
        Load(identity.Clone());
    }

    private void StartNew()
    {
        _isNew = true;
        Load(new Identity { Name = "New identity", AuthKind = AuthKind.Password });
        _name.Focus();
        _name.SelectAll();
    }

    private void Load(Identity identity)
    {
        _draft = identity;
        _name.Text = identity.Name;
        _user.Text = identity.Username ?? "";
        _auth.SelectedIndex = identity.AuthKind == AuthKind.PrivateKey ? 1 : 0;
        _password.Text = identity.Password ?? "";
        _password.Revealed = false;
        _passphrase.Text = identity.Passphrase ?? "";
        _passphrase.Revealed = false;
        _delete.Visible = !_isNew;
        SetError(null);
        UpdateAuthVisibility();
        ShowKeyStatus();
        RefreshList();
    }

    private void UpdateAuthVisibility()
    {
        bool key = _auth.SelectedIndex == 1;
        _passwordCaption.Visible = _password.Visible = !key;
        _keyCaption.Visible = _keyBox.Visible = _paste.Visible = _import.Visible = key;
        _passphraseCaption.Visible = _passphrase.Visible = key;
        InvalidateLayout();
    }

    private void SetError(string? message)
    {
        _error.Text = message ?? "";
        _error.Visible = message is not null;
        InvalidateLayout();
    }

    // ---- private key ----

    private void ShowKeyStatus()
    {
        if (string.IsNullOrWhiteSpace(_draft.PrivateKey))
            _keyBox.Set("No key loaded", "Paste an OpenSSH, PEM or PuTTY private key, or import it from a file.", KeyState.Empty);
        else if (_draft.KeyType is not null && _draft.Fingerprint is not null)
            _keyBox.Set($"{ShortType(_draft.KeyType)} key", _draft.Fingerprint, KeyState.Ok);
        else
            InspectKey();
    }

    private static string ShortType(string keyType) => keyType.StartsWith("ssh-", StringComparison.Ordinal) ? keyType[4..] : keyType;

    private void SetKey(string text)
    {
        _draft.PrivateKey = text;
        _draft.KeyType = _draft.Fingerprint = null;
        SetError(null);
        InspectKey();
    }

    /// <summary>Parses the key off the UI thread (encrypted keys run a slow KDF) and shows type and fingerprint.</summary>
    private async void InspectKey(int delayMs = 0)
    {
        string? key = _draft.PrivateKey;
        if (string.IsNullOrWhiteSpace(key))
            return;
        int generation = ++_inspectGeneration;
        if (delayMs > 0)
        {
            await Task.Delay(delayMs); // debounce typing in the passphrase field
            if (generation != _inspectGeneration || IsClosed)
                return;
        }
        string passphrase = _passphrase.Text;
        _keyBox.Set("Checking key…", "", KeyState.Busy);
        (bool ok, string type, string fingerprint, string? error, bool needsPassphrase) = await Task.Run(() =>
        {
            bool needs = KeyInspector.NeedsPassphrase(key);
            if (needs && passphrase.Length == 0)
                return (false, "", "", (string?)null, true);
            bool success = KeyInspector.TryInspect(key, passphrase, out string t, out string f, out string? e);
            return (success, t, f, e, needs);
        });
        if (generation != _inspectGeneration || IsClosed)
            return;
        if (ok)
        {
            _draft.KeyType = type;
            _draft.Fingerprint = fingerprint;
            _keyBox.Set(needsPassphrase ? $"{ShortType(type)} key · passphrase OK" : $"{ShortType(type)} key", fingerprint, KeyState.Ok);
        }
        else if (needsPassphrase && passphrase.Length == 0)
        {
            _keyBox.Set("Encrypted key", "Enter its passphrase below.", KeyState.Warning);
        }
        else
        {
            _keyBox.Set("Key not usable", error ?? "The private key could not be read.", KeyState.Error);
        }
    }

    private void PasteKey()
    {
        string text = Browser.GetClipboardText();
        if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal) && !text.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            SetError("The clipboard does not contain a private key.");
            return;
        }
        SetKey(text);
    }

    private async void ImportKey()
    {
        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        string? path;
        if (FilePicker.IsAvailable)
        {
            try
            {
                path = await FilePicker.PickFileAsync("Import private key", sshDir);
            }
            catch (Exception ex)
            {
                Log.Warning($"File picker failed: {ex.Message}");
                path = await AskForPath(sshDir);
            }
        }
        else
        {
            path = await AskForPath(sshDir);
        }
        if (path is null || IsClosed)
            return;

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxKeyFileBytes)
            {
                SetError("That file is too large to be a private key.");
                return;
            }
            SetKey(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetError($"Could not read the file: {ex.Message}");
        }
    }

    // Fallback when no native picker exists: type the path.
    private Task<string?> AskForPath(string sshDir) =>
        new PromptDialog(View, "Import private key", null, "No file picker is available on this system. Enter the path of the key file.",
            "File path", "Import", initialText: Path.Combine(sshDir, "id_ed25519")).ShowAsync();

    // ---- save / delete ----

    protected override async void Accept()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            _name.HasError = true;
            _name.Focus();
            SetError("Give the identity a name.");
            return;
        }
        bool key = _auth.SelectedIndex == 1;
        if (key && string.IsNullOrWhiteSpace(_draft.PrivateKey))
        {
            SetError("Load a private key first (paste it or import a file).");
            return;
        }

        Identity identity = _draft.Clone();
        identity.Name = name;
        identity.Username = _user.Text.Trim() is { Length: > 0 } user ? user : null;
        identity.AuthKind = key ? AuthKind.PrivateKey : AuthKind.Password;
        if (key)
        {
            identity.Password = null;
            identity.Passphrase = _passphrase.Text.Length > 0 ? _passphrase.Text : null;
            if (identity.KeyType is null || identity.Fingerprint is null)
            {
                _save.Enabled = false;
                string privateKey = identity.PrivateKey!, passphrase = _passphrase.Text;
                (bool ok, string type, string fp, string? error) = await Task.Run(() =>
                    (KeyInspector.TryInspect(privateKey, passphrase, out string t, out string f, out string? e), t, f, e));
                _save.Enabled = true;
                if (IsClosed)
                    return;
                if (!ok)
                {
                    SetError(error ?? "The private key could not be read.");
                    return;
                }
                identity.KeyType = type;
                identity.Fingerprint = fp;
            }
        }
        else
        {
            identity.Password = _password.Text.Length > 0 ? _password.Text : null;
            identity.PrivateKey = identity.Passphrase = identity.KeyType = identity.Fingerprint = null;
        }

        if (!View.RunVault(() => _vault.SaveIdentityAsync(identity)))
            return;
        View.ShowToast($"Saved “{identity.Name}”", ToastKind.Success);
        _isNew = false;
        Load(identity);
    }

    private async void Delete()
    {
        Identity target = _draft;
        int users = _vault.Current.Hosts.Count(h => h.IdentityId == target.Id);
        string message = users == 0
            ? $"“{target.Name}” will be removed from all your devices."
            : $"“{target.Name}” will be removed from all your devices. {users} host{(users == 1 ? "" : "s")} using it will ask for a password instead.";
        if (!await ConfirmDialog.ShowAsync(View, "Delete identity?", message, "Delete", danger: true) || IsClosed)
            return;
        if (!View.RunVault(() => _vault.DeleteIdentityAsync(target.Id)))
            return;
        List<Identity> rest = Sorted().Where(i => i.Id != target.Id).ToList();
        if (rest.Count > 0)
            Select(rest[0]);
        else
            StartNew();
    }

    // ---- layout ----

    protected override float LayoutBody(float left, float top, float width)
    {
        float y0 = top;
        _list.Transform.SetLocalFrame(left, y0, ListW, BodyH - 44);
        _new.Transform.SetLocalFrame(left, y0 + BodyH - 34, ListW, 34);

        float x = left + ListW + 24, w = width - ListW - 24;
        float col = (w - Gap) / 2f;
        Form.Place(_nameCaption, _name, x, y0, col);
        float y = Form.Place(_userCaption, _user, x + col + Gap, y0, col) + Form.RowGap;
        y = Form.Place(_authCaption, _auth, x, y, Math.Min(w, 300)) + Form.RowGap;
        if (_auth.SelectedIndex == 0)
        {
            y = Form.Place(_passwordCaption, _password, x, y, w) + Form.RowGap;
        }
        else
        {
            y = Form.Place(_keyCaption, _keyBox, x, y, w, KeyBoxH) + 10;
            float pw = _paste.PreferredWidth;
            _paste.Transform.SetLocalFrame(x, y, pw, 32);
            _import.Transform.SetLocalFrame(x + pw + 8, y, _import.PreferredWidth, 32);
            y += 32 + Form.RowGap;
            y = Form.Place(_passphraseCaption, _passphrase, x, y, w) + Form.RowGap;
        }
        if (_error.Visible)
            _error.Transform.SetLocalFrame(x, y - 4, w, _error.MeasureHeight(w));
        _list.RevealSelected(); // it has its real height now
        return BodyH;
    }

    private enum KeyState { Empty, Busy, Ok, Warning, Error }

    /// <summary>Two-line key summary: "ed25519 · SHA256:…" plus a hint, tinted by state.</summary>
    private sealed class KeyStatusBox : Control
    {
        private string _title = "", _detail = "";
        private KeyState _state;

        public void Set(string title, string detail, KeyState state)
        {
            _title = title;
            _detail = detail;
            _state = state;
            InvalidatePaint();
        }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            SKColor tint = _state switch
            {
                KeyState.Ok => Theme.Success,
                KeyState.Warning => Theme.Warning,
                KeyState.Error => Theme.Danger,
                _ => Theme.TextMuted,
            };
            Gfx.FillRound(c, r, Theme.Radius, Theme.Input);
            Gfx.StrokeRound(c, r, Theme.Radius, _state is KeyState.Empty or KeyState.Busy ? Theme.BorderInput : tint.WithAlpha(110));
            Gfx.FillRound(c, SKRect.Create(10, H / 2f - 17, 34, 34), Theme.Radius, tint.WithAlpha(32));
            Icons.Draw(c, "key", 27, H / 2f, 18, tint);
            Gfx.Text(c, _title, 56, _detail.Length > 0 ? H / 2f - 9 : H / 2f, Theme.FontBase, Theme.WeightSemibold,
                _state == KeyState.Error ? Theme.Danger : Theme.TextPrimary, TextAlignment.Left, W - 66);
            // A loaded key shows its fingerprint in the monospace font, like ssh-keygen -l.
            SKPaint detailFont = _state == KeyState.Ok ? Gfx.Font(Theme.FontSm, Theme.Mono) : Gfx.Font(Theme.FontSm);
            if (_detail.Length > 0)
                Gfx.Text(c, _detail, 56, H / 2f + 10, detailFont, Theme.TextSecondary, TextAlignment.Left, W - 66);
        }
    }

    /// <summary>Selectable, scrollable list of identities (plus an unsaved "new" entry while one is being created).</summary>
    private sealed class IdentityList : Control
    {
        private const float RowH = 48, Inset = 4;
        private List<Identity> _items = [];
        private Guid? _selected;
        private string? _newName;
        private int _hover = -1;
        private float _scroll, _pointerY = -1;

        public IdentityList()
        {
            IsClipping = true;
            Events.OnMouseMove += (_, e) =>
            {
                _pointerY = e.Relative.Y;
                SetAndPaint(ref _hover, IndexAt(_pointerY));
            };
            Events.OnClick += (_, e) =>
            {
                e.Handled = true;
                int i = IndexAt(e.Relative.Y);
                if (i >= 0 && i < _items.Count)
                    Picked?.Invoke(_items[i]);
            };
            Events.OnScroll += (_, e) =>
            {
                e.Handled = true;
                SetAndPaint(ref _scroll, Math.Clamp(_scroll - e.Offset.Y * RowH, 0, MaxScroll));
                SetAndPaint(ref _hover, IndexAt(_pointerY));
            };
        }

        public event Action<Identity>? Picked;

        private int Count => _items.Count + (_newName is null ? 0 : 1);

        private float MaxScroll => Math.Max(0, 2 * Inset + Count * RowH - H);

        public void SetItems(List<Identity> items, Guid? selected, string? newName)
        {
            _items = items;
            _selected = selected;
            _newName = newName;
            RevealSelected();
            InvalidatePaint();
        }

        /// <summary>Scrolls the selected (or new) row into view.</summary>
        public void RevealSelected()
        {
            if (H <= 0)
                return;
            int index = _newName is not null ? _items.Count : _items.FindIndex(i => i.Id == _selected);
            float scroll = Math.Clamp(_scroll, 0, MaxScroll);
            if (index >= 0)
            {
                float top = index * RowH, bottom = top + RowH + 2 * Inset;
                if (top < scroll)
                    scroll = top;
                else if (bottom > scroll + H)
                    scroll = bottom - H;
            }
            SetAndPaint(ref _scroll, scroll);
        }

        protected override void OnHoverChanged()
        {
            if (!IsHovered)
            {
                _hover = -1;
                _pointerY = -1;
            }
        }

        private int IndexAt(float y)
        {
            if (y < 0 || y >= H)
                return -1;
            float contentY = y + _scroll - Inset;
            int i = (int)(contentY / RowH);
            return contentY >= 0 && i < Count ? i : -1;
        }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.Radius, Theme.Input);
            Gfx.StrokeRound(c, r, Theme.Radius, Theme.Border);
            int count = Count;
            bool scrollable = MaxScroll > 0;
            float right = W - (scrollable ? 12 : 4);
            c.Save();
            c.ClipRect(new SKRect(1, 1, W - 1, H - 1));
            for (int i = 0; i < count; i++)
            {
                var row = new SKRect(4, Inset + i * RowH - _scroll, right, Inset + (i + 1) * RowH - _scroll);
                if (row.Bottom < 0 || row.Top > H)
                    continue;
                bool isNew = i == _items.Count;
                Identity? item = isNew ? null : _items[i];
                bool selected = isNew || item!.Id == _selected;
                if (selected)
                    Gfx.FillRound(c, row, Theme.RadiusSm, Theme.Accent.WithAlpha(50));
                else if (i == _hover)
                    Gfx.FillRound(c, row, Theme.RadiusSm, Theme.SurfaceRaised);
                string icon = item?.AuthKind == AuthKind.PrivateKey ? "key" : "lock";
                Icons.Draw(c, isNew ? "plus" : icon, row.Left + 18, row.MidY, 16, selected ? Theme.Accent : Theme.TextMuted);
                string name = isNew ? (_newName is { Length: > 0 } n ? n : "New identity") : item!.Name;
                string detail = isNew ? "Not saved yet"
                    : item!.AuthKind == AuthKind.PrivateKey ? (item.KeyType is { } kt ? ShortType(kt) + " key" : "Private key")
                    : string.IsNullOrEmpty(item.Password) ? "Password (ask)" : "Password";
                if (item?.Username is { Length: > 0 } user)
                    detail = $"{user} · {detail}";
                Gfx.Text(c, name, row.Left + 36, row.MidY - 8, Theme.FontBase, Theme.WeightRegular, Theme.TextPrimary, TextAlignment.Left, row.Width - 44);
                Gfx.Text(c, detail, row.Left + 36, row.MidY + 9, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, row.Width - 44);
            }
            c.Restore();

            if (scrollable)
            {
                float track = H - 2 * Inset;
                float thumb = Math.Max(24, track * H / (H + MaxScroll));
                float y = Inset + (track - thumb) * (_scroll / MaxScroll);
                Gfx.FillRound(c, SKRect.Create(W - 8, y, 4, thumb), 2, Theme.TextMuted.WithAlpha(110));
            }
            if (count == 0)
                Gfx.Text(c, "No identities yet", W / 2f, 30, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center);
        }
    }
}
