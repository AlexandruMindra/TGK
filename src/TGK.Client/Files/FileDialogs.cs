using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TGK.Client.Controls;
using TGK.Client.Dialogs;
using TGK.Client.Views;
using TGK.Core.Files;

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

    private PermissionsDialog(TgkView view, IReadOnlyList<FileEntry> entries)
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
    public static Task<int?> ShowAsync(TgkView view, IReadOnlyList<FileEntry> entries)
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

/// <summary>How files go from one host to another.</summary>
public enum TransferRoute
{
    /// <summary>Each file comes down to this computer (a private buffer) and goes up again: the hosts never meet.</summary>
    ThroughThisComputer,

    /// <summary>The source host sends them straight to the other host (with that host's credentials).</summary>
    Direct,
}

/// <summary>What the user chose in a <see cref="TransferDialog"/>.</summary>
public sealed record TransferChoice(TransferRoute Route, string DirectHost, int DirectPort);

/// <summary>
/// Confirms a copy or move between two file panes ("Copy 3 items from web-01:/var/www to this computer:~/site"). Between
/// two hosts it also offers the route: through this computer (default), or directly from one host to the other, where
/// the address the source host uses to reach the other one can be changed and what that means for the credentials is
/// spelled out. Completes with the choice, or null when cancelled.
/// </summary>
public sealed class TransferDialog : DialogBase
{
    private readonly Label _message;
    private readonly SegmentedControl? _route;
    private readonly Label? _explain;
    private readonly Label? _addressCaption;
    private readonly TextField? _address;
    private readonly Label _error;
    private readonly string _sourceName, _targetName;
    private readonly int _defaultPort;
    private readonly TaskCompletionSource<TransferChoice?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TransferDialog(TgkView view, bool move, IReadOnlyList<FileEntry> entries, string from, string to, string sourceName, string targetName,
        bool offerDirect, string defaultHost, int defaultPort, string? jumpNote)
        : base(view, move ? "Move" : "Copy", 500)
    {
        _sourceName = sourceName;
        _targetName = targetName;
        _defaultPort = defaultPort;
        string what = FileFormat.Describe(entries);
        _message = AddBody(new Label($"{(move ? "Move" : "Copy")} {what}\nfrom {FileFormat.Printable(from)}\nto {FileFormat.Printable(to)}"
            + (move ? "\n\nThe originals are removed once everything was copied." : ""), Theme.FontBase, Theme.TextSecondary) { MaxLines = 8 });
        if (offerDirect)
        {
            _route = AddBody(new SegmentedControl("Through this computer", "Directly between the hosts"));
            _route.SelectionChanged += _ => UpdateRoute();
            _explain = AddBody(new Label("", Theme.FontSm, Theme.TextSecondary) { MaxLines = 9 });
            _addressCaption = AddBody(Form.Caption($"Address {sourceName} uses to reach {targetName}"));
            _address = AddBody(new TextField("host or host:port") { Text = defaultPort == 22 ? defaultHost : $"{defaultHost}:{defaultPort}", Mono = true });
            _jumpNote = jumpNote;
        }
        _error = AddBody(new Label("", Theme.FontSm, Theme.Danger) { MaxLines = 3, Visible = false });
        AddButton("Cancel", ButtonVariant.Secondary, Cancel);
        AddButton(move ? "Move" : "Copy", ButtonVariant.Primary, Accept);
        UpdateRoute();
    }

    private readonly string? _jumpNote;

    /// <summary>On the UI thread: completes with the choice, or null when cancelled.</summary>
    /// <param name="offerDirect">Between two hosts: offer the direct route.</param>
    /// <param name="jumpNote">Why the default address may not work from the source host (the target is behind jump hosts), if so.</param>
    public static Task<TransferChoice?> ShowAsync(TgkView view, bool move, IReadOnlyList<FileEntry> entries, string from, string to,
        string sourceName, string targetName, bool offerDirect = false, string defaultHost = "", int defaultPort = 22, string? jumpNote = null)
    {
        var dialog = new TransferDialog(view, move, entries, from, to, sourceName, targetName, offerDirect, defaultHost, defaultPort, jumpNote);
        dialog.Open();
        return dialog._result.Task;
    }

    private bool Direct => _route?.SelectedIndex == 1;

    private void UpdateRoute()
    {
        if (_route is null)
            return;
        _explain!.Text = Direct
            ? $"{_sourceName} connects to {_targetName} itself (it needs ssh and tar; {_targetName} needs tar). For that, "
              + $"{_targetName}'s key or password is placed on {_sourceName}, in a folder only your account there (and its "
              + $"administrators) can read, and removed when the copy ends. {_sourceName} accepts only the host key you "
              + $"trusted for {_targetName}. Use this only if you trust {_sourceName} with {_targetName}'s credentials."
              + (_jumpNote is null ? "" : $"\n{_jumpNote}")
            : $"Each file comes down to this computer, into a private folder, and goes up to {_targetName}: the hosts never "
              + "need to reach each other and no credentials leave this computer. Fast enough for most copies.";
        _explain.Color = Direct ? Theme.Warning : Theme.TextSecondary;
        _addressCaption!.Visible = _address!.Visible = Direct;
        _error.Visible = false;
        InvalidateLayout();
    }

    protected override float LayoutBody(float left, float top, float width)
    {
        float y = top;
        float mh = _message.MeasureHeight(width);
        _message.Transform.SetLocalFrame(left, y, width, mh);
        y += mh + 14;
        if (_route is not null)
        {
            _route.Transform.SetLocalFrame(left, y, width, Theme.ControlHeight);
            y += Theme.ControlHeight + 10;
            float eh = _explain!.MeasureHeight(width);
            _explain.Transform.SetLocalFrame(left, y, width, eh);
            y += eh + 10;
            if (_address!.Visible)
                y = Form.Place(_addressCaption!, _address, left, y, width);
        }
        if (_error.Visible)
        {
            float eh = _error.MeasureHeight(width);
            _error.Transform.SetLocalFrame(left, y + 6, width, eh);
            y += 6 + eh;
        }
        return y - top;
    }

    protected override void Accept()
    {
        if (!Direct)
        {
            _result.TrySetResult(new TransferChoice(TransferRoute.ThroughThisComputer, "", 0));
            Close();
            return;
        }
        string text = _address!.Text.Trim();
        string host = text;
        int port = _defaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int p))
        {
            host = text[..colon];
            port = p;
        }
        if (host.Length == 0 || host.AsSpan().IndexOfAny(" \t'\"\\") >= 0 || port is < 1 or > 65535)
        {
            _error.Text = "Enter the address as host or host:port.";
            _error.Visible = true;
            _address.HasError = true;
            InvalidateLayout();
            _address.Focus();
            return;
        }
        _result.TrySetResult(new TransferChoice(TransferRoute.Direct, host, port));
        Close();
    }

    protected override void OnClosed() => _result.TrySetResult(null);
}

