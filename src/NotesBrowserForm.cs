using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>Main window listing all notes and all deadlines.</summary>
    internal class NotesBrowserForm : Form
    {
        private readonly AppController _app;
        private readonly TabControl _tabs;
        private readonly TreeView _tree;
        private readonly ListView _list, _deadlines;
        private readonly RichTextBox _preview;
        private readonly ToolStripTextBox _search;
        private readonly CheckBox _chkShowDone;
        private int _sortCol = 6; private bool _sortDesc = true;
        private readonly Timer _refreshTimer = new Timer { Interval = 400 };

        public NotesBrowserForm(AppController app)
        {
            _app = app;
            Text = "StickyNotes Pro – Notes browser";
            Icon = AppController.AppIcon; Font = UI.UiFont;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(UI.S(980), UI.S(640));
            MinimumSize = new Size(UI.S(640), UI.S(400));

            // toolbar
            var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(UI.S(6), UI.S(2), 0, UI.S(2)), ImageScalingSize = new Size(UI.S(20), UI.S(20)) };
            ToolStripButton TB(string text, string glyph, Action act)
            {
                var b = new ToolStripButton(text, GlyphImage(glyph), (s, e) => act()) { TextImageRelation = TextImageRelation.ImageAboveText, Padding = new Padding(UI.S(4), 0, UI.S(4), 0) };
                ts.Items.Add(b);
                return b;
            }
            TB("New note", UI.GAdd, () => _app.NewNote(NoteKind.Text, null));
            TB("New checklist", UI.GCheckList, () => _app.NewNote(NoteKind.Checklist, null));
            TB("New toggle list", UI.GList, () => _app.NewNote(NoteKind.Toggle, null));
            TB("Show", UI.GWindow, () => ForSelected(n => _app.GoToNote(n)));
            TB("Hide", UI.GMinimize, () => ForSelected(n => n.HideNote()));
            TB("Delete", UI.GDelete, DeleteSelected);
            ts.Items.Add(new ToolStripSeparator());
            var arrange = new ToolStripDropDownButton("Arrange notes", GlyphImage("\uE8A9")) { TextImageRelation = TextImageRelation.ImageAboveText };
            arrange.DropDownItems.Add("Cascade", null, (s, e) => _app.Arrange("cascade"));
            arrange.DropDownItems.Add("Tile", null, (s, e) => _app.Arrange("tile"));
            arrange.DropDownItems.Add("Line up on the right", null, (s, e) => _app.Arrange("right"));
            arrange.DropDownItems.Add(new ToolStripSeparator());
            arrange.DropDownItems.Add("Show all desktop notes", null, (s, e) => _app.ShowAll(true));
            arrange.DropDownItems.Add("Hide all notes", null, (s, e) => _app.ShowAll(false));
            ts.Items.Add(arrange);
            var backup = new ToolStripDropDownButton("Backup / transfer", GlyphImage("\uE896")) { TextImageRelation = TextImageRelation.ImageAboveText };
            foreach (ToolStripItem bi in _app.BuildBackupMenu(this, SelectedData).DropDownItems.Cast<ToolStripItem>().ToList()) backup.DropDownItems.Add(bi);
            ts.Items.Add(backup);
            TB("Settings", UI.GSettings, () => _app.ShowSettings());
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Search:"));
            _search = new ToolStripTextBox { Width = UI.S(200), BorderStyle = BorderStyle.FixedSingle };
            _search.TextChanged += (s, e) => RefreshLists();
            ts.Items.Add(_search);

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(UI.S(14), UI.S(5)) };
            var tNotes = new TabPage("  Notes  ");
            var tDead = new TabPage("  Deadlines & alerts  ");
            _tabs.TabPages.Add(tNotes); _tabs.TabPages.Add(tDead);

            // ---- notes tab
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None, ItemHeight = UI.S(22), FullRowSelect = true, ShowLines = false };
            _tree.AfterSelect += (s, e) => RefreshLists();
            split.Panel1.Controls.Add(_tree);

            var split2 = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None };
            _list.Columns.Add("★", UI.S(28));
            _list.Columns.Add("Title", UI.S(260));
            _list.Columns.Add("Type", UI.S(80));
            _list.Columns.Add("Due / next deadline", UI.S(150));
            _list.Columns.Add("Tags", UI.S(120));
            _list.Columns.Add("Stuck to", UI.S(140));
            _list.Columns.Add("Date modified", UI.S(130));
            _list.SelectedIndexChanged += (s, e) => UpdatePreview();
            _list.DoubleClick += (s, e) => ForSelected(n => _app.GoToNote(n));
            _list.ColumnClick += (s, e) => { if (_sortCol == e.Column) _sortDesc = !_sortDesc; else { _sortCol = e.Column; _sortDesc = false; } RefreshLists(); };
            _list.MouseClick += (s, e) =>
            {
                var hit = _list.HitTest(e.Location);
                if (hit.Item != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 0 && hit.Item.Tag is NoteForm nf)
                { nf.Data.Starred = !nf.Data.Starred; _app.MarkDirty(); RefreshLists(); }
            };
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); if (e.KeyCode == Keys.Enter) ForSelected(n => _app.GoToNote(n)); };
            var cm = new ContextMenuStrip();
            cm.Items.Add("Show note", null, (s, e) => ForSelected(n => _app.GoToNote(n)));
            cm.Items.Add("Hide note", null, (s, e) => ForSelected(n => n.HideNote()));
            cm.Items.Add("Edit title…", null, (s, e) => ForSelected(n => n.EditTitle()));
            cm.Items.Add("Due date / alert…", null, (s, e) => ForSelected(n => n.EditNoteDue()));
            cm.Items.Add("Stick to window…", null, (s, e) => ForSelected(n => n.StickToWindow()));
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Export selected notes (backup file)…", null, (s, e) => { var sel = SelectedData(); if (sel.Count > 0) _app.ExportBackup(this, sel); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Delete", null, (s, e) => DeleteSelected());
            _list.ContextMenuStrip = cm;
            _preview = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White };
            split2.Panel1.Controls.Add(_list);
            split2.Panel2.Controls.Add(_preview);
            split.Panel2.Controls.Add(split2);
            tNotes.Controls.Add(split);

            // ---- deadlines tab
            var top = new Panel { Dock = DockStyle.Top, Height = UI.S(36) };
            var info = new Label { Text = "All checklist tasks and notes with a deadline, sorted by date. Double-click to open, right-click for options.", Left = UI.S(8), Top = UI.S(10), AutoSize = true, ForeColor = Color.DimGray };
            _chkShowDone = new CheckBox { Text = "Show completed", AutoSize = true, Top = UI.S(8), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _chkShowDone.CheckedChanged += (s, e) => RefreshLists();
            top.Controls.Add(info); top.Controls.Add(_chkShowDone);
            top.Resize += (s, e) => _chkShowDone.Left = top.Width - _chkShowDone.Width - UI.S(10);
            _deadlines = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None };
            _deadlines.Columns.Add("Task", UI.S(300));
            _deadlines.Columns.Add("Note", UI.S(170));
            _deadlines.Columns.Add("Deadline", UI.S(170));
            _deadlines.Columns.Add("Days left", UI.S(150));
            _deadlines.Columns.Add("Alert", UI.S(170));
            _deadlines.DoubleClick += (s, e) => { if (_deadlines.SelectedItems.Count > 0) _app.GoToNote(((DeadlineRow)_deadlines.SelectedItems[0].Tag).Note); };
            var dcm = new ContextMenuStrip();
            dcm.Items.Add("Go to note", null, (s, e) => { if (SelDeadline() is DeadlineRow r) _app.GoToNote(r.Note); });
            dcm.Items.Add("Mark as done / not done", null, (s, e) =>
            {
                if (SelDeadline() is DeadlineRow r && r.Item != null)
                { r.Item.Done = !r.Item.Done; r.Item.DoneOn = r.Item.Done ? DateTime.Now : default; r.Note.RefreshItem(r.Item); _app.MarkDirty(); RefreshLists(); }
            });
            dcm.Items.Add("Edit deadline & alert…", null, (s, e) =>
            {
                if (!(SelDeadline() is DeadlineRow r)) return;
                using (var d = new DeadlineDialog(r.Item?.Text ?? r.Note.Data.DisplayTitle, r.Due))
                    if (d.ShowDialog(this) == DialogResult.OK && d.Result != null)
                    {
                        if (r.Item != null) { r.Item.Due = d.Result; r.Note.RefreshItem(r.Item); } else r.Note.Data.Due = d.Result;
                        r.Note.RefreshDeadlines(); _app.MarkDirty(); RefreshLists();
                    }
            });
            _deadlines.ContextMenuStrip = dcm;
            tDead.Controls.Add(_deadlines);
            tDead.Controls.Add(top);

            Controls.Add(_tabs);
            Controls.Add(ts);

            Load += (s, e) => { split.SplitterDistance = UI.S(180); split2.SplitterDistance = (int)(split2.Height * 0.62); RebuildTree(); RefreshLists(); };
            _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); RebuildTree(); RefreshLists(); };
            _app.NotesChanged += OnNotesChanged;
            FormClosed += (s, e) => { _app.NotesChanged -= OnNotesChanged; _refreshTimer.Dispose(); };
        }

        public void ShowDeadlinesTab() => _tabs.SelectedIndex = 1;

        private void OnNotesChanged()
        {
            if (IsDisposed) return;
            _refreshTimer.Stop(); _refreshTimer.Start();
        }

        private static Image GlyphImage(string glyph)
        {
            var bmp = new Bitmap(UI.S(20), UI.S(20));
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                TextRenderer.DrawText(g, glyph, UI.IconFont(12f), new Rectangle(0, 0, bmp.Width, bmp.Height), Color.FromArgb(40, 90, 160), Color.Transparent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            return bmp;
        }

        private class NoteRow { public NoteForm Note; public string[] Cols; public DateTime? Next; }
        private class DeadlineRow { public NoteForm Note; public ChecklistItem Item; public Deadline Due; }
        private DeadlineRow SelDeadline() => _deadlines.SelectedItems.Count > 0 ? (DeadlineRow)_deadlines.SelectedItems[0].Tag : null;

        private void RebuildTree()
        {
            string sel = _tree.SelectedNode?.Name ?? "all";
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            var notes = _app.Forms;
            void Add(string key, string text, int count) => _tree.Nodes.Add(key, $"{text}  ({count})");
            Add("all", "All notes", notes.Count);
            Add("starred", "★ Starred", notes.Count(n => n.Data.Starred));
            Add("desktop", "Desktop notes", notes.Count(n => n.Data.Mode == StickMode.Desktop));
            Add("window", "Stuck to windows", notes.Count(n => n.Data.Mode == StickMode.Window));
            Add("checklist", "Checklists", notes.Count(n => n.Data.Kind == NoteKind.Checklist));
            Add("toggle", "Toggle lists", notes.Count(n => n.Data.Kind == NoteKind.Toggle));
            Add("due", "With deadlines", notes.Count(n => n.Data.Due.Enabled || n.LiveItems.Any(i => i.Due.Enabled && !i.Done)));
            Add("hidden", "Hidden notes", notes.Count(n => n.Data.Hidden));
            Add("encrypted", "Encrypted", notes.Count(n => n.Data.Encrypted));
            var tagRoot = _tree.Nodes.Add("tags", "Tags");
            foreach (var t in _app.AllTags())
                tagRoot.Nodes.Add("tag:" + t, $"{t}  ({notes.Count(n => n.Data.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))})");
            tagRoot.Expand();
            var found = _tree.Nodes.Find(sel, true);
            _tree.SelectedNode = found.Length > 0 ? found[0] : _tree.Nodes[0];
            _tree.EndUpdate();
        }

        private IEnumerable<NoteForm> Filtered()
        {
            string key = _tree.SelectedNode?.Name ?? "all";
            IEnumerable<NoteForm> q = _app.Forms;
            switch (key)
            {
                case "starred": q = q.Where(n => n.Data.Starred); break;
                case "desktop": q = q.Where(n => n.Data.Mode == StickMode.Desktop); break;
                case "window": q = q.Where(n => n.Data.Mode == StickMode.Window); break;
                case "checklist": q = q.Where(n => n.Data.Kind == NoteKind.Checklist); break;
                case "toggle": q = q.Where(n => n.Data.Kind == NoteKind.Toggle); break;
                case "due": q = q.Where(n => n.Data.Due.Enabled || n.LiveItems.Any(i => i.Due.Enabled && !i.Done)); break;
                case "hidden": q = q.Where(n => n.Data.Hidden); break;
                case "encrypted": q = q.Where(n => n.Data.Encrypted); break;
                default:
                    if (key.StartsWith("tag:")) { string t = key.Substring(4); q = q.Where(n => n.Data.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)); }
                    break;
            }
            string s = _search.Text.Trim();
            if (s.Length > 0)
                q = q.Where(n => n.Data.DisplayTitle.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                              || (n.Data.PlainText ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                              || n.LiveItems.Any(i => i.Text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                              || n.LiveToggles.Any(t => (t.Title ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || (t.PlainText ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                              || n.Data.Tags.Any(t => t.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0));
            return q;
        }

        private static DateTime? NextDeadline(NoteForm n)
        {
            var dates = n.LiveItems.Where(i => i.Due.Enabled && !i.Done).Select(i => i.Due.At).ToList();
            if (n.Data.Due.Enabled) dates.Add(n.Data.Due.At);
            return dates.Count == 0 ? (DateTime?)null : dates.Min();
        }

        public void RefreshLists()
        {
            if (IsDisposed) return;
            // notes
            var selIds = _list.SelectedItems.Cast<ListViewItem>().Select(i => ((NoteForm)i.Tag).Data.Id).ToList();
            var rows = Filtered().Select(n =>
            {
                var nd = NextDeadline(n);
                return new NoteRow
                {
                    Note = n,
                    Cols = new[]
                    {
                        n.Data.Starred ? "★" : "☆",
                        n.Data.DisplayTitle,
                        n.Data.Kind == NoteKind.Text ? "Text" : n.Data.Kind == NoteKind.Toggle ? $"Toggle list ({n.LiveToggles.Count})"
                            : $"Checklist ({n.LiveItems.Count(i => i.Done && !i.IsHeading)}/{n.LiveItems.Count(i => !i.IsHeading)})",
                        nd.HasValue ? nd.Value.ToString("ddd d MMM yyyy HH:mm") : "",
                        string.Join(", ", n.Data.Tags),
                        n.Data.Mode == StickMode.Window ? $"{n.Data.WinProcess} {n.Data.WinTitle}".Trim() : (n.Data.Hidden ? "Desktop (hidden)" : "Desktop"),
                        n.Data.Modified.ToString("yyyy-MM-dd HH:mm")
                    },
                    Next = nd
                };
            }).ToList();
            Func<NoteRow, IComparable> keySel = r => _sortCol == 3 ? (IComparable)(r.Next ?? DateTime.MaxValue)
                                                   : _sortCol == 6 ? (IComparable)r.Note.Data.Modified
                                                   : (IComparable)(r.Cols[_sortCol] ?? "").ToLowerInvariant();
            var sorted = _sortDesc ? rows.OrderByDescending(r => keySel(r)).ToList() : rows.OrderBy(r => keySel(r)).ToList();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in sorted)
            {
                var it = new ListViewItem(r.Cols) { Tag = r.Note, UseItemStyleForSubItems = false };
                it.SubItems[0].ForeColor = r.Note.Data.Starred ? Color.DarkOrange : Color.Silver;
                if (r.Note.Data.Hidden) for (int c = 1; c < it.SubItems.Count; c++) it.SubItems[c].ForeColor = Color.Gray;
                if (r.Next.HasValue && r.Next.Value < DateTime.Now) it.SubItems[3].ForeColor = Color.Firebrick;
                it.Selected = selIds.Contains(r.Note.Data.Id);
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            UpdatePreview();

            // deadlines
            var drs = new List<DeadlineRow>();
            foreach (var n in _app.Forms)
            {
                if (n.Data.Due.Enabled) drs.Add(new DeadlineRow { Note = n, Due = n.Data.Due });
                foreach (var i in n.LiveItems.Where(i => i.Due.Enabled && !i.IsHeading && (_chkShowDone.Checked || !i.Done)))
                    drs.Add(new DeadlineRow { Note = n, Item = i, Due = i.Due });
            }
            string s = _search.Text.Trim();
            if (s.Length > 0) drs = drs.Where(r => (r.Item?.Text ?? r.Note.Data.DisplayTitle).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || r.Note.Data.DisplayTitle.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _deadlines.BeginUpdate();
            _deadlines.Items.Clear();
            foreach (var r in drs.OrderBy(r => r.Item?.Done ?? false).ThenBy(r => r.Due.At))
            {
                string status = r.Due.StatusText(r.Item?.Done ?? false, out Color col);
                var it = new ListViewItem(new[]
                {
                    (r.Item != null ? (r.Item.Done ? "☑ " : "☐ ") + r.Item.Text : "📝 (whole note) " + r.Note.Data.DisplayTitle),
                    r.Note.Data.DisplayTitle,
                    r.Due.At.ToString("ddd d MMM yyyy  HH:mm"),
                    status,
                    r.Due.AlertText()
                }) { Tag = r, UseItemStyleForSubItems = false };
                it.SubItems[3].ForeColor = col;
                it.SubItems[3].Font = UI.UiFontBold;
                if (r.Item?.Done == true) it.SubItems[0].ForeColor = Color.Gray;
                _deadlines.Items.Add(it);
            }
            _deadlines.EndUpdate();
        }

        private void UpdatePreview()
        {
            if (_list.SelectedItems.Count != 1) { _preview.Clear(); return; }
            var n = (NoteForm)_list.SelectedItems[0].Tag;
            _preview.BackColor = n.Data.Skin == SkinType.Antique ? Palette.Antique : n.Data.Color;
            try
            {
                if (n.IsContentLocked) _preview.Text = "🔒 Encrypted note – open it and unlock to view.";
                else if (n.Data.Kind == NoteKind.Text && !n.Data.Encrypted && (n.Data.Rtf ?? "").StartsWith("{\\rtf")) _preview.Rtf = n.Data.Rtf;
                else if (n.Data.Kind == NoteKind.Checklist)
                {
                    _preview.Text = string.Join("\n", n.LiveItems.Select(i => i.IsHeading ? (i.Collapsed ? "▸ " : "▾ ") + i.Text : "      " + (i.Done ? "☑  " : "☐  ") + i.Text +
                        (i.Due.Enabled ? $"      [deadline {i.Due.At:ddd d MMM HH:mm} – {i.Due.StatusText(i.Done, out _)}]" : "")));
                    if (!n.LiveItems.Any(i => i.IsHeading)) _preview.Text = _preview.Text.Replace("\n      ", "\n").TrimStart(' ');
                    BoldLines(l => l.StartsWith("▸ ") || l.StartsWith("▾ "));
                }
                else if (n.Data.Kind == NoteKind.Toggle)
                {
                    _preview.Text = string.Join("\n", n.LiveToggles.Select(t => "▾ " + t.Title +
                        (string.IsNullOrWhiteSpace(t.PlainText) ? "" : "\n" + string.Join("\n", t.PlainText.Replace("\r", "").Split('\n').Select(l => "      " + l)))));
                    BoldLines(l => l.StartsWith("▾ "));
                }
                else _preview.Text = n.Data.PlainText ?? "";
            }
            catch { _preview.Text = n.Data.PlainText ?? ""; }
        }

        private void BoldLines(Func<string, bool> isTitle)
        {
            int pos = 0;
            foreach (var line in _preview.Text.Split('\n'))
            {
                if (isTitle(line)) { _preview.Select(pos, line.Length); _preview.SelectionFont = new Font(_preview.Font, FontStyle.Bold); }
                pos += line.Length + 1;
            }
            _preview.Select(0, 0);
        }

        private IList<NoteData> SelectedData() => _list.SelectedItems.Cast<ListViewItem>().Select(i => ((NoteForm)i.Tag).Data).ToList();

        private void ForSelected(Action<NoteForm> act)
        {
            foreach (var n in _list.SelectedItems.Cast<ListViewItem>().Select(i => (NoteForm)i.Tag).ToList()) act(n);
            RefreshLists();
        }

        private void DeleteSelected()
        {
            var sel = _list.SelectedItems.Cast<ListViewItem>().Select(i => (NoteForm)i.Tag).ToList();
            if (sel.Count == 0) return;
            if (!UI.Confirm(this, sel.Count == 1 ? $"Delete note \"{sel[0].Data.DisplayTitle}\"?" : $"Delete {sel.Count} notes?")) return;
            foreach (var n in sel) _app.DeleteNote(n, false);
            RebuildTree(); RefreshLists();
        }
    }
}
