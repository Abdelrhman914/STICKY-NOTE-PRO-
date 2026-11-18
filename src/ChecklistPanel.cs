using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>Checklist editor: each line has a checkbox, text and an optional deadline + alert.</summary>
    internal class ChecklistPanel : UserControl
    {
        public event Action Changed;
        private List<ChecklistItem> _items = new List<ChecklistItem>();
        private readonly FlowLayoutPanel _flow;
        private readonly Label _add, _addSection;
        private readonly Panel _bottom;
        private readonly ToolTip _tt;
        internal Color BaseColor = Palette.Default;
        internal SkinType Skin = SkinType.Plain;
        internal Font ItemFont = new Font("Segoe UI", 11f);
        internal bool ReadOnly;

        public ChecklistPanel(ToolTip tt)
        {
            _tt = tt;
            DoubleBuffered = true;
            _flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(0), Margin = new Padding(0)
            };
            _bottom = new Panel { Dock = DockStyle.Bottom, Height = UI.S(26) };
            _add = MakeLink("+   Add item", () => AddAtEnd());
            _add.Dock = DockStyle.Fill;
            _addSection = MakeLink("+  Add section", () => AddSection());
            _addSection.Dock = DockStyle.Right; _addSection.AutoSize = false; _addSection.Width = UI.S(104);
            tt.SetToolTip(_addSection, "Add a collapsible section title (group items under it)");
            _bottom.Controls.Add(_add);
            _bottom.Controls.Add(_addSection);
            Controls.Add(_flow);
            Controls.Add(_bottom);
            _flow.Resize += (s, e) => FitWidths();
            _flow.Click += (s, e) => { if (_items.Count == 0) AddAtEnd(); };
        }

        private Label MakeLink(string text, Action act)
        {
            var l = new Label
            {
                Text = text, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(UI.S(8), 0, 0, 0), ForeColor = Color.FromArgb(110, 0, 0, 0), Font = UI.UiFont
            };
            l.Click += (s, e) => act();
            l.MouseEnter += (s, e) => l.ForeColor = Color.FromArgb(200, 0, 0, 0);
            l.MouseLeave += (s, e) => l.ForeColor = Color.FromArgb(110, 0, 0, 0);
            return l;
        }

        public List<ChecklistItem> Items => _items;
        public bool HasSections => _items.Any(i => i.IsHeading);

        /// <summary>Shows / hides rows according to collapsed section headings and refreshes heading counters.</summary>
        internal void UpdateSections()
        {
            _flow.SuspendLayout();
            bool collapsed = false;
            foreach (var r in Rows)
            {
                if (r.Item.IsHeading) { collapsed = r.Item.Collapsed; r.SetHidden(false); r.Invalidate(); }
                else r.SetHidden(collapsed);
            }
            _flow.ResumeLayout();
            FitWidths();
        }

        /// <summary>done / total / overdue counts for the items that belong to a heading.</summary>
        internal void SectionStats(ChecklistItem heading, out int done, out int total, out bool overdue)
        {
            done = total = 0; overdue = false;
            int idx = _items.IndexOf(heading);
            for (int i = idx + 1; i < _items.Count && !_items[i].IsHeading; i++)
            {
                total++;
                if (_items[i].Done) done++;
                else if (_items[i].Due.Enabled && _items[i].Due.At < DateTime.Now) overdue = true;
            }
        }

        internal void ToggleSection(ChecklistRow row)
        {
            row.Item.Collapsed = !row.Item.Collapsed;
            row.ApplyLook();
            UpdateSections();
            RaiseChanged();
        }

        public void ExpandAll(bool expand)
        {
            foreach (var it in _items.Where(i => i.IsHeading)) it.Collapsed = !expand;
            foreach (var r in Rows.Where(r => r.Item.IsHeading)) r.ApplyLook();
            UpdateSections();
            RaiseChanged();
        }

        public void AddSection(string text = "")
        {
            if (ReadOnly) return;
            var it = new ChecklistItem { Text = text, IsHeading = true };
            _items.Add(it);
            var row = new ChecklistRow(this, it, _tt);
            _flow.Controls.Add(row);
            UpdateSections();
            _flow.ScrollControlIntoView(row);
            row.FocusText(false);
            RaiseChanged();
        }

        internal void MakeHeading(ChecklistRow row, bool heading)
        {
            if (ReadOnly) return;
            row.Item.IsHeading = heading;
            row.Item.Collapsed = false;
            if (heading) { row.Item.Done = false; row.Item.Due.Enabled = false; }
            row.SyncFromItem();
            UpdateSections();
            row.FocusText(true);
            RaiseChanged();
        }

        public void Bind(List<ChecklistItem> items)
        {
            _items = items ?? new List<ChecklistItem>();
            Rebuild();
        }

        public void ApplyLook(Color baseColor, SkinType skin, Font font, bool readOnly)
        {
            BaseColor = baseColor; Skin = skin; ItemFont = font; ReadOnly = readOnly;
            BackColor = baseColor; _flow.BackColor = baseColor; _bottom.BackColor = UI.Darken(baseColor, 0.03f);
            _add.Visible = _addSection.Visible = !readOnly;
            foreach (var r in Rows) r.ApplyLook();
            UpdateSections();
        }

        private IEnumerable<ChecklistRow> Rows => _flow.Controls.OfType<ChecklistRow>();
        private IEnumerable<ChecklistRow> ShownRows => Rows.Where(r => !r.Hidden);

        private void Rebuild()
        {
            _flow.SuspendLayout();
            foreach (Control c in _flow.Controls.Cast<Control>().ToList()) { _flow.Controls.Remove(c); c.Dispose(); }
            foreach (var it in _items) _flow.Controls.Add(new ChecklistRow(this, it, _tt));
            _flow.ResumeLayout();
            UpdateSections();
        }

        internal void FitWidths()
        {
            if (_flow.ClientSize.Width <= 0) return;
            int total = ShownRows.Sum(r => r.Height + r.Margin.Vertical);
            int w = _flow.Width - (total > _flow.Height ? SystemInformation.VerticalScrollBarWidth : 0) - 1;
            _flow.SuspendLayout();
            foreach (var r in Rows) if (r.Width != w) r.Width = Math.Max(UI.S(80), w);
            _flow.ResumeLayout();
        }

        internal void RaiseChanged() => Changed?.Invoke();

        public void AddAtEnd(string text = "")
        {
            if (ReadOnly) return;
            var it = new ChecklistItem { Text = text };
            var lastHead = _items.LastOrDefault(i => i.IsHeading);
            if (lastHead != null && lastHead.Collapsed) { lastHead.Collapsed = false; Rows.FirstOrDefault(r => r.Item == lastHead)?.ApplyLook(); }
            _items.Add(it);
            var row = new ChecklistRow(this, it, _tt);
            _flow.Controls.Add(row);
            UpdateSections();
            _flow.ScrollControlIntoView(row);
            row.FocusText(false);
            RaiseChanged();
        }

        internal void InsertAfter(ChecklistRow row)
        {
            if (ReadOnly) return;
            int idx = _items.IndexOf(row.Item);
            if (row.Item.IsHeading && row.Item.Collapsed) { row.Item.Collapsed = false; row.ApplyLook(); }
            var it = new ChecklistItem();
            _items.Insert(idx + 1, it);
            var nr = new ChecklistRow(this, it, _tt);
            _flow.Controls.Add(nr);
            _flow.Controls.SetChildIndex(nr, idx + 1);
            UpdateSections();
            _flow.ScrollControlIntoView(nr);
            nr.FocusText(false);
            RaiseChanged();
        }

        internal void RemoveRow(ChecklistRow row, bool focusPrev)
        {
            if (ReadOnly) return;
            int idx = _items.IndexOf(row.Item);
            _items.Remove(row.Item);
            _flow.Controls.Remove(row);
            row.Dispose();
            UpdateSections();
            if (focusPrev && _items.Count > 0) FocusIndex(Math.Max(0, idx - 1), true);
            RaiseChanged();
        }

        internal void FocusRelative(ChecklistRow row, int delta)
        {
            var shown = ShownRows.ToList();
            int idx = shown.IndexOf(row) + delta;
            if (idx >= 0 && idx < shown.Count) { _flow.ScrollControlIntoView(shown[idx]); shown[idx].FocusText(true); }
        }

        internal void MoveRow(ChecklistRow row, int delta)
        {
            if (ReadOnly) return;
            int idx = _items.IndexOf(row.Item);
            int n = idx + delta;
            if (n < 0 || n >= _items.Count) return;
            _items.RemoveAt(idx);
            _items.Insert(n, row.Item);
            _flow.Controls.SetChildIndex(row, n);
            UpdateSections();
            row.FocusText(true);
            RaiseChanged();
        }

        private void FocusIndex(int idx, bool end)
        {
            var row = Rows.FirstOrDefault(r => r.Item == _items[idx]);
            if (row != null) { _flow.ScrollControlIntoView(row); row.FocusText(end); }
        }

        public void RefreshDeadlines()
        {
            foreach (var r in Rows) r.UpdateLayoutAndPaint();
            UpdateSections();
        }

        public void RefreshItem(ChecklistItem item)
        {
            var row = Rows.FirstOrDefault(r => r.Item == item);
            row?.SyncFromItem();
            UpdateSections();
        }

        public void FocusFirst()
        {
            if (_items.Count == 0) { if (!ReadOnly) AddAtEnd(); }
            else FocusIndex(0, true);
        }

        public string PlainText => string.Join("\n", _items.Select(i => i.IsHeading ? "## " + i.Text : (i.Done ? "[x] " : "[ ] ") + i.Text +
            (i.Due.Enabled ? $"  (deadline {i.Due.At:dd MMM yyyy HH:mm})" : "")));
    }

    internal class ChecklistRow : Panel
    {
        public readonly ChecklistItem Item;
        private readonly ChecklistPanel _owner;
        private readonly CheckBox _chk;
        private readonly TextBox _txt;
        private readonly GlyphButton _btnDate, _btnDel, _arrow;
        private readonly ToolTip _tt;
        private bool _loading;
        private bool _showDel = true;
        public bool Hidden { get; private set; }
        private bool Heading => Item.IsHeading;
        private bool HasDue => Item.Due.Enabled && !Item.IsHeading;

        public void SetHidden(bool hidden)
        {
            Hidden = hidden;
            Visible = !hidden;
        }

        private int TopH => Math.Max(UI.S(26), _txt.PreferredHeight + UI.S(8));
        private int InfoH => UI.S(22);

        public ChecklistRow(ChecklistPanel owner, ChecklistItem item, ToolTip tt)
        {
            _owner = owner; Item = item; _tt = tt;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(0);

            _chk = new CheckBox { AutoSize = false, Size = new Size(UI.S(18), UI.S(18)), Cursor = Cursors.Hand };
            _txt = new TextBox { BorderStyle = BorderStyle.None };
            _btnDate = new GlyphButton(UI.GCalendar, "Set deadline & alert for this task", tt, 9.5f);
            _btnDel = new GlyphButton(UI.GCancel, "Remove this item", tt, 7.5f);
            _btnDel.ForeColor = Color.FromArgb(120, 120, 120);
            _arrow = new GlyphButton("\uE70D", "Expand / collapse this section (Ctrl+Enter)", tt, 8f) { Size = new Size(UI.S(20), UI.S(22)) };
            _arrow.Click += (s, e) => _owner.ToggleSection(this);
            Controls.AddRange(new Control[] { _chk, _arrow, _txt, _btnDate, _btnDel });

            _chk.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                Item.Done = _chk.Checked;
                Item.DoneOn = Item.Done ? DateTime.Now : default;
                ApplyLook();
                _owner.UpdateSections();
                _owner.RaiseChanged();
            };
            _txt.TextChanged += (s, e) => { if (_loading) return; Item.Text = _txt.Text; _tt.SetToolTip(_txt, Item.Text); _owner.RaiseChanged(); };
            _txt.HandleCreated += (s, e) => { if (Item.IsHeading) UI.SetCue(_txt, "Section title…"); };
            _txt.KeyDown += Txt_KeyDown;
            _btnDate.Click += (s, e) => EditDeadline();
            _btnDel.Click += (s, e) => _owner.RemoveRow(this, false);
            Cursor = Cursors.Default;

            SyncFromItem();
        }

        public void SyncFromItem()
        {
            _loading = true;
            _chk.Checked = Item.Done;
            if (_txt.Text != Item.Text) _txt.Text = Item.Text;
            _loading = false;
            ApplyLook();
        }

        public void ApplyLook()
        {
            var bc = _owner.BaseColor;
            BackColor = bc;
            _txt.BackColor = bc;
            if (Heading)
            {
                _txt.BackColor = UI.Darken(bc, 0.07f);
                _txt.Font = new Font(_owner.ItemFont.FontFamily, _owner.ItemFont.Size + 0.5f, FontStyle.Bold);
                _txt.ForeColor = Color.FromArgb(30, 30, 30);
                _arrow.Text = Item.Collapsed ? "\uE76C" : "\uE70D";
                _btnDel.ForeColor = Color.FromArgb(120, 120, 120);
            }
            else
            {
                _txt.Font = Item.Done ? new Font(_owner.ItemFont, FontStyle.Strikeout) : _owner.ItemFont;
                _txt.ForeColor = Item.Done ? Color.FromArgb(130, 130, 130) : Color.Black;
            }
            if (_txt.IsHandleCreated) UI.SetCue(_txt, Heading ? "Section title…" : "");
            _chk.Visible = !Heading;
            _arrow.Visible = Heading;
            _btnDate.Visible = !Heading;
            _txt.ReadOnly = _owner.ReadOnly;
            _chk.Enabled = !_owner.ReadOnly;
            _showDel = !_owner.ReadOnly;
            _btnDel.Visible = _showDel;
            _btnDate.Enabled = !_owner.ReadOnly;
            _btnDate.ForeColor = Item.Due.Enabled ? Color.FromArgb(0, 100, 200) : Color.FromArgb(90, 90, 90);
            _btnDate.Checked = Item.Due.Enabled;
            UpdateLayoutAndPaint();
        }

        public void UpdateLayoutAndPaint()
        {
            int h = TopH + (HasDue ? InfoH : 0) + (Heading ? UI.S(4) : 0);
            if (Height != h) Height = h;
            LayoutChildren();
            if (HasDue)
            {
                Item.Due.StatusText(Item.Done, out _);
                _tt.SetToolTip(this, $"Deadline: {Item.Due.At:dddd, d MMMM yyyy  HH:mm}\n{Item.Due.AlertText()}\nClick to change");
            }
            else _tt.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutChildren(); }

        private void LayoutChildren()
        {
            if (_txt == null) return;
            int th = TopH;
            int right = Width;
            _btnDel.Location = new Point(right - _btnDel.Width - UI.S(2), (th - _btnDel.Height) / 2);
            _btnDate.Location = new Point((_showDel ? _btnDel.Left : right) - _btnDate.Width - UI.S(1), (th - _btnDate.Height) / 2);
            if (Heading)
            {
                int top = UI.S(4);
                _arrow.Location = new Point(UI.S(3), top + (th - _arrow.Height) / 2);
                _btnDel.Top = top + (th - _btnDel.Height) / 2;
                int tx = _arrow.Right + UI.S(2);
                _txt.Location = new Point(tx, top + (th - _txt.PreferredHeight) / 2 + 1);
                // leave room for the "done/total" counter
                _txt.Width = Math.Max(UI.S(20), (_showDel ? _btnDel.Left : right) - UI.S(56) - tx);
                return;
            }
            _chk.Location = new Point(UI.S(6), (th - _chk.Height) / 2);
            int tx2 = _chk.Right + UI.S(4);
            _txt.Location = new Point(tx2, (th - _txt.PreferredHeight) / 2 + 1);
            _txt.Width = Math.Max(UI.S(20), _btnDate.Left - tx2 - UI.S(2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // skin separator line
            var sk = _owner.Skin;
            if (sk == SkinType.Ruled || sk == SkinType.Notebook || sk == SkinType.Grid)
                using (var p = new Pen(Color.FromArgb(70, 60, 110, 180))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            else if (sk == SkinType.Dotted)
                using (var p = new Pen(Color.FromArgb(110, 90, 90, 90)) { DashStyle = DashStyle.Dot }) g.DrawLine(p, UI.S(4), Height - 1, Width, Height - 1);
            if (sk == SkinType.Notebook)
                using (var red = new Pen(Color.FromArgb(120, 220, 60, 60))) g.DrawLine(red, UI.S(2), 0, UI.S(2), Height);

            if (Heading)
            {
                var band = new Rectangle(0, UI.S(4), Width, Height - UI.S(4));
                using (var b = new SolidBrush(UI.Darken(BackColor, 0.07f))) g.FillRectangle(b, band);
                using (var p = new Pen(UI.Darken(BackColor, 0.2f))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
                _owner.SectionStats(Item, out int sDone, out int sTotal, out bool overdue);
                if (sTotal > 0)
                {
                    string cnt = $"{sDone}/{sTotal}";
                    var sCol = sDone == sTotal ? Color.FromArgb(40, 140, 60) : Color.FromArgb(100, 100, 100);
                    int rx = (_showDel ? _btnDel.Left : Width) - UI.S(4);
                    var r = new Rectangle(rx - UI.S(50), band.Y, UI.S(50), band.Height);
                    TextRenderer.DrawText(g, cnt + (Item.Collapsed ? " …" : ""), UI.SmallFont, r, sCol, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
                    if (overdue)
                        using (var red = new SolidBrush(Color.FromArgb(210, 50, 50)))
                            g.FillEllipse(red, rx - UI.S(56), band.Y + band.Height / 2 - UI.S(3), UI.S(6), UI.S(6));
                }
                return;
            }

            if (!Item.Due.Enabled) return;
            int y = TopH - UI.S(3);
            var rect = new Rectangle(_txt.Left, y, Width - _txt.Left - UI.S(6), InfoH - UI.S(2));
            string status = Item.Due.StatusText(Item.Done, out Color col);
            string bell = Item.Due.Alert ? "  " : "";

            // pill
            var iconFont = UI.IconFont(8f);
            string text = $"{Item.Due.At:ddd d MMM, HH:mm}  •  {status}";
            var sz = TextRenderer.MeasureText(g, text, UI.SmallFont, Size.Empty, TextFormatFlags.NoPadding);
            int iconW = UI.S(16);
            int pillW = Math.Min(rect.Width, sz.Width + iconW * (Item.Due.Alert ? 2 : 1) + UI.S(12));
            var pill = new Rectangle(rect.X - UI.S(2), rect.Y, pillW, rect.Height - UI.S(2));
            using (var path = UI.RoundRect(pill, pill.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(Item.Done ? 25 : 40, col)))
            using (var pen = new Pen(Color.FromArgb(90, col)))
            {
                g.FillPath(b, path); g.DrawPath(pen, path);
            }
            int cx = pill.X + UI.S(6);
            TextRenderer.DrawText(g, UI.GCalendar, iconFont, new Rectangle(cx, pill.Y, iconW, pill.Height), col, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            cx += iconW;
            TextRenderer.DrawText(g, text, UI.SmallFont, new Rectangle(cx, pill.Y, pill.Right - cx - (Item.Due.Alert ? iconW : 0), pill.Height), col,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Item.Due.Alert)
                TextRenderer.DrawText(g, UI.GBell, iconFont, new Rectangle(pill.Right - iconW - UI.S(2), pill.Y, iconW, pill.Height), col, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            // progress bar (time used between "set on" and deadline)
            int total = Item.Due.TotalDays;
            if (!Item.Done && total > 0)
            {
                double used = 1.0 - Math.Max(0, Item.Due.DaysLeft) / (double)total;
                used = Math.Max(0, Math.Min(1, used));
                int bx = pill.Right + UI.S(8), bw = rect.Right - bx;
                if (bw > UI.S(30))
                {
                    var bar = new Rectangle(bx, pill.Y + pill.Height / 2 - UI.S(2), bw, UI.S(4));
                    using (var bg = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(bg, bar);
                    using (var fg = new SolidBrush(col)) g.FillRectangle(fg, new Rectangle(bar.X, bar.Y, (int)(bar.Width * used), bar.Height));
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (HasDue && e.Y > TopH - UI.S(4) && !_owner.ReadOnly) EditDeadline();
            else if (Heading && e.X < _txt.Left) _owner.ToggleSection(this);
            else _txt.Focus();
        }

        public void EditDeadline()
        {
            if (Heading) return;
            using (var dlg = new DeadlineDialog(Item.Text, Item.Due))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.Result != null)
                {
                    Item.Due = dlg.Result;
                    ApplyLook();
                    _owner.FitWidths();
                    _owner.RaiseChanged();
                }
            }
        }

        public void FocusText(bool end)
        {
            _txt.Focus();
            _txt.SelectionStart = end ? _txt.TextLength : 0;
            _txt.SelectionLength = 0;
        }

        private void Txt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.H && e.Control && e.Shift) { e.SuppressKeyPress = true; _owner.MakeHeading(this, !Heading); }
            else if (e.KeyCode == Keys.Enter && e.Control && Heading) { e.SuppressKeyPress = true; _owner.ToggleSection(this); }
            else if (e.KeyCode == Keys.Enter && e.Control) { _chk.Checked = !_chk.Checked; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _owner.InsertAfter(this); }
            else if (e.KeyCode == Keys.Back && _txt.TextLength == 0) { e.SuppressKeyPress = true; _owner.RemoveRow(this, true); }
            else if (e.KeyCode == Keys.Up && e.Alt) { e.SuppressKeyPress = true; _owner.MoveRow(this, -1); }
            else if (e.KeyCode == Keys.Down && e.Alt) { e.SuppressKeyPress = true; _owner.MoveRow(this, 1); }
            else if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; _owner.FocusRelative(this, -1); }
            else if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; _owner.FocusRelative(this, 1); }
            else if (e.KeyCode == Keys.D && e.Control && e.Shift) { e.SuppressKeyPress = true; EditDeadline(); }
        }
    }
}
