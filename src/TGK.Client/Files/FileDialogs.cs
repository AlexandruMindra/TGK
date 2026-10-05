using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Views;
using TGK.Core.Sftp;

namespace TGK.Client.Files;

/// <summary>Asks what to do with items that already exist where a transfer copies to: replace, skip or cancel.</summary>
public sealed class ConflictDialog : DialogBase
{
    private readonly Label _message;
    private readonly TaskCompletionSource<ConflictChoice> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ConflictDialog(TgkView view, IReadOnlyList<string> names, string destination)
        : base(view, names.Count == 1 ? "Replace existing item?" : $"Replace {names.Count} existing items?", 480)
    {
        string list = string.Join("\n", names.Take(6).Select(n => "• " + FileFormat.Printable(n)))
            + (names.Count > 6 ? $"\n… and {names.Count - 6} more" : "");
        string what = names.Count == 1 ? "An item with this name already exists" : "Items with these names already exist";
        _message = AddBody(new Label($"{what} in {FileFormat.Printable(destination)}:\n{list}\n\nFolders are merged: files in them with the same name are replaced.",
            Theme.FontBase, Theme.TextSecondary) { MaxLines = 14 });
        AddLeftButton("Cancel", ButtonVariant.Ghost, Cancel);
        AddButton("Skip", ButtonVariant.Secondary, () => Finish(ConflictChoice.Skip));
        AddButton("Replace", ButtonVariant.Primary, Accept);
    }

    /// <summary>On the UI thread: completes with the choice (Cancel when the dialog is dismissed).</summary>
    public static Task<ConflictChoice> ShowAsync(TgkView view, IReadOnlyList<string> names, string destination)
    {
        var dialog = new ConflictDialog(view, names, destination);
        dialog.Open();
        return dialog._result.Task;
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float h = _message.MeasureHeight(width);
        _message.Transform.SetLocalFrame(left, top, width, h);
        return h;
    }

    protected override void Accept() => Finish(ConflictChoice.Replace);

    private void Finish(ConflictChoice choice)
    {
        _result.TrySetResult(choice);
        Close();
    }

    protected override void OnClosed() => _result.TrySetResult(ConflictChoice.Cancel);
}

/// <summary>
/// Edits the permission bits of one or more entries: read, write and execute for the owner, the group and others,
/// kept in step with the octal value (which also takes setuid, setgid and sticky). Completes with the new mode, or
/// null when cancelled.
/// </summary>
public sealed class PermissionsDialog : DialogBase
{
    private static readonly string[] Who = ["Owner", "Group", "Others"];
    private static readonly string[] What = ["Read", "Write", "Execute"];

    private readonly Label _intro;
    private readonly Label[] _rowLabels = new Label[3];
    private readonly Checkbox[,] _boxes = new Checkbox[3, 3];
    private readonly Label _octalCaption;
    private readonly TextField _octal;
    private readonly Label _error;
    private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _special; // setuid, setgid and sticky bits, kept as they were unless typed in the octal field
    private bool _syncing;

    private PermissionsDialog(TgkView view, IReadOnlyList<SftpEntry> entries)
        : base(view, "Permissions", 420)
    {
        Subtitle = entries.Count == 1 ? FileFormat.Printable(entries[0].Path) : FileFormat.Describe(entries);
        int mode = entries[0].Mode & 0xFFF;
        bool mixed = entries.Any(e => (e.Mode & 0xFFF) != mode);
        _intro = AddBody(new Label(mixed ? "The selected items have different permissions; all of them get the ones below." : "",
            Theme.FontSm, Theme.TextSecondary) { MaxLines = 3, Visible = mixed });
        for (int who = 0; who < 3; who++)
        {
            _rowLabels[who] = AddBody(new Label(Who[who], Theme.FontBase, Theme.TextPrimary));
            for (int what = 0; what < 3; what++)
            {
                var box = AddBody(new Checkbox(What[what]));
                box.CheckedChanged += _ => FromBoxes();
                _boxes[who, what] = box;
            }
        }
        _octalCaption = AddBody(Form.Caption("Octal"));
        _octal = AddBody(new TextField("e.g. 755") { Mono = true, MaxLength = 4, DigitsOnly = true });
        _octal.Changed += _ => FromOctal();
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { Visible = false });
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton("Apply", ButtonVariant.Primary, Accept);
        Set(mode);
    }

    /// <summary>On the UI thread: completes with the chosen mode, or null when cancelled.</summary>
    public static Task<int?> ShowAsync(TgkView view, IReadOnlyList<SftpEntry> entries)
    {
        var dialog = new PermissionsDialog(view, entries);
        dialog.Open();
        return dialog._result.Task;
    }

    private int BoxMode()
    {
        int mode = _special;
        for (int who = 0; who < 3; who++)
        {
            for (int what = 0; what < 3; what++)
            {
                if (_boxes[who, what].Checked)
                    mode |= 1 << ((2 - who) * 3 + (2 - what));
            }
        }
        return mode;
    }

    private void Set(int mode)
    {
        _syncing = true;
        _special = mode & 0xE00;
        for (int who = 0; who < 3; who++)
        {
            for (int what = 0; what < 3; what++)
                _boxes[who, what].Checked = (mode & (1 << ((2 - who) * 3 + (2 - what)))) != 0;
        }
        _octal.Text = Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0');
        _octal.HasError = false;
        _error.Visible = false;
        _syncing = false;
    }

    private void FromBoxes()
    {
        if (_syncing)
            return;
        _syncing = true;
        _octal.Text = Convert.ToString(BoxMode(), 8).PadLeft(3, '0');
        _octal.HasError = false;
        _error.Visible = false;
        _syncing = false;
    }

    private void FromOctal()
    {
        if (_syncing)
            return;
        if (FileFormat.ParseMode(_octal.Text) is { } mode)
        {
            _syncing = true;
            _special = mode & 0xE00;
            for (int who = 0; who < 3; who++)
            {
                for (int what = 0; what < 3; what++)
                    _boxes[who, what].Checked = (mode & (1 << ((2 - who) * 3 + (2 - what)))) != 0;
            }
            _syncing = false;
            _octal.HasError = false;
            _error.Visible = false;
        }
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        if (_intro.Visible)
        {
            float ih = _intro.MeasureHeight(width);
            _intro.Transform.SetLocalFrame(left, y, width, ih);
            y += ih + 12;
        }
        float labelW = 70, colW = (width - labelW) / 3f;
        for (int who = 0; who < 3; who++)
        {
            _rowLabels[who].Transform.SetLocalFrame(left, y, labelW, 24);
            for (int what = 0; what < 3; what++)
                _boxes[who, what].Transform.SetLocalFrame(left + labelW + what * colW, y, colW, 24);
            y += 32;
        }
        y += 6;
        y = Form.Place(_octalCaption, _octal, left, y, 120);
        if (_error.Visible)
        {
            _error.Transform.SetLocalFrame(left, y + 6, width, 18);
            y += 24;
        }
        return y - top;
    }

    protected override void Accept()
    {
        if (FileFormat.ParseMode(_octal.Text) is not { } mode)
        {
            _octal.HasError = true;
            _error.Text = "Enter 3 or 4 octal digits (0–7), e.g. 644 or 755.";
            _error.Visible = true;
            InvalidateLayout();
            _octal.Focus();
            return;
        }
        _result.TrySetResult(mode);
        Close();
    }

    protected override void OnClosed() => _result.TrySetResult(null);
}
