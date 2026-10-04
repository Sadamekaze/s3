using System.Text.Json;
using System.Text.RegularExpressions;
using SuikodenText.Core;

namespace SuikodenText.Translator;

// JSON translation editor for the *_teks.json / etc.json export format.
// Layout: file list on the left, virtualized dialog list on top, source and
// translation editors stacked below. Saves back to the same JSON files.
public sealed class TranslatorGui : Form
{
    private sealed record Row(TranslationEntry Entry, string Section);

    private sealed record FileItem(string Name, string Path, int Total, int Untranslated);

    private readonly ListBox _files = new();
    private readonly ListBox _entries = new();
    private readonly TextBox _search = new();
    private readonly TextBox _sourceBox = new();
    private readonly TextBox _translationBox = new();
    private readonly CheckBox _untranslatedOnly = new();
    private readonly CheckBox _wrap = new() { Text = "Wrap", Checked = true };
    private readonly Button _openFolder = new();
    private readonly Button _save = new();
    private readonly Button _prev = new();
    private readonly Button _next = new();
    private readonly Button _nextUntranslated = new();
    private readonly Label _dialogHeader = new();
    private readonly Label _sourceHeader = new();
    private readonly Label _translationHeader = new();
    private readonly StatusStrip _statusStrip = new();
    private Panel _entryPanel = new();
    private SplitContainer _rightSplit = new();
    private SplitContainer _editorSplit = new();
    private readonly ToolStripStatusLabel _statusFile = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusCounts = new();
    private readonly ToolStripStatusLabel _statusDirty = new() { ForeColor = Color.FromArgb(0xB3, 0x55, 0x0F) };
    private readonly ToolTip _tip = new();

    private readonly List<Row> _allRows = new();
    private readonly List<Row> _view = new();
    private string? _folder;
    private TranslationDocument? _document;
    private string? _documentPath;
    private bool _dirty;
    private bool _suspend;

    private static readonly Regex CtrlRe = new(@"\{CTRL:[0-9A-F-]+\}", RegexOptions.Compiled);
    private static readonly Regex RawRe = new(@"\{RAW:[0-9A-F-]+\}", RegexOptions.Compiled);
    private static readonly Regex PrintfRe = new(
        @"%(?:%|[-+#0]*[0-9]*(?:\.[0-9]+)?[hlL]*[diuoxXfFeEgGaAcspn])", RegexOptions.Compiled);

