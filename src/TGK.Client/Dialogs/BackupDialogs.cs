using System;
using System.IO;
using System.Threading.Tasks;
using Blossom;
using Blossom.Core.Visual;
using TGK.Client.Controls;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Services;

namespace TGK.Client.Dialogs;

/// <summary>Writes the vault (either mode) to an encrypted backup file protected by a backup password.</summary>
public sealed class BackupExportDialog : FormDialog
{
    private readonly Label _note;
    private readonly NewPasswordRows _password;
    private readonly Button _export;

    public BackupExportDialog(TgkView view) : base(view, "Export backup", 480)
    {
        Subtitle = "An encrypted file with your hosts, keys and settings.";
        _note = AddBody(new Label("Importing it needs the backup password. It can't be recovered if forgotten, so keep it safe.",
            Theme.FontSm, Theme.TextSecondary) { MaxLines = 3 });
        _password = AddNewPassword("Backup password", "Confirm backup password");
        AddFormButton("Cancel", ButtonVariant.Secondary, Cancel);
        _export = AddFormButton("Export…", ButtonVariant.Primary, Accept);
    }

    protected override VisualElement? InitialFocus => _password.Password;

    protected override float LayoutBody(float left, float top, float width)
    {
        float h = _note.MeasureHeight(width);
        _note.Transform.SetLocalFrame(left, top, width, h);
        float y = _password.Place(left, top + h + 14, width);
        return PlaceError(left, y, width) - top;
    }

    protected override async void Accept()
    {
        if (IsBusy || !CheckNewPassword(_password))
            return;
        SetBusy(true);
        _export.Text = "Choose a file…";
        string? path = await BackupFile.ChooseAsync(View, save: true);
        if (path is null || IsClosed)
        {
            SetBusy(false);
            _export.Text = "Export…";
            return;
        }
        _export.Text = "Encrypting…";
        try
        {
            await View.Services.Migration.ExportBackupAsync(path, _password.Password.Text);
            SetBusy(false);
            Close();
            View.ShowToast($"Backup saved to {path}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is VaultException or InvalidOperationException)
        {
            SetBusy(false);
            _export.Text = "Export…";
            SetError(ex.Message, ex is VaultException { Code: VaultError.ValidationFailed } ? _password.Password : null);
        }
    }
}

/// <summary>Merges an encrypted backup file into the current vault (either mode).</summary>
public sealed class BackupImportDialog : FormDialog
{
    private readonly Label _fileCaption, _passwordCaption, _note;
    private readonly TextField _file, _password;
    private readonly Button _browse, _import;

    public BackupImportDialog(TgkView view) : base(view, "Import backup", 520)
    {
        Subtitle = "Adds the hosts, keys and settings of a TGK backup file to your vault.";
        _fileCaption = AddBody(Form.Caption("Backup file"));
        _file = AddField(new TextField($"Path of a {VaultMigration.BackupExtension} file") { Mono = true });
        _browse = AddField(new Button("Browse…", ButtonVariant.Secondary, "folder"));
        _browse.Clicked += Browse;
        _passwordCaption = AddBody(Form.Caption("Backup password"));
        _password = AddField(new TextField { IsPassword = true });
        _note = AddBody(new Label("Only what you don't have yet is added: entries already in your vault, your connection defaults and host keys are kept as they are.",
            Theme.FontSm, Theme.TextMuted) { MaxLines = 3 });
        AddFormButton("Cancel", ButtonVariant.Secondary, Cancel);
        _import = AddFormButton("Import", ButtonVariant.Primary, Accept);
    }

    protected override VisualElement? InitialFocus => _file;

    protected override float LayoutBody(float left, float top, float width)
    {
        float browseW = Math.Max(96, _browse.PreferredWidth);
        float y = Form.Place(_fileCaption, _file, left, top, width - browseW - 8);
        _browse.Transform.SetLocalFrame(left + width - browseW, y - Theme.FieldHeight, browseW, Theme.FieldHeight);
        y = Form.Place(_passwordCaption, _password, left, y + Form.RowGap, width);
        float h = _note.MeasureHeight(width);
        _note.Transform.SetLocalFrame(left, y + 12, width, h);
        return PlaceError(left, y + 12 + h, width) - top;
    }

    private async void Browse()
    {
        if (IsBusy)
            return;
        SetBusy(true);
        string? path = await BackupFile.ChooseAsync(View, save: false);
        SetBusy(false);
        if (path is null || IsClosed)
            return;
        _file.Text = path;
        _password.Focus();
    }

    protected override async void Accept()
    {
        if (IsBusy)
            return;
        string path = _file.Text.Trim();
        if (path.Length == 0)
        {
            SetError("Choose the backup file.", _file);
            return;
        }
        if (_password.Text.Length == 0)
        {
            SetError("Enter the backup password.", _password);
            return;
        }
        SetBusy(true);
        _import.Text = "Importing…";
        try
        {
            MergeCounts counts = await View.Services.Migration.ImportBackupAsync(path, _password.Text);
            SetBusy(false);
            Close();
            string conflicts = counts.Conflicts == 0 ? "" : $", {counts.Conflicts} kept as they are (the backup has another version)";
            View.ShowToast($"Backup imported: {counts.Added} added, {counts.Skipped} already there{conflicts}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is VaultException or InvalidOperationException)
        {
            SetBusy(false);
            _import.Text = "Import";
            SetError(ex.Message, ex is VaultException { Code: VaultError.InvalidCredentials } ? _password : _file);
        }
    }
}

/// <summary>Picks a backup file with the native dialog, or asks for its path when there is none.</summary>
internal static class BackupFile
{
    private static readonly FileFilter Filter = new("TGK backup", "*" + VaultMigration.BackupExtension);

    /// <summary>
    /// The chosen path, or null when cancelled. UI thread. To save: with the backup extension added when it has none,
    /// and confirmed before replacing an existing file (unless the native dialog already asked for that exact path).
    /// </summary>
    public static async Task<string?> ChooseAsync(TgkView view, bool save)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultName = $"tgk-backup-{DateTime.Now:yyyy-MM-dd}{VaultMigration.BackupExtension}";
        string title = save ? "Export backup" : "Import backup";
        string? path = null;
        bool picked = false;
        if (FilePicker.IsAvailable)
        {
            try
            {
                path = save
                    ? await FilePicker.SaveFileAsync(title, defaultName, home, Filter)
                    : await FilePicker.PickFileAsync(title, home, Filter);
                picked = true;
            }
            catch (Exception ex)
            {
                Log.Warning($"File picker failed: {ex.Message}");
            }
        }
        if (!picked)
        {
            path = await new PromptDialog(view, title, null, "No file picker is available on this system. Enter the path of the backup file.",
                "File path", save ? "Save" : "Open", initialText: save ? Path.Combine(home, defaultName) : "").ShowAsync();
        }
        if (!save || path is null)
            return path;

        bool askedToReplace = picked && Path.GetExtension(path).Length > 0;
        if (Path.GetExtension(path).Length == 0)
            path += VaultMigration.BackupExtension;
        if (!askedToReplace && File.Exists(path)
            && !await ConfirmDialog.ShowAsync(view, "Replace the existing file?",
                $"“{Path.GetFileName(path)}” already exists. Replacing it loses the backup in it.", "Replace", danger: true))
            return null;
        return path;
    }
}
