using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>
    /// Toggle-list editor: a list of titles, each of which expands / collapses to show its own rich-text content.
    /// </summary>
    internal class TogglePanel : UserControl
    {
        public event Action Changed;
        public event Action<NoteTextBox> BodyCreated;

        private List<ToggleItem> _items = new List<ToggleItem>();
        private readonly FlowLayoutPanel _flow;
        private readonly Panel _bottom;
        private readonly Label _add, _expandAll, _collapseAll;
        private readonly ToolTip _tt;
        private NoteTextBox _active;

        internal Color BaseColor = Palette.Default;
        internal SkinType Skin = SkinType.Plain;
        internal Font ItemFont = new Font("Segoe UI", 11f);
        internal bool ReadOnly;

        public TogglePanel(ToolTip tt)
        {
            _tt = tt;
            DoubleBuffered = true;
            _flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(0), Margin = new Padding(0)
            };
            _bottom = new Panel { Dock = DockStyle.Bottom, Height = UI.S(26) };
            _add = MakeLink("+   Add toggle", () => AddAtEnd());
            _add.Dock = DockStyle.Fill;
            _collapseAll = MakeLink("Collapse all", () => ExpandAll(false));
            _collapseAll.Dock = DockStyle.Right; _collapseAll.AutoSize = false; _collapseAll.Width = UI.S(80);
            _expandAll = MakeLink("Expand all", () => ExpandAll(true));
            _expandAll.Dock = DockStyle.Right; _expandAll.AutoSize = false; _expandAll.Width = UI.S(74);
            _bottom.Controls.Add(_add);
            _bottom.Controls.Add(_expandAll);
            _bottom.Controls.Add(_collapseAll);
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

        public List<ToggleItem> Items => _items;
        internal IEnumerable<ToggleSection> Sections => _flow.Controls.OfType<ToggleSection>();

        /// <summary>The text box that the formatting toolbar works on.</summary>
        public NoteTextBox ActiveBody
        {
            get
            {
                if (_active != null && !_active.IsDisposed && _active.Parent != null) return _active;
                return Sections.FirstOrDefault(s => s.Item.Expanded)?.Body;
            }
        }

        internal void SetActive(NoteTextBox b) => _active = b;
        internal void RaiseChanged() => Changed?.Invoke();
        internal void RaiseBodyCreated(NoteTextBox b) => BodyCreated?.Invoke(b);

        public void Bind(List<ToggleItem> items)
        {
            _items = items ?? new List<ToggleItem>();
            Rebuild();
        }

        private void Rebuild()
        {
            _flow.SuspendLayout();
            foreach (Control c in _flow.Controls.Cast<Control>().ToList()) { _flow.Controls.Remove(c); c.Dispose(); }
            _active = null;
            foreach (var it in _items) _flow.Controls.Add(new ToggleSection(this, it, _tt));
            _flow.ResumeLayout();
            FitWidths();
        }

        public void ApplyLook(Color baseColor, SkinType skin, Font font, bool readOnly)
        {
            BaseColor = baseColor; Skin = skin; ItemFont = font; ReadOnly = readOnly;
            BackColor = baseColor; _flow.BackColor = baseColor;
            _bottom.BackColor = UI.Darken(baseColor, 0.03f);
            _add.Visible = !readOnly;
            foreach (var s in Sections) s.ApplyLook();
            FitWidths();
        }

        private bool _fitting;
        internal void FitWidths()
        {
            if (_fitting || _flow.ClientSize.Width <= 0) return;
            _fitting = true;
            try
            {
                int total = Sections.Sum(r => r.Height + r.Margin.Vertical);
                int w = _flow.Width - (total > _flow.Height ? SystemInformation.VerticalScrollBarWidth : 0) - 1;
                _flow.SuspendLayout();
                foreach (var r in Sections) if (r.Width != w) r.Width = Math.Max(UI.S(80), w);
                _flow.ResumeLayout();
            }
            finally { _fitting = false; }
        }

        public void AddAtEnd(string title = "")
        {
            if (ReadOnly) return;
            var it = new ToggleItem { Title = title, Expanded = true };
            _items.Add(it);
            var sec = new ToggleSection(this, it, _tt);
            _flow.Controls.Add(sec);
            FitWidths();
            _flow.ScrollControlIntoView(sec);
            sec.FocusTitle();
            RaiseChanged();
        }

        internal void InsertAfter(ToggleSection sec)
        {
            if (ReadOnly) return;
            int idx = _items.IndexOf(sec.Item);
            var it = new ToggleItem { Expanded = true };
            _items.Insert(idx + 1, it);
            var ns = new ToggleSection(this, it, _tt);
            _flow.Controls.Add(ns);
            _flow.Controls.SetChildIndex(ns, idx + 1);
            FitWidths();
            _flow.ScrollControlIntoView(ns);
            ns.FocusTitle();
            RaiseChanged();
        }

        internal void Remove(ToggleSection sec, bool confirm)
        {
            if (ReadOnly) return;
            if (confirm && sec.Body.TextLength > 0 &&
                !UI.Confirm(FindForm(), $"Delete the toggle \"{(string.IsNullOrWhiteSpace(sec.Item.Title) ? "(untitled)" : sec.Item.Title)}\" and its content?")) return;
            int idx = _items.IndexOf(sec.Item);
            _items.Remove(sec.Item);
            if (_active == sec.Body) _active = null;
            _flow.Controls.Remove(sec);
            sec.Dispose();
            FitWidths();
            if (_items.Count > 0) FocusIndex(Math.Max(0, idx - 1));
            RaiseChanged();
        }

        internal void MoveSection(ToggleSection sec, int delta)
        {
            if (ReadOnly) return;
            int idx = _items.IndexOf(sec.Item), n = idx + delta;
            if (n < 0 || n >= _items.Count) return;
            _items.RemoveAt(idx);
            _items.Insert(n, sec.Item);
            _flow.Controls.SetChildIndex(sec, n);
            sec.FocusTitle();
            RaiseChanged();
        }

        internal void FocusRelative(ToggleSection sec, int delta)
        {
            int idx = _items.IndexOf(sec.Item) + delta;
            if (idx >= 0 && idx < _items.Count) FocusIndex(idx);
        }

        private void FocusIndex(int idx)
        {
            var s = Sections.FirstOrDefault(x => x.Item == _items[idx]);
            if (s != null) { _flow.ScrollControlIntoView(s); s.FocusTitle(); }
        }

        /// <summary>Expands the toggle that owns the given body and scrolls it into view.</summary>
        public void Reveal(NoteTextBox body)
        {
            var s = Sections.FirstOrDefault(x => x.Body == body);
            if (s == null) return;
            if (!s.Item.Expanded) s.SetExpanded(true, false);
            _flow.ScrollControlIntoView(s);
        }

        public void InsertAfterActive()
        {
            var s = Sections.FirstOrDefault(x => x.Body == ActiveBody);
            if (s != null) InsertAfter(s); else AddAtEnd();
        }

        /// <summary>Scrolls the list so the caret of the body being edited stays visible while typing.</summary>
        internal void EnsureCaretVisible(NoteTextBox body)
        {
            try
            {
                if (!body.Focused || !_flow.IsHandleCreated) return;
                var pt = body.GetPositionFromCharIndex(body.SelectionStart);
                var p = _flow.PointToClient(body.PointToScreen(pt));
                int lineH = body.Font.Height + UI.S(6);
                int cur = -_flow.AutoScrollPosition.Y;
                if (p.Y + lineH > _flow.ClientSize.Height) _flow.AutoScrollPosition = new Point(0, cur + p.Y + lineH - _flow.ClientSize.Height);
                else if (p.Y < 0) _flow.AutoScrollPosition = new Point(0, Math.Max(0, cur + p.Y - UI.S(4)));
            }
            catch { }
        }

        public void ExpandAll(bool expand)
        {
            _flow.SuspendLayout();
            foreach (var s in Sections) s.SetExpanded(expand, false);
            _flow.ResumeLayout();
            FitWidths();
            RaiseChanged();
        }

        public void FocusFirst()
        {
            if (_items.Count == 0) { if (!ReadOnly) AddAtEnd(); }
            else FocusIndex(0);
        }

        /// <summary>Copies rich text of every body into the model (before saving).</summary>
        public void SyncToModel()
        {
            foreach (var s in Sections) s.SyncToModel();
        }

        public string PlainText => string.Join("\n", _items.Select(i =>
            "▸ " + i.Title + (string.IsNullOrWhiteSpace(i.PlainText) ? "" : "\n" + string.Join("\n", i.PlainText.Split('\n').Select(l => "    " + l)))));

        public int WordCount => _items.Sum(i => System.Text.RegularExpressions.Regex.Matches((i.Title ?? "") + " " + (i.PlainText ?? ""), @"\S+").Count);
    }

    /// <summary>One toggle: arrow + title, and a rich-text body that is shown when expanded.</summary>
    internal class ToggleSection : Panel
    {
        public readonly ToggleItem Item;
        public readonly NoteTextBox Body;
        private readonly TogglePanel _owner;
        private readonly GlyphButton _arrow, _del, _addBelow;
        private readonly TextBox _title;
        private bool _loading;
        private int _contentsH, _bodyH;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private int HeaderH => Math.Max(UI.S(30), _title.PreferredHeight + UI.S(10));
        private int Indent => UI.S(28);
        private const string ChevronRight = "\uE76C", ChevronDown = "\uE70D";

        public ToggleSection(TogglePanel owner, ToggleItem item, ToolTip tt)
        {
            _owner = owner; Item = item;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(0);

            _arrow = new GlyphButton(ChevronRight, "Expand / collapse (Ctrl+Enter)", tt, 8f);
            _title = new TextBox { BorderStyle = BorderStyle.None };
            _addBelow = new GlyphButton(UI.GAdd, "Add a new toggle below (Ctrl+Shift+Enter)", tt, 8f);
            _del = new GlyphButton(UI.GCancel, "Delete this toggle", tt, 7.5f);
            _addBelow.ForeColor = _del.ForeColor = Color.FromArgb(120, 120, 120);
            Body = new NoteTextBox { ScrollBars = RichTextBoxScrollBars.None, WordWrap = true, Multiline = true };
            Controls.AddRange(new Control[] { _arrow, _title, _addBelow, _del, Body });

            _loading = true;
            Body.Font = owner.ItemFont;
            _title.Text = item.Title ?? "";
            try
            {
                string safe = Security.RtfSanitizer.LooksLikeRtf(item.Rtf) ? Security.InputValidator.SafeRtf(item.Rtf) : null;
                if (safe != null) Body.Rtf = safe;
                else Body.Text = item.PlainText ?? "";
            }
            catch { Body.Text = item.PlainText ?? ""; }
            _loading = false;

            _arrow.Click += (s, e) => SetExpanded(!Item.Expanded, true);
            _addBelow.Click += (s, e) => _owner.InsertAfter(this);
            _del.Click += (s, e) => _owner.Remove(this, true);
            _title.TextChanged += (s, e) => { if (_loading) return; Item.Title = _title.Text; _owner.RaiseChanged(); };
            _title.KeyDown += Title_KeyDown;
            _title.HandleCreated += (s, e) => UI.SetCue(_title, "Toggle title…");
            _title.Enter += (s, e) => _owner.SetActive(Body);
            Body.Enter += (s, e) => _owner.SetActive(Body);
            Body.TextChanged += (s, e) =>
            {
                if (_loading) return;
                Item.PlainText = Body.Text;
                UpdateBodyHeight();
                _owner.RaiseChanged();
            };
            Body.ContentsResized += (s, e) => { _contentsH = e.NewRectangle.Height; UpdateBodyHeight(); };
            Body.HandleCreated += (s, e) => SendMessage(Body.Handle, 0x0441, IntPtr.Zero, IntPtr.Zero);
            Body.KeyDown += Body_KeyDown;
            Body.SelectionChanged += (s, e) => { if (Body.Focused) BeginInvoke((Action)(() => _owner.EnsureCaretVisible(Body))); };
            MouseClick += (s, e) => { if (e.Y < HeaderH) SetExpanded(!Item.Expanded, true); };

            _owner.RaiseBodyCreated(Body);
            ApplyLook();
        }

        public void ApplyLook()
        {
            var bc = _owner.BaseColor;
            BackColor = bc;
            Body.BackColor = bc;
            Body.Skin = _owner.Skin;
            if (Body.TextLength == 0) Body.Font = _owner.ItemFont;
            _title.BackColor = UI.Darken(bc, 0.05f);
            _title.Font = new Font(_owner.ItemFont.FontFamily, _owner.ItemFont.Size + 0.5f, FontStyle.Bold);
            _title.ReadOnly = _owner.ReadOnly;
            Body.ReadOnly = _owner.ReadOnly;
            _del.Visible = _addBelow.Visible = !_owner.ReadOnly;
            _arrow.Text = Item.Expanded ? ChevronDown : ChevronRight;
            Body.Visible = Item.Expanded;
            UpdateBodyHeight();
            LayoutChildren();
            Invalidate();
        }

        public void SetExpanded(bool expanded, bool focus)
        {
            Item.Expanded = expanded;
            _arrow.Text = expanded ? ChevronDown : ChevronRight;
            Body.Visible = expanded;
            UpdateHeight();
            _owner.FitWidths();
            if (focus)
            {
                if (expanded && !_owner.ReadOnly) Body.Focus(); else _title.Focus();
                _owner.RaiseChanged();
            }
        }

        private int MeasureBody()
        {
            int w = Math.Max(UI.S(40), Body.Width - UI.S(8));
            string t = Body.Text;
            if (string.IsNullOrEmpty(t)) t = "Ag";
            if (t.EndsWith("\n")) t += "Ag";
            var sz = TextRenderer.MeasureText(t, Body.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return sz.Height;
        }

        private void UpdateBodyHeight()
        {
            int minH = TextRenderer.MeasureText("Ag", Body.Font).Height + UI.S(6);
            // exact height reported by RichEdit (EN_REQUESTRESIZE); measuring is only a fallback
            int h = Math.Max(minH, (_contentsH > 0 ? _contentsH : MeasureBody()) + UI.S(6));
            if (h != _bodyH)
            {
                _bodyH = h;
                UpdateHeight();
                _owner.FitWidths();
            }
        }

        private void UpdateHeight()
        {
            int h = HeaderH + (Item.Expanded ? _bodyH + UI.S(8) : 0);
            if (Height != h) Height = h;
            LayoutChildren();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
            if (Body != null && Body.Visible) UpdateBodyHeight();
        }

        private void LayoutChildren()
        {
            if (Body == null) return;
            int hh = HeaderH;
            _arrow.Location = new Point(UI.S(3), (hh - _arrow.Height) / 2);
            bool ro = _owner.ReadOnly;
            int right = Width - UI.S(2);
            if (!ro)
            {
                _del.Location = new Point(right - _del.Width, (hh - _del.Height) / 2); right = _del.Left;
                _addBelow.Location = new Point(right - _addBelow.Width, (hh - _addBelow.Height) / 2); right = _addBelow.Left;
            }
            int tx = _arrow.Right + UI.S(2);
            _title.Location = new Point(tx, (hh - _title.PreferredHeight) / 2 + 1);
            _title.Width = Math.Max(UI.S(20), right - tx - UI.S(4));
            Body.Location = new Point(Indent, hh + UI.S(2));
            int bw = Math.Max(UI.S(40), Width - Indent - UI.S(6));
            bool widthChanged = Body.Width != bw;
            Body.Size = new Size(bw, Math.Max(UI.S(10), _bodyH));
            if (widthChanged && Body.IsHandleCreated) SendMessage(Body.Handle, 0x0441 /*EM_REQUESTRESIZE*/, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int hh = HeaderH;
            using (var b = new SolidBrush(UI.Darken(BackColor, 0.05f))) g.FillRectangle(b, 0, 0, Width, hh);
            using (var p = new Pen(UI.Darken(BackColor, 0.15f))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            if (Item.Expanded)
                using (var p = new Pen(UI.Darken(BackColor, 0.18f), Math.Max(1, UI.S(2))))
                    g.DrawLine(p, UI.S(14), hh + UI.S(2), UI.S(14), Height - UI.S(6));
        }

        public void SyncToModel()
        {
            Item.Title = _title.Text;
            Item.PlainText = Body.Text;
            try { Item.Rtf = Body.TextLength == 0 ? "" : Body.Rtf; } catch { }
        }

        public void FocusTitle()
        {
            _title.Focus();
            _title.SelectionStart = _title.TextLength;
            _owner.SetActive(Body);
        }

        private void Title_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Control && e.Shift) { e.SuppressKeyPress = true; _owner.InsertAfter(this); }
            else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; SetExpanded(!Item.Expanded, false); }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (!Item.Expanded) SetExpanded(true, false);
                Body.Focus();
            }
            else if (e.KeyCode == Keys.Back && _title.TextLength == 0 && Body.TextLength == 0) { e.SuppressKeyPress = true; _owner.Remove(this, false); }
            else if (e.KeyCode == Keys.Up && e.Alt) { e.SuppressKeyPress = true; _owner.MoveSection(this, -1); }
            else if (e.KeyCode == Keys.Down && e.Alt) { e.SuppressKeyPress = true; _owner.MoveSection(this, 1); }
            else if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; _owner.FocusRelative(this, -1); }
            else if (e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                if (Item.Expanded) { Body.Focus(); Body.SelectionStart = 0; }
                else _owner.FocusRelative(this, 1);
            }
        }

        private void Body_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Control && e.Shift) { e.SuppressKeyPress = true; _owner.InsertAfter(this); }
            else if (e.KeyCode == Keys.Up && e.Control) { e.SuppressKeyPress = true; FocusTitle(); }
            else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; SetExpanded(false, false); FocusTitle(); }
        }
    }
}