    public TranslatorGui()
    {
        Text = "Suikoden Text Translator";
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(940, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0xF0, 0xF1, 0xF3);
        KeyPreview = true;

        BuildEntryList();
        BuildEditors(); // rightSplit must be Controls[0] so Fill docks last.
        BuildSidebar();
        BuildToolbar();
        BuildStatusBar();

        KeyDown += OnGlobalKeyDown;
        FormClosing += (_, _) =>
        {
            FlushCurrent();
            if (_dirty) Save(silent: true);
            SaveSettings();
        };

        string? last = LoadSettings();
        string fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".Himetsu", "s3-tr"));
        OpenFolderNow(Directory.Exists(last) ? last! : fallback);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplySplitterDistances();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitterDistances();
    }

    // SplitterDistance set during construction is clamped against the
    // SplitContainer's default 150x100 size; re-apply once real bounds exist.
    private void ApplySplitterDistances()
    {
        try
        {
            int rMax = _rightSplit.Height - _rightSplit.SplitterWidth - _rightSplit.Panel2MinSize;
            if (rMax >= _rightSplit.Panel1MinSize)
                _rightSplit.SplitterDistance = Math.Clamp(280, _rightSplit.Panel1MinSize, rMax);
            int eMax = _editorSplit.Height - _editorSplit.SplitterWidth - _editorSplit.Panel2MinSize;
            if (eMax >= _editorSplit.Panel1MinSize)
                _editorSplit.SplitterDistance = Math.Clamp(230, _editorSplit.Panel1MinSize, eMax);
        }
        catch { /* layout is best-effort */ }
    }

    // ---------------------------------------------------------------- layout

    private void BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.White };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(0xE1, 0xE4, 0xE8));
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        StyleButton(_openFolder, "Open Folder");
        StyleButton(_save, "Save  Ctrl+S");
        StyleButton(_prev, "▲");
        StyleButton(_next, "▼");
        StyleButton(_nextUntranslated, "Next untranslated  F4");
        _prev.Size = _next.Size = new Size(34, 28);
        _tip.SetToolTip(_prev, "Previous entry  (Alt+Up)");
        _tip.SetToolTip(_next, "Next entry  (Alt+Down)");
        _tip.SetToolTip(_nextUntranslated, "Jump to the next untranslated entry in the current view");

        _openFolder.Click += (_, _) => PickFolder();
        _save.Click += (_, _) => Save();
        _prev.Click += (_, _) => MoveSelection(-1);
        _next.Click += (_, _) => MoveSelection(1);
        _nextUntranslated.Click += (_, _) => JumpToNextUntranslated();

        _search.PlaceholderText = "Filter source or translation...   Ctrl+F";
        _search.Font = new Font("Segoe UI", 9F);
        _search.Size = new Size(300, 25);
        _search.TextChanged += (_, _) => RefreshEntries();

        _untranslatedOnly.Text = "Untranslated only";
        _untranslatedOnly.Font = new Font("Segoe UI", 8.5F);
        _untranslatedOnly.AutoSize = true;
        _untranslatedOnly.CheckedChanged += (_, _) => RefreshEntries();

        _wrap.Text = "Wrap";
        _wrap.Font = new Font("Segoe UI", 8.5F);
        _wrap.AutoSize = true;
        _wrap.CheckedChanged += (_, _) =>
        {
            var scroll = _wrap.Checked ? ScrollBars.Vertical : ScrollBars.Both;
            _sourceBox.WordWrap = _translationBox.WordWrap = _wrap.Checked;
            _sourceBox.ScrollBars = _translationBox.ScrollBars = scroll;
        };

        bar.Controls.Add(_openFolder);
        bar.Controls.Add(_save);
        bar.Controls.Add(_prev);
        bar.Controls.Add(_next);
        bar.Controls.Add(_nextUntranslated);
        bar.Controls.Add(_search);
        bar.Controls.Add(_untranslatedOnly);
        bar.Controls.Add(_wrap);

        int x = 8;
        foreach (var b in new[] { _openFolder, _save })
        {
            b.Location = new Point(x, 9);
            x += b.Width + 6;
        }
        x += 8;
        foreach (var b in new[] { _prev, _next, _nextUntranslated })
        {
            b.Location = new Point(x, 9);
            x += b.Width + 6;
        }

        bar.Resize += (_, _) =>
        {
            int right = bar.Width - 12;
            _wrap.Location = new Point(right - _wrap.Width, 13);
            _untranslatedOnly.Location = new Point(_wrap.Left - _untranslatedOnly.Width - 14, 13);
            _search.Location = new Point(_untranslatedOnly.Left - _search.Width - 14, 10);
        };

        Controls.Add(bar);
    }

    private void BuildSidebar()
    {
        _files.Dock = DockStyle.Fill;
        _files.Font = new Font("Segoe UI", 9F);
        _files.IntegralHeight = false;
        _files.BorderStyle = BorderStyle.FixedSingle;
        _files.DrawMode = DrawMode.OwnerDrawFixed;
        _files.ItemHeight = 22;
        _files.DrawItem += DrawFileItem;
        _files.SelectedIndexChanged += (_, _) => LoadSelectedFile();

        var panel = new Panel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(8, 0, 8, 8) };
        var header = MakeHeader("Files");
        panel.Controls.Add(_files);
        panel.Controls.Add(header);
        Controls.Add(panel);
    }

    private void BuildEntryList()
    {
        _entries.Dock = DockStyle.Fill;
        _entries.Font = new Font("Segoe UI", 9F);
        _entries.IntegralHeight = false;
        _entries.BorderStyle = BorderStyle.FixedSingle;
        _entries.DrawMode = DrawMode.OwnerDrawFixed;
        _entries.ItemHeight = 22;
        _entries.DrawItem += DrawEntryItem;
        _entries.SelectedIndexChanged += (_, _) => LoadSelectedEntry();

        _dialogHeader.Text = "Dialogs";
        _dialogHeader.Dock = DockStyle.Top;
        _dialogHeader.Height = 24;
        _dialogHeader.TextAlign = ContentAlignment.MiddleLeft;
        _dialogHeader.Padding = new Padding(4, 0, 0, 0);
        _dialogHeader.Font = new Font("Segoe UI", 8.25F, FontStyle.Bold);
        _dialogHeader.ForeColor = Color.FromArgb(0x66, 0x6A, 0x70);

        _entryPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        _entryPanel.Controls.Add(_entries);
        _entryPanel.Controls.Add(_dialogHeader);
    }

    private void BuildEditors()
    {
        StyleEditor(_sourceBox, readOnly: true);
        StyleEditor(_translationBox, readOnly: false);
        _translationBox.TextChanged += (_, _) => OnTranslationChanged();

        _sourceHeader.Text = "Source";
        _translationHeader.Text = "Translation";
        foreach (var header in new[] { _sourceHeader, _translationHeader })
        {
            header.Dock = DockStyle.Top;
            header.Height = 24;
            header.TextAlign = ContentAlignment.MiddleLeft;
            header.Padding = new Padding(4, 0, 0, 0);
            header.Font = new Font("Segoe UI", 8.25F, FontStyle.Bold);
            header.ForeColor = Color.FromArgb(0x66, 0x6A, 0x70);
        }

        var sourcePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        sourcePanel.Controls.Add(_sourceBox);
        sourcePanel.Controls.Add(_sourceHeader);

        var translationPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 8) };
        translationPanel.Controls.Add(_translationBox);
        translationPanel.Controls.Add(_translationHeader);

        _editorSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            FixedPanel = FixedPanel.Panel1,
            BackColor = BackColor,
            Panel1MinSize = 80,
            Panel2MinSize = 100
        };
        _editorSplit.Panel1.Controls.Add(sourcePanel);
        _editorSplit.Panel2.Controls.Add(translationPanel);

        _rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            FixedPanel = FixedPanel.Panel1,
            BackColor = BackColor,
            Panel1MinSize = 100,
            Panel2MinSize = 140
        };
        _rightSplit.Panel1.Controls.Add(_entryPanel);
        _rightSplit.Panel2.Controls.Add(_editorSplit);

        Controls.Add(_rightSplit);
    }

    private void BuildStatusBar()
    {
        _statusStrip.BackColor = Color.White;
        _statusStrip.SizingGrip = false;
        _statusStrip.Items.Add(_statusFile);
        _statusStrip.Items.Add(_statusCounts);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Text = "   F4 next untranslated · Alt+↓/↑ entry · Ctrl+F search · Ctrl+S save",
            ForeColor = Color.FromArgb(0x8A, 0x8F, 0x98) });
        _statusStrip.Items.Add(_statusDirty);
        Controls.Add(_statusStrip);
    }

    private static Label MakeHeader(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 0, 0),
        Font = new Font("Segoe UI", 8.25F, FontStyle.Bold),
        ForeColor = Color.FromArgb(0x66, 0x6A, 0x70)
    };

    private static void StyleButton(Button b, string text)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEC, 0xEF, 0xF3);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xE0, 0xE4, 0xEA);
        b.BackColor = Color.White;
        b.ForeColor = Color.FromArgb(0x33, 0x37, 0x3D);
        b.Font = new Font("Segoe UI", 8.5F);
        b.Size = new Size(Math.Max(70, text.Length * 7 + 24), 28);
        b.Cursor = Cursors.Hand;
    }

    private static void StyleEditor(TextBox box, bool readOnly)
    {
        box.Multiline = true;
        box.ReadOnly = readOnly;
        box.ScrollBars = ScrollBars.Vertical;
        box.Dock = DockStyle.Fill;
        box.Font = new Font("Consolas", 10F);
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = readOnly ? Color.FromArgb(0xF7, 0xF8, 0xFA) : Color.White;
        box.ForeColor = Color.FromArgb(0x2A, 0x2E, 0x34);
        box.AcceptsTab = true;
    }

    // The editors render CRLF; stored JSON keeps the export format's \n.
    private static string ForBox(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
    private static string ForData(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    // ------------------------------------------------------------------ data

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder with *_teks.json / etc.json" };
        if (_folder is not null) dialog.SelectedPath = _folder;
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenFolderNow(dialog.SelectedPath);
    }

    private void OpenFolderNow(string folder)
    {
        FlushCurrent();
        if (_dirty) Save(silent: true);
        _folder = folder;
        _document = null;
        _documentPath = null;
        _allRows.Clear();
        _view.Clear();
        _entries.Items.Clear();

        var items = new List<FileItem>();
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (string path in Directory.GetFiles(folder).Where(IsTranslationJson)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var counts = CountFile(path);
                items.Add(new FileItem(Path.GetFileName(path), path,
                    counts?.Total ?? 0, counts?.Untranslated ?? 0));
            }
        }
        finally { Cursor = Cursors.Default; }

        _files.Items.Clear();
        foreach (var item in items) _files.Items.Add(item);
        if (_files.Items.Count > 0) _files.SelectedIndex = 0;

        _statusFile.Text = folder;
        SaveSettings();
    }

    private static bool IsTranslationJson(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith("_teks.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("etc.json", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Total, int Untranslated)? CountFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            int total = 0, untranslated = 0;
            void Scan(JsonElement entries)
            {
                foreach (var e in entries.EnumerateArray())
                {
                    total++;
                    if (e.TryGetProperty("translation", out var t) && e.TryGetProperty("source", out var s) &&
                        string.Equals(t.GetString(), s.GetString(), StringComparison.Ordinal))
                        untranslated++;
                }
            }
            var root = doc.RootElement;
            if (root.TryGetProperty("blocks", out var blocks))
                foreach (var b in blocks.EnumerateArray())
                    if (b.TryGetProperty("entries", out var en)) Scan(en);
            foreach (string section in new[] { "strings", "executable" })
                if (root.TryGetProperty(section, out var arr)) Scan(arr);
            return (total, untranslated);
        }
        catch
        {
            return null;
        }
    }

    private void LoadSelectedFile()
    {
        if (_files.SelectedItem is not FileItem item) return;
        FlushCurrent();
        if (_dirty) Save(silent: true);

        try
        {
            _document = Files.ReadDocument(item.Path);
        }
        catch (Exception ex)
        {
            _document = null;
            MessageBox.Show(this, ex.Message, "Load failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _documentPath = item.Path;
        _dirty = false;

        _allRows.Clear();
        foreach (var block in _document.Blocks)
            foreach (var entry in block.Entries)
                _allRows.Add(new Row(entry, $"block {block.Index}"));
        foreach (var entry in _document.Strings ?? [])
            _allRows.Add(new Row(entry, "string"));
        foreach (var entry in _document.Executable ?? [])
            _allRows.Add(new Row(entry, "executable"));

        RefreshEntries();
    }

    private void RefreshEntries()
    {
        if (_suspend) return;
        Row? keep = SelectedRow();
        _suspend = true;

        string filter = _search.Text.Trim();
        _view.Clear();
        foreach (var row in _allRows)
        {
            if (_untranslatedOnly.Checked && row.Entry.Translation != row.Entry.Source) continue;
            if (filter.Length > 0 &&
                row.Entry.Source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                row.Entry.Translation.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _view.Add(row);
        }

        _entries.BeginUpdate();
        _entries.Items.Clear();
        if (_view.Count > 0) _entries.Items.AddRange(_view.Cast<object>().ToArray());
        int index = keep is null ? -1 : _view.IndexOf(keep);
        _entries.SelectedIndex = _view.Count == 0 ? -1 : Math.Clamp(index < 0 ? 0 : index, 0, _view.Count - 1);
        _entries.EndUpdate();
        _suspend = false;

        LoadSelectedEntry();
        _dialogHeader.Text = _view.Count == _allRows.Count
            ? $"Dialogs  ({_allRows.Count})"
            : $"Dialogs  ({_view.Count} of {_allRows.Count})";
        UpdateStatus();
    }

    private Row? SelectedRow() => _entries.SelectedItem as Row;

    private void LoadSelectedEntry()
    {
        if (_suspend) return;
        if (SelectedRow() is not { } row) return;
        _suspend = true;
        _sourceBox.Text = ForBox(row.Entry.Source);
        _translationBox.Text = ForBox(row.Entry.Translation);
        _sourceHeader.Text = $"Source — {row.Entry.Id} · {row.Section} · {row.Entry.Source.Length} chars";
        _suspend = false;
        UpdateTranslationHeader();
    }

    private void OnTranslationChanged()
    {
        if (_suspend) return;
        if (SelectedRow() is not { } row) return;
        string data = ForData(_translationBox.Text);
        if (data == row.Entry.Translation) return;
        bool wasTranslated = row.Entry.Translation != row.Entry.Source;
        row.Entry.Translation = data;
        bool isTranslated = row.Entry.Translation != row.Entry.Source;
        if (wasTranslated != isTranslated && _files.SelectedItem is FileItem item)
        {
            int delta = isTranslated ? -1 : 1;
            _files.Items[_files.SelectedIndex] = item with { Untranslated = item.Untranslated + delta };
            _files.Invalidate();
        }
        _dirty = true;
        _entries.Invalidate();
        UpdateTranslationHeader();
        UpdateStatus();
    }

    private void UpdateTranslationHeader()
    {
        if (SelectedRow() is not { } row) return;
        string data = ForData(_translationBox.Text);
        string issue = TokenIssue(row.Entry.Source, data);
        _translationHeader.Text = $"Translation — {data.Length} chars"
            + (issue.Length == 0 ? "" : $"   ⚠ {issue}");
        _translationHeader.ForeColor = issue.Length == 0
            ? Color.FromArgb(0x66, 0x6A, 0x70)
            : Color.FromArgb(0xC0, 0x39, 0x2B);
    }

    private static string TokenIssue(string source, string translation)
    {
        if (!CtrlRe.Matches(source).Select(m => m.Value)
                .SequenceEqual(CtrlRe.Matches(translation).Select(m => m.Value))) return "CTRL tokens differ";
        if (!RawRe.Matches(source).Select(m => m.Value)
                .SequenceEqual(RawRe.Matches(translation).Select(m => m.Value))) return "RAW tokens differ";
        if (!PrintfRe.Matches(source).Select(m => m.Value)
                .SequenceEqual(PrintfRe.Matches(translation).Select(m => m.Value))) return "printf tokens differ";
        return "";
    }

    // ------------------------------------------------------------- navigation

    private void MoveSelection(int delta)
    {
        if (_view.Count == 0) return;
        int next = Math.Clamp(_entries.SelectedIndex + delta, 0, _view.Count - 1);
        SelectRow(next);
    }

    private void JumpToNextUntranslated()
    {
        if (_view.Count == 0) return;
        int start = _entries.SelectedIndex;
        for (int step = 1; step <= _view.Count; step++)
        {
            int i = (start + step) % _view.Count;
            if (_view[i].Entry.Translation == _view[i].Entry.Source)
            {
                SelectRow(i);
                return;
            }
        }
        _statusCounts.Text = "  all visible entries translated";
    }

    private void SelectRow(int index)
    {
        _entries.SelectedIndex = index;
        _entries.TopIndex = Math.Max(0, index - 3);
        FocusTranslation();
    }

    private void FocusTranslation()
    {
        _translationBox.Focus();
        _translationBox.SelectionStart = _translationBox.Text.Length;
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S) { Save(); e.Handled = true; }
        else if (e.KeyCode == Keys.F4) { JumpToNextUntranslated(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Down) { MoveSelection(1); e.Handled = true; }
        else if (e.Alt && e.KeyCode == Keys.Up) { MoveSelection(-1); e.Handled = true; }
    }

    // ------------------------------------------------------------------ save

    private void FlushCurrent()
    {
        if (SelectedRow() is { } row)
        {
            string data = ForData(_translationBox.Text);
            if (data != row.Entry.Translation)
            {
                row.Entry.Translation = data;
                _dirty = true;
            }
        }
    }

    private void Save(bool silent = false)
    {
        FlushCurrent();
        if (_document is null || _documentPath is null) return;
        try
        {
            Files.WriteJson(_documentPath, _document, _ => true);
            _dirty = false;
            if (_files.SelectedItem is FileItem item)
            {
                int untranslated = _allRows.Count(r => r.Entry.Translation == r.Entry.Source);
                _files.Items[_files.SelectedIndex] = item with { Total = _allRows.Count, Untranslated = untranslated };
                _files.Invalidate();
            }
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (silent) throw;
            MessageBox.Show(this, ex.Message, "Save failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateStatus()
    {
        if (_document is null || _documentPath is null)
        {
            _statusCounts.Text = "";
            _statusDirty.Text = "";
            return;
        }
        int translated = _allRows.Count - _allRows.Count(r => r.Entry.Translation == r.Entry.Source);
        double pct = _allRows.Count == 0 ? 0 : translated * 100.0 / _allRows.Count;
        _statusCounts.Text = $"  {translated}/{_allRows.Count} translated ({pct:0.0}%)";
        _statusDirty.Text = _dirty ? "● unsaved" : "";
        _statusFile.Text = Path.GetFileName(_documentPath);
    }

    // ---------------------------------------------------------------- drawing

    private void DrawFileItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _files.Items.Count) return;
        var item = (FileItem)_files.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.DrawBackground();

        using var nameBrush = new SolidBrush(selected ? SystemColors.HighlightText : Color.FromArgb(0x2A, 0x2E, 0x34));
        using var countBrush = new SolidBrush(selected ? SystemColors.HighlightText :
            item.Untranslated > 0 ? Color.FromArgb(0xB3, 0x55, 0x0F) : Color.FromArgb(0x5A, 0x8F, 0x5A));
        e.Graphics.DrawString(item.Name, _files.Font, nameBrush, e.Bounds.X + 6, e.Bounds.Y + 3);

        string count = item.Total == 0 ? "" : item.Untranslated > 0 ? item.Untranslated.ToString() : "✓";
        if (count.Length > 0)
        {
            var size = e.Graphics.MeasureString(count, _files.Font);
            e.Graphics.DrawString(count, _files.Font, countBrush,
                e.Bounds.Right - size.Width - 8, e.Bounds.Y + 3);
        }
        e.DrawFocusRectangle();
    }

    private void DrawEntryItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _view.Count) return;
        var row = _view[e.Index];
        bool translated = row.Entry.Translation != row.Entry.Source;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.DrawBackground();

        using var markerBrush = new SolidBrush(selected ? SystemColors.HighlightText :
            translated ? Color.FromArgb(0x4C, 0x8C, 0x4A) : Color.FromArgb(0xB0, 0xB4, 0xBA));
        using var textBrush = new SolidBrush(selected ? SystemColors.HighlightText : Color.FromArgb(0x2A, 0x2E, 0x34));
        using var sectionBrush = new SolidBrush(selected ? SystemColors.HighlightText : Color.FromArgb(0x9A, 0x9F, 0xA6));

        e.Graphics.DrawString(translated ? "[+]" : "[ ]", _entries.Font, markerBrush,
            e.Bounds.X + 6, e.Bounds.Y + 2);

        string text = OneLine(row.Entry.Source);
        var textSize = e.Graphics.MeasureString(text, _entries.Font);
        var clip = new RectangleF(e.Bounds.X + 40, e.Bounds.Y, e.Bounds.Width - 40 - 80, e.Bounds.Height);
        e.Graphics.DrawString(text, _entries.Font, textBrush, clip);

        string section = row.Section;
        var sectionSize = e.Graphics.MeasureString(section, _entries.Font);
        e.Graphics.DrawString(section, _entries.Font, sectionBrush,
            e.Bounds.Right - sectionSize.Width - 8, e.Bounds.Y + 2);

        e.DrawFocusRectangle();
    }

    private static string OneLine(string text)
    {
        string flat = text.Replace("\n", " / ");
        return flat.Length > 160 ? flat[..160] + "..." : flat;
    }

    // --------------------------------------------------------------- settings

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SuikodenText", "translator-settings.json");

    private static string? LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("folder", out var f) ? f.GetString() : null;
        }
        catch { return null; }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new { folder = _folder ?? "" }));
        }
        catch { /* settings are best-effort */ }
    }
}
