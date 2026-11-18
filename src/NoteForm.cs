using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using StickyNotesPro.Security;

namespace StickyNotesPro
{
    /// <summary>A single sticky note window.</summary>
    internal class NoteForm : Form
    {
        public readonly NoteData Data;
        private readonly AppController _app;
        private readonly ToolTip _tt = new ToolTip { InitialDelay = 400 };
        private readonly Panel _title, _host, _toolbar, _lockedPanel;
        private readonly GlyphButton _btnMenu, _btnNew, _btnPin, _btnRoll, _btnClose;
        private readonly NoteTextBox _rtb;
        private readonly ChecklistPanel _checklist;
        private readonly TogglePanel _toggle;
        private List<ToggleItem> _decryptedToggles;
        private GlyphButton _bType;

        /// <summary>The rich-text box the formatting commands work on (text note, or the active toggle body).</summary>
        private NoteTextBox Tb => Data.Kind == NoteKind.Text ? _rtb : Data.Kind == NoteKind.Toggle && !IsContentLocked ? _toggle.ActiveBody : null;
        private Label _lblWords;
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ContextMenuStrip _textMenu = new ContextMenuStrip();
        private readonly List<GlyphButton> _fmtButtons = new List<GlyphButton>();
        private GlyphButton _bBold, _bItalic, _bUnder, _bStrike, _bBullet;

        private bool _decrypted;              // encrypted note currently unlocked
        private List<ChecklistItem> _decryptedItems;
        private bool _loading;
        internal bool ProgrammaticMove;
        public IntPtr AttachedHwnd = IntPtr.Zero;
        private string _lastFind = "";
        private int _rightClickIndex = -1;

        private int TitleH => UI.S(28);
        private int Border => UI.S(3);

        public NoteForm(AppController app, NoteData data)
        {
            _app = app; Data = data;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Text = "Sticky note";
            MinimumSize = new Size(UI.S(140), TitleH + Border * 2);
            Padding = new Padding(Border);
            Font = UI.UiFont;
            Icon = AppController.AppIcon;

            // ----- Title bar -----
            _title = new DoubleBufferedPanel { Dock = DockStyle.Top, Height = TitleH, AllowDrop = true };
            _btnMenu = new GlyphButton(UI.GMenu, "Menu", _tt);
            _btnNew = new GlyphButton(UI.GAdd, "New note (Ctrl+N)", _tt);
            _btnPin = new GlyphButton(UI.GPin, "Always on top", _tt);
            _btnRoll = new GlyphButton(UI.GMinimize, "Roll up / down (double-click title)", _tt);
            _btnClose = new GlyphButton(UI.GClose, "Hide note (open again from the tray / Notes browser)", _tt, 8.5f);
            foreach (var b in new[] { _btnMenu, _btnNew, _btnPin, _btnRoll, _btnClose })
            { b.Top = (TitleH - b.Height) / 2; _title.Controls.Add(b); }
            _title.Resize += (s, e) => LayoutTitle();
            _title.Paint += Title_Paint;
            _title.MouseDown += Title_MouseDown;
            _title.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowMenu(_title.PointToScreen(e.Location)); };
            _title.DoubleClick += (s, e) => ToggleRoll();
            _title.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Link; };
            _title.DragDrop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                { AddLinks(files); }
            };
            _btnMenu.Click += (s, e) => ShowMenu(_btnMenu.PointToScreen(new Point(0, _btnMenu.Height)));
            _btnNew.Click += (s, e) => _app.NewNote(NoteKind.Text, Location + new Size(UI.S(30), UI.S(30)));
            _btnPin.Click += (s, e) => { Data.TopMost = !Data.TopMost; ApplyAppearance(); Changed(); };
            _btnRoll.Click += (s, e) => ToggleRoll();
            _btnClose.Click += (s, e) => HideNote();

            // ----- Formatting toolbar -----
            _toolbar = new Panel { Dock = DockStyle.Bottom, Height = UI.S(28), Visible = false };
            BuildToolbar();

            // ----- Content -----
            _host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(UI.S(4), UI.S(2), UI.S(2), UI.S(2)) };
            _host.Paint += Host_Paint;
            _rtb = new NoteTextBox { Dock = DockStyle.Fill };
            _rtb.TextChanged += (s, e) => { if (!_loading) { Changed(); UpdateWordCount(); _title.Invalidate(); } };
            AttachTextBox(_rtb);
            _textMenu.Opening += TextMenu_Opening;

            _toggle = new TogglePanel(_tt) { Dock = DockStyle.Fill, Visible = false };
            _toggle.BodyCreated += AttachTextBox;
            _toggle.Changed += () => { if (!_loading) { Changed(); UpdateWordCount(); _title.Invalidate(); } };

            _checklist = new ChecklistPanel(_tt) { Dock = DockStyle.Fill, Visible = false };
            _checklist.Changed += () => { if (!_loading) { Changed(); _title.Invalidate(); } };

            _lockedPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            var lockLbl = new Label { Text = UI.GLock, Font = UI.IconFont(22f), AutoSize = false, Dock = DockStyle.Top, Height = UI.S(50), TextAlign = ContentAlignment.BottomCenter };
            var lockTxt = new Label { Text = "This note is encrypted.", AutoSize = false, Dock = DockStyle.Top, Height = UI.S(26), TextAlign = ContentAlignment.MiddleCenter };
            var unlock = new Button { Text = "Unlock…", Width = UI.S(100), Height = UI.S(28), FlatStyle = FlatStyle.System };
            var unlockHolder = new Panel { Dock = DockStyle.Top, Height = UI.S(40) };
            unlockHolder.Controls.Add(unlock);
            unlockHolder.Resize += (s, e) => unlock.Left = (unlockHolder.Width - unlock.Width) / 2;
            unlock.Click += (s, e) => Unlock();
            _lockedPanel.Controls.Add(unlockHolder);
            _lockedPanel.Controls.Add(lockTxt);
            _lockedPanel.Controls.Add(lockLbl);

            _host.Controls.Add(_rtb);
            _host.Controls.Add(_checklist);
            _host.Controls.Add(_toggle);
            _host.Controls.Add(_lockedPanel);

            Controls.Add(_host);
            Controls.Add(_toolbar);
            Controls.Add(_title);

            Bounds = new Rectangle(Data.X, Data.Y, Math.Max(UI.S(140), Data.W), Data.RolledUp ? TitleH + Border * 2 : Math.Max(UI.S(80), Data.H));
            EnsureOnScreen();

            ApplyAppearance();
            LoadContent();
            LayoutTitle();

            Activated += (s, e) => { UpdateToolbarVisibility(true); _title.Invalidate(); };
            Deactivate += (s, e) => { UpdateToolbarVisibility(false); _title.Invalidate(); };
            LocationChanged += (s, e) => OnMovedOrResized();
            SizeChanged += (s, e) => OnMovedOrResized();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // hide from Alt+Tab & taskbar
                cp.ClassStyle |= 0x00020000;             // CS_DROPSHADOW
                return cp;
            }
        }

        // ---------------------------------------------------------------- layout & painting

        private void LayoutTitle()
        {
            int x = UI.S(2);
            _btnMenu.Left = x;
            int r = _title.Width - UI.S(2);
            _btnClose.Left = r - _btnClose.Width; r = _btnClose.Left;
            _btnRoll.Left = r - _btnRoll.Width; r = _btnRoll.Left;
            _btnPin.Left = r - _btnPin.Width; r = _btnPin.Left;
            _btnNew.Left = r - _btnNew.Width;
            bool roomy = _title.Width > UI.S(200);
            _btnNew.Visible = roomy;
        }

        private void Title_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int left = _btnMenu.Right + UI.S(4);
            int right = (_title.Width > UI.S(200) ? _btnNew.Left : _btnPin.Left) - UI.S(4);
            var icons = new List<string>();
            if (Data.Encrypted) icons.Add(UI.GLock);
            else if (Data.ReadOnlyLock) icons.Add(UI.GLock);
            if (Data.Mode == StickMode.Window) icons.Add(UI.GWindow);
            if (Data.Due.Enabled) icons.Add(UI.GBell);
            if (Data.Links.Count > 0) icons.Add(UI.GLink);
            foreach (var ic in icons)
            {
                var sz = UI.S(16);
                TextRenderer.DrawText(g, ic, UI.IconFont(8.5f), new Rectangle(left, 0, sz, TitleH), Color.FromArgb(150, 0, 0, 0), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                left += sz;
            }
            TextRenderer.DrawText(g, Data.DisplayTitle, UI.UiFontBold, new Rectangle(left + UI.S(2), 0, Math.Max(0, right - left), TitleH), Color.FromArgb(40, 40, 40),
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                (icons.Count == 0 && _title.Width > UI.S(200) ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
        }

        private void Host_Paint(object sender, PaintEventArgs e)
        {
            if (Data.Skin != SkinType.Antique) return;
            // aged paper edges
            var r = _host.ClientRectangle;
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, r.Width, UI.S(6) + 1), Color.FromArgb(90, 150, 110, 50), Color.FromArgb(0, 150, 110, 50), 90f))
                e.Graphics.FillRectangle(b, 0, 0, r.Width, UI.S(6));
            using (var b = new LinearGradientBrush(new Rectangle(0, r.Height - UI.S(6) - 1, r.Width, UI.S(6) + 1), Color.FromArgb(0, 150, 110, 50), Color.FromArgb(90, 150, 110, 50), 90f))
                e.Graphics.FillRectangle(b, 0, r.Height - UI.S(6), r.Width, UI.S(6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // resize grip at bottom-right
            var g = e.Graphics;
            using (var p = new Pen(Color.FromArgb(90, 0, 0, 0)))
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                g.DrawLine(p, w - UI.S(3), h - UI.S(1), w - UI.S(1), h - UI.S(3));
            }
        }

        public void ApplyAppearance()
        {
            Color baseC = Data.Skin == SkinType.Antique ? Palette.Antique : Data.Color;
            BackColor = UI.Darken(baseC, 0.22f);
            _title.BackColor = UI.Darken(baseC, 0.07f);
            _host.BackColor = baseC;
            _rtb.BackColor = baseC;
            _rtb.Skin = Data.Skin;
            _toolbar.BackColor = UI.Darken(baseC, 0.04f);
            _lockedPanel.BackColor = baseC;
            Font f;
            try { f = new Font(Data.FontName ?? "Segoe UI", Data.FontSize <= 0 ? 11f : Data.FontSize); } catch { f = new Font("Segoe UI", 11f); }
            if (!_loading && _rtb.TextLength == 0) _rtb.Font = f;
            _checklist.ApplyLook(baseC, Data.Skin, f, Data.ReadOnlyLock);
            _toggle.ApplyLook(baseC, Data.Skin, f, Data.ReadOnlyLock);
            _rtb.ReadOnly = Data.ReadOnlyLock;
            Opacity = UI.Clamp(Data.Opacity, 20, 100) / 100.0;
            bool top = Data.TopMost || Data.Mode == StickMode.Window;
            if (TopMost != top) TopMost = top;
            _btnPin.Text = Data.TopMost ? UI.GPinned : UI.GPin;
            _btnPin.Checked = Data.TopMost;
            _title.Invalidate();
            _host.Invalidate();
            UpdateToolbarVisibility(Form.ActiveForm == this);
        }

        private void EnsureOnScreen()
        {
            var r = Bounds;
            var wa = Screen.FromRectangle(r).WorkingArea;
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X, r.Y, r.Width, UI.S(20)))))
                Location = new Point(wa.X + UI.S(100), wa.Y + UI.S(100));
        }

        // ---------------------------------------------------------------- content

        public List<ChecklistItem> LiveItems
        {
            get
            {
                if (Data.Kind != NoteKind.Checklist) return new List<ChecklistItem>();
                if (Data.Encrypted) return _decrypted ? (_decryptedItems ?? new List<ChecklistItem>()) : new List<ChecklistItem>();
                return Data.Items;
            }
        }

        public List<ToggleItem> LiveToggles
        {
            get
            {
                if (Data.Kind != NoteKind.Toggle) return new List<ToggleItem>();
                if (Data.Encrypted) return _decrypted ? (_decryptedToggles ?? new List<ToggleItem>()) : new List<ToggleItem>();
                return Data.Toggles;
            }
        }

        public bool IsContentLocked => Data.Encrypted && !_decrypted;

        /// <summary>Wires context menu, spell-check suggestions, links and toolbar state for a rich-text box.</summary>
        private void AttachTextBox(NoteTextBox box)
        {
            box.ContextMenuStrip = _textMenu;
            box.SelectionChanged += (s, e) => UpdateFormatButtons();
            box.LinkClicked += (s, e) => SafeShell.OpenHyperlink(this, e.LinkText);
            box.Enter += (s, e) => { if (box != _rtb) { _toggle.SetActive(box); UpdateFormatButtons(); } };
            box.MouseDown += (s, e) =>
            {
                if (box != _rtb) _toggle.SetActive(box);
                if (e.Button == MouseButtons.Right)
                {
                    _rightClickIndex = box.GetCharIndexFromPosition(e.Location);
                    if (box.SelectionLength == 0) box.Select(_rightClickIndex, 0);
                }
            };
        }

        private void LoadContent()
        {
            _loading = true;
            try
            {
                string rtf = Data.Rtf;
                List<ChecklistItem> items = Data.Items;
                List<ToggleItem> toggles = Data.Toggles;
                if (Data.Encrypted)
                {
                    if (!_decrypted)
                    {
                        _lockedPanel.Visible = true; _rtb.Visible = false; _checklist.Visible = false; _toggle.Visible = false;
                        _lockedPanel.BringToFront();
                        return;
                    }
                    items = _decryptedItems;
                    rtf = _decryptedRtf;
                    toggles = _decryptedToggles = _decryptedToggles ?? new List<ToggleItem>();
                }
                _lockedPanel.Visible = false;
                if (Data.Kind == NoteKind.Toggle)
                {
                    _rtb.Visible = false; _checklist.Visible = false; _toggle.Visible = true;
                    _toggle.BringToFront();
                    _toggle.Bind(toggles);
                }
                else if (Data.Kind == NoteKind.Text)
                {
                    _checklist.Visible = false; _toggle.Visible = false; _rtb.Visible = true;
                    _rtb.BringToFront();
                    try
                    {
                        string safe = RtfSanitizer.LooksLikeRtf(rtf) ? InputValidator.SafeRtf(rtf) : null;
                        if (safe != null) _rtb.Rtf = safe;
                        else if (RtfSanitizer.LooksLikeRtf(rtf)) _rtb.Text = Data.PlainText ?? "";
                        else { _rtb.Text = rtf ?? ""; }
                    }
                    catch { _rtb.Text = Data.PlainText ?? ""; }
                    if (_rtb.TextLength == 0)
                        try { _rtb.Font = new Font(Data.FontName, Data.FontSize); } catch { }
                }
                else
                {
                    _rtb.Visible = false; _toggle.Visible = false; _checklist.Visible = true;
                    _checklist.BringToFront();
                    _checklist.Bind(items);
                }
                UpdateWordCount();
            }
            finally { _loading = false; }
        }

        private string _decryptedRtf = "";

        /// <summary>Copy UI state into Data (called before saving).</summary>
        public void SyncToData()
        {
            if (WindowState == FormWindowState.Normal)
            {
                if (Data.Mode == StickMode.Desktop || !Visible)
                {
                    if (Data.Mode == StickMode.Desktop) { Data.X = Left; Data.Y = Top; }
                }
                Data.W = Width;
                if (!Data.RolledUp) Data.H = Height;
            }
            if (IsContentLocked) return;
            if (Data.Kind == NoteKind.Toggle) _toggle.SyncToModel();
            string rtf = Data.Kind == NoteKind.Text ? _rtb.Rtf : "";
            string plain = CurrentPlainText();
            if (Data.Encrypted)
            {
                var key = _app.MasterKey;
                if (key == null) return;
                var content = new EncryptedContent { Rtf = rtf, Items = _decryptedItems ?? new List<ChecklistItem>(), Toggles = _decryptedToggles ?? new List<ToggleItem>() };
                Data.EncryptedPayload = Crypto.Encrypt(Storage.SerializeContent(content), key);
                Data.Rtf = ""; Data.Items = new List<ChecklistItem>(); Data.Toggles = new List<ToggleItem>(); Data.PlainText = "";
                _decryptedRtf = rtf;
            }
            else
            {
                Data.Rtf = rtf;
                Data.PlainText = plain;
            }
        }

        private void Changed()
        {
            if (_loading) return;
            Data.Modified = DateTime.Now;
            if (Data.Kind == NoteKind.Text && !IsContentLocked && !Data.Encrypted) Data.PlainText = _rtb.Text;
            if (Data.Kind == NoteKind.Checklist && !IsContentLocked && !Data.Encrypted) Data.PlainText = _checklist.PlainText;
            if (Data.Kind == NoteKind.Toggle && !IsContentLocked && !Data.Encrypted) Data.PlainText = _toggle.PlainText;
            _app.MarkDirty();
        }

        private void OnMovedOrResized()
        {
            if (_loading) return;
            if (Data.Mode == StickMode.Window && !ProgrammaticMove && AttachedHwnd != IntPtr.Zero && Visible)
            {
                var r = Native.GetVisibleRect(AttachedHwnd);
                Data.WinOffsetX = Left - r.Left;
                Data.WinOffsetY = Top - r.Top;
            }
            _app.MarkDirty();
        }

        public void RefreshDeadlines()
        {
            if (Data.Kind == NoteKind.Checklist && !IsContentLocked) _checklist.RefreshDeadlines();
            _title.Invalidate();
        }

        public void RefreshItem(ChecklistItem item) => _checklist.RefreshItem(item);

        // ---------------------------------------------------------------- toolbar

        private readonly List<(GlyphButton btn, string tip, Action act)> _tbItems = new List<(GlyphButton, string, Action)>();
        private FlowLayoutPanel _tbFlow;
        private GlyphButton _bMore;

        private void BuildToolbar()
        {
            var flow = _tbFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(UI.S(2), UI.S(3), 0, 0) };
            GlyphButton Add(string glyph, string tip, Action act, float size = 9f, bool icon = true, FontStyle style = FontStyle.Bold)
            {
                var b = new GlyphButton(glyph, tip, _tt, size, icon) { Margin = new Padding(0), Width = UI.S(23) };
                if (!icon) b.Font = new Font("Segoe UI", size, style);
                Action run = () => { if (Tb == null) return; act(); Tb?.Focus(); };
                b.Click += (s, e) => run();
                flow.Controls.Add(b);
                _fmtButtons.Add(b);
                _tbItems.Add((b, tip, run));
                return b;
            }
            _bBold = Add("B", "Bold (Ctrl+B)", () => Tb.ToggleEffect(Native.CFM_BOLD), 9.5f, false, FontStyle.Bold);
            _bItalic = Add("I", "Italic (Ctrl+I)", () => Tb.ToggleEffect(Native.CFM_ITALIC), 9.5f, false, FontStyle.Italic | FontStyle.Bold);
            _bUnder = Add("U", "Underline (Ctrl+U)", () => Tb.ToggleEffect(Native.CFM_UNDERLINE), 9.5f, false, FontStyle.Underline | FontStyle.Bold);
            _bStrike = Add("S", "Strikethrough (Ctrl+T)", () => Tb.ToggleEffect(Native.CFM_STRIKEOUT), 9.5f, false, FontStyle.Strikeout | FontStyle.Bold);
            Add("\uE73A", "Checkboxes (Ctrl+Shift+C): select text and click to turn it into checkboxes on the same line, e.g. \u2610 lec1   \u2610 study.\nWith nothing selected, inserts a checkbox. Click a box (or press Ctrl+Space) to tick it. Tip: typing ( ) also makes a box.", () => Tb.MakeCheckboxes(), 9.5f);
            _bBullet = Add(UI.GList, "Bullets (Ctrl+Shift+L)", () => { Tb.SelectionBullet = !Tb.SelectionBullet; UpdateFormatButtons(); });
            Add(UI.GFontColor, "Text color", PickTextColor);
            Add(UI.GHighlight, "Highlight", PickHighlight);
            Add(UI.GFont, "Font…", PickFont);
            Add(UI.GAlignLeft, "Align (left / center / right)", CycleAlign);
            Add(UI.GClock, "Insert date / time (F5)", InsertDateTime);
            Add(UI.GPicture, "Add picture…", AddPicture);
            _bType = new GlyphButton(UI.GCheckList, "Change note type (text / checklist / toggle list)", _tt, 9f) { Margin = new Padding(0), Width = UI.S(23) };
            _bType.Click += (s, e) => BuildTypeMenu().Show(_bType, new Point(0, -UI.S(4)), ToolStripDropDownDirection.AboveRight);
            flow.Controls.Add(_bType);
            _tbItems.Add((_bType, "Change note type…", () => BuildTypeMenu().Show(_bMore, new Point(0, -UI.S(4)), ToolStripDropDownDirection.AboveLeft)));

            // "»" shows the buttons that do not fit into a narrow note
            _bMore = new GlyphButton("\u00BB", "More formatting", _tt, 10f, false) { Dock = DockStyle.Right, Width = UI.S(20), Visible = false };
            _bMore.Click += (s, e) =>
            {
                var m = new ContextMenuStrip();
                foreach (var it in _tbItems.Where(x => !x.btn.Visible))
                {
                    var run = it.act;
                    m.Items.Add(new ToolStripMenuItem(it.tip.Split('\n')[0].Split(':')[0], null, (s2, e2) => run()));
                }
                m.Show(_bMore, new Point(0, -UI.S(4)), ToolStripDropDownDirection.AboveLeft);
            };
            _toolbar.Controls.Add(flow);
            _toolbar.Controls.Add(_bMore);
            _lblWords = new Label { Dock = DockStyle.Right, AutoSize = false, Width = UI.S(64), TextAlign = ContentAlignment.MiddleRight, Font = UI.SmallFont, ForeColor = Color.FromArgb(110, 0, 0, 0), Padding = new Padding(0, 0, UI.S(6), 0) };
            _toolbar.Controls.Add(_lblWords);
            _toolbar.Resize += (s, e) => LayoutToolbarButtons();
        }

        private void LayoutToolbarButtons()
        {
            if (_tbFlow == null) return;
            int avail = _toolbar.Width - _lblWords.Width - UI.S(4);
            int all = _tbItems.Sum(x => x.btn.Width);
            bool overflow = all > avail;
            if (overflow) avail -= UI.S(20);
            int used = 0;
            _tbFlow.SuspendLayout();
            foreach (var it in _tbItems)
            {
                used += it.btn.Width;
                bool fits = used <= avail;
                if (it.btn.Visible != fits) it.btn.Visible = fits;
            }
            _tbFlow.ResumeLayout();
            if (_bMore.Visible != overflow) _bMore.Visible = overflow;
        }

        private void UpdateToolbarVisibility(bool active)
        {
            bool show = active && (Data.Kind == NoteKind.Text || Data.Kind == NoteKind.Toggle) && !Data.ReadOnlyLock && !IsContentLocked && !Data.RolledUp && Height > UI.S(110);
            if (_toolbar.Visible != show) _toolbar.Visible = show;
        }

        private void UpdateFormatButtons()
        {
            if (!_toolbar.Visible) return;
            try
            {
                _bBold.Checked = Tb.HasEffect(Native.CFM_BOLD);
                _bItalic.Checked = Tb.HasEffect(Native.CFM_ITALIC);
                _bUnder.Checked = Tb.HasEffect(Native.CFM_UNDERLINE);
                _bStrike.Checked = Tb.HasEffect(Native.CFM_STRIKEOUT);
                _bBullet.Checked = Tb.SelectionBullet;
            }
            catch { }
        }

        private void UpdateWordCount()
        {
            if (_lblWords == null) return;
            int words = 0, done = 0, total = 0;
            if (Data.Kind == NoteKind.Text) { words = _rtb.WordCount; _rtb.CountBoxes(out done, out total); }
            else if (Data.Kind == NoteKind.Toggle)
            {
                words = _toggle.WordCount;
                foreach (var sec in _toggle.Sections) { sec.Body.CountBoxes(out int d, out int t); done += d; total += t; }
            }
            else return;
            _lblWords.Text = total > 0 ? $"{done}/{total} done" : words + " words";
            _tt.SetToolTip(_lblWords, $"{words} words" + (total > 0 ? $"\n{done} of {total} checkboxes ticked" : ""));
        }

        private void PickTextColor()
        {
            if (Tb == null) return;
            using (var d = new ColorDialog { Color = Tb.SelectionColor, FullOpen = true })
                if (d.ShowDialog(this) == DialogResult.OK) Tb.SelectionColor = d.Color;
        }

        private void PickHighlight()
        {
            if (Tb == null) return;
            using (var d = new ColorDialog { Color = Color.Yellow, FullOpen = true })
                if (d.ShowDialog(this) == DialogResult.OK) Tb.SelectionBackColor = d.Color;
        }

        private void PickFont()
        {
            var tb = Tb;
            Font cur = tb != null ? (tb.SelectionFont ?? tb.Font) : new Font(Data.FontName ?? "Segoe UI", Data.FontSize <= 0 ? 11f : Data.FontSize);
            using (var d = new FontDialog { Font = cur, ShowColor = tb != null, Color = tb?.SelectionColor ?? Color.Black })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (tb == null || tb.TextLength == 0 || tb.SelectionLength == tb.TextLength)
                {
                    // whole note: becomes the note's default font
                    Data.FontName = d.Font.Name; Data.FontSize = d.Font.Size;
                    if (tb != null) tb.Font = d.Font;
                    _checklist.ApplyLook(_checklist.BaseColor, Data.Skin, d.Font, Data.ReadOnlyLock);
                    _toggle.ApplyLook(_toggle.BaseColor, Data.Skin, d.Font, Data.ReadOnlyLock);
                }
                if (tb != null)
                {
                    tb.SelectionFont = d.Font;
                    tb.SelectionColor = d.Color;
                }
                Changed();
            }
        }

        private void CycleAlign()
        {
            if (Tb == null) return;
            Tb.SelectionAlignment = Tb.SelectionAlignment == HorizontalAlignment.Left ? HorizontalAlignment.Center
                : Tb.SelectionAlignment == HorizontalAlignment.Center ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        private void InsertDateTime()
        {
            if (Tb != null && !Tb.ReadOnly) Tb.SelectedText = DateTime.Now.ToString("f");
        }

        private void AddPicture()
        {
            var tb = Tb;
            if (tb == null || tb.ReadOnly) return;
            using (var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", Title = "Add picture to note" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var img = Image.FromFile(d.FileName)) tb.InsertImage(img);
                    if (Height < UI.S(260)) Height = UI.S(300);
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not add picture: " + ex.Message); }
            }
        }

        private void Find(bool next)
        {
            if (IsContentLocked || (Data.Kind != NoteKind.Text && Data.Kind != NoteKind.Toggle)) return;
            if (!next || string.IsNullOrEmpty(_lastFind))
            {
                var s = UI.InputBox(this, "Find", "Find text in this note:", _lastFind);
                if (string.IsNullOrEmpty(s)) return;
                _lastFind = s;
            }
            var boxes = Data.Kind == NoteKind.Text ? new List<NoteTextBox> { _rtb } : _toggle.Sections.Select(x => x.Body).ToList();
            if (boxes.Count == 0) return;
            int ci = Math.Max(0, boxes.IndexOf(Tb));
            for (int k = 0; k <= boxes.Count; k++)
            {
                var b = boxes[(ci + k) % boxes.Count];
                int start = k == 0 ? b.SelectionStart + (next ? b.SelectionLength : 0) : 0;
                int idx = start < b.TextLength ? b.Find(_lastFind, start, RichTextBoxFinds.None) : -1;
                if (idx < 0) continue;
                if (b != _rtb) _toggle.Reveal(b);
                b.Focus();
                b.Select(idx, _lastFind.Length);
                b.ScrollToCaret();
                return;
            }
            System.Media.SystemSounds.Beep.Play();
        }

        private void TextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _textMenu.Items.Clear();
            if (Tb == null) { e.Cancel = true; return; }
            bool ro = Tb.ReadOnly;
            // spelling suggestions
            if (!ro && _rightClickIndex >= 0 && Tb.WordAt(_rightClickIndex, out int ws, out int wl))
            {
                string word = Tb.Text.Substring(ws, wl);
                var sugg = SpellService.Suggest(word);
                if (sugg != null)
                {
                    if (sugg.Count == 0) _textMenu.Items.Add(new ToolStripMenuItem("(no spelling suggestions)") { Enabled = false });
                    foreach (var sg in sugg)
                    {
                        string rep = sg;
                        var it = new ToolStripMenuItem(rep) { Font = new Font(_textMenu.Font, FontStyle.Bold) };
                        it.Click += (s2, e2) => { Tb.Select(ws, wl); Tb.SelectedText = rep; };
                        _textMenu.Items.Add(it);
                    }
                    _textMenu.Items.Add("Add to dictionary", null, (s2, e2) => { SpellService.Add(word); Tb.Invalidate(); });
                    _textMenu.Items.Add("Ignore", null, (s2, e2) => { SpellService.Ignore(word); Tb.Invalidate(); });
                    _textMenu.Items.Add(new ToolStripSeparator());
                }
            }
            _rightClickIndex = -1;
            _textMenu.Items.Add(new ToolStripMenuItem("Cut", null, (s2, e2) => Tb.Cut(), Keys.Control | Keys.X) { Enabled = !ro && Tb.SelectionLength > 0 });
            _textMenu.Items.Add(new ToolStripMenuItem("Copy", null, (s2, e2) => Tb.Copy(), Keys.Control | Keys.C) { Enabled = Tb.SelectionLength > 0 });
            _textMenu.Items.Add(new ToolStripMenuItem("Paste", null, (s2, e2) => PasteSmart(), Keys.Control | Keys.V) { Enabled = !ro });
            _textMenu.Items.Add(new ToolStripMenuItem("Paste as plain text", null, (s2, e2) => Tb.PastePlain(), Keys.Control | Keys.Shift | Keys.V) { Enabled = !ro });
            _textMenu.Items.Add(new ToolStripMenuItem("Select all", null, (s2, e2) => Tb.SelectAll(), Keys.Control | Keys.A));
            _textMenu.Items.Add(new ToolStripSeparator());
            _textMenu.Items.Add(new ToolStripMenuItem("Find…", null, (s2, e2) => Find(false), Keys.Control | Keys.F));
            _textMenu.Items.Add(new ToolStripMenuItem("Add picture…", null, (s2, e2) => AddPicture()) { Enabled = !ro });
            _textMenu.Items.Add(new ToolStripMenuItem("Insert date / time", null, (s2, e2) => InsertDateTime(), Keys.F5) { Enabled = !ro });
            _textMenu.Items.Add(new ToolStripMenuItem($"Word count: {Tb.WordCount}  •  Characters: {Tb.TextLength}") { Enabled = false });
            _textMenu.Items.Add(new ToolStripSeparator());
            _textMenu.Items.Add(new ToolStripMenuItem(Tb.SelectionLength > 0 ? "Make checkboxes (same line)" : "Insert checkbox", null, (s2, e2) => Tb.MakeCheckboxes()) { Enabled = !ro, ShortcutKeyDisplayString = "Ctrl+Shift+C" });
            Tb.CountBoxes(out int bxDone, out int bxTotal);
            if (bxTotal > 0)
            {
                string scope = Tb.SelectionLength > 0 ? " in selection" : "";
                _textMenu.Items.Add(new ToolStripMenuItem("Tick all checkboxes" + scope, null, (s2, e2) => Tb.SetAllBoxes(true)) { Enabled = !ro });
                _textMenu.Items.Add(new ToolStripMenuItem("Untick all checkboxes" + scope, null, (s2, e2) => Tb.SetAllBoxes(false)) { Enabled = !ro });
            }
            _textMenu.Items.Add(new ToolStripSeparator());
            if (Data.Kind == NoteKind.Toggle)
            {
                _textMenu.Items.Add(new ToolStripMenuItem("Add toggle below", null, (s2, e2) => _toggle.InsertAfterActive()) { Enabled = !ro, ShortcutKeyDisplayString = "Ctrl+Shift+Enter" });
                _textMenu.Items.Add(new ToolStripMenuItem("Expand all", null, (s2, e2) => _toggle.ExpandAll(true)));
                _textMenu.Items.Add(new ToolStripMenuItem("Collapse all", null, (s2, e2) => _toggle.ExpandAll(false)));
            }
            var typeMenu = new ToolStripMenuItem("Change note type") { Enabled = !ro };
            foreach (ToolStripItem it2 in BuildTypeMenu().Items.Cast<ToolStripItem>().ToList()) typeMenu.DropDownItems.Add(it2);
            _textMenu.Items.Add(typeMenu);
            e.Cancel = false;
        }

        private void PasteSmart()
        {
            if (Tb == null || Tb.ReadOnly) return;
            Tb.SafePaste();
        }

        // ---------------------------------------------------------------- note menu

        private void ShowMenu(Point screenPt)
        {
            BuildMenu();
            _menu.Show(screenPt);
        }

        private ToolStripMenuItem MI(string text, Action act, Keys keys = Keys.None, bool check = false, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { Checked = check, Enabled = enabled };
            if (keys != Keys.None) it.ShortcutKeys = keys;
            if (act != null) it.Click += (s, e) => act();
            return it;
        }

        private void BuildMenu()
        {
            _menu.Items.Clear();
            bool locked = IsContentLocked;

            var newMenu = MI("New note", null);
            newMenu.DropDownItems.Add(MI("Text note", () => _app.NewNote(NoteKind.Text, Location + new Size(UI.S(30), UI.S(30))), Keys.Control | Keys.N));
            newMenu.DropDownItems.Add(MI("Checklist note", () => _app.NewNote(NoteKind.Checklist, Location + new Size(UI.S(30), UI.S(30))), Keys.Control | Keys.Shift | Keys.N));
            newMenu.DropDownItems.Add(MI("Toggle list note", () => _app.NewNote(NoteKind.Toggle, Location + new Size(UI.S(30), UI.S(30))), Keys.Control | Keys.Shift | Keys.G));
            _menu.Items.Add(newMenu);
            _menu.Items.Add(MI("Edit title…", EditTitle, Keys.Control | Keys.F2));
            _menu.Items.Add(MI(Data.Due.Enabled ? $"Due date / alert…  ({Data.Due.At:d MMM HH:mm})" : "Due date / alert…", EditNoteDue, Keys.Control | Keys.R));
            _menu.Items.Add(new ToolStripSeparator());

            // Appearance
            var app = MI("Appearance", null);
            foreach (var (name, color) in Palette.Colors)
            {
                var c = color;
                var it = MI(name, () => { Data.Color = c; if (Data.Skin == SkinType.Antique) Data.Skin = SkinType.Plain; ApplyAppearance(); Changed(); }, check: Data.ColorArgb == c.ToArgb());
                it.Image = UI.MakeColorSwatch(c);
                app.DropDownItems.Add(it);
            }
            app.DropDownItems.Add(MI("Custom color…", () =>
            {
                using (var d = new ColorDialog { Color = Data.Color, FullOpen = true })
                    if (d.ShowDialog(this) == DialogResult.OK) { Data.Color = d.Color; ApplyAppearance(); Changed(); }
            }));
            app.DropDownItems.Add(new ToolStripSeparator());
            var skin = MI("Skin", null);
            foreach (SkinType st in Enum.GetValues(typeof(SkinType)))
            {
                var s = st;
                string label = s == SkinType.Ruled ? "Ruled paper" : s == SkinType.Notebook ? "Notebook (ruled + margin)" : s == SkinType.Grid ? "Grid paper" : s == SkinType.Dotted ? "Dotted paper" : s == SkinType.Antique ? "Antique paper" : "Plain";
                skin.DropDownItems.Add(MI(label, () => { Data.Skin = s; ApplyAppearance(); Changed(); }, check: Data.Skin == s));
            }
            app.DropDownItems.Add(skin);
            var tr = MI("Transparency", null);
            foreach (int o in new[] { 100, 90, 80, 70, 60, 50, 40 })
            {
                int op = o;
                tr.DropDownItems.Add(MI(o == 100 ? "Opaque" : $"{100 - o}% transparent", () => { Data.Opacity = op; ApplyAppearance(); Changed(); }, check: Data.Opacity == o));
            }
            app.DropDownItems.Add(tr);
            app.DropDownItems.Add(MI("Font…", PickFont));
            _menu.Items.Add(app);

            _menu.Items.Add(MI("Stick to desktop", StickToDesktop, check: Data.Mode == StickMode.Desktop));
            _menu.Items.Add(MI(Data.Mode == StickMode.Window ? $"Stick to window…  ({Data.WinProcess} {Data.WinTitle})".Trim() : "Stick to window…", StickToWindow, Keys.Control | Keys.W, Data.Mode == StickMode.Window));
            _menu.Items.Add(MI("Always on top", () => { Data.TopMost = !Data.TopMost; ApplyAppearance(); Changed(); }, check: Data.TopMost));
            var typeMi = MI("Note type", null, enabled: !locked && !Data.ReadOnlyLock);
            foreach (ToolStripItem it in BuildTypeMenu().Items.Cast<ToolStripItem>().ToList()) typeMi.DropDownItems.Add(it);
            _menu.Items.Add(typeMi);
            if (!locked && (Data.Kind == NoteKind.Toggle || (Data.Kind == NoteKind.Checklist && _checklist.HasSections)))
            {
                _menu.Items.Add(MI("Expand all", () => ExpandAll(true)));
                _menu.Items.Add(MI("Collapse all", () => ExpandAll(false)));
            }
            _menu.Items.Add(new ToolStripSeparator());

            var send = MI("Send to", null);
            send.DropDownItems.Add(MI("Clipboard (text)", () => { try { Clipboard.SetText(CurrentPlainText()); } catch { } }, enabled: !locked));
            send.DropDownItems.Add(MI("Text file…", () => ExportFile(false), enabled: !locked));
            send.DropDownItems.Add(MI("Rich text file (.rtf)…", () => ExportFile(true), enabled: !locked && Data.Kind == NoteKind.Text));
            send.DropDownItems.Add(MI("E-mail…", SendEmail, enabled: !locked));
            send.DropDownItems.Add(MI("Print…", PrintNote, enabled: !locked));
            _menu.Items.Add(send);
            _menu.Items.Add(MI(Data.Tags.Count > 0 ? $"Tags…  ({string.Join(", ", Data.Tags)})" : "Tags…", EditTags));

            var links = MI("Linked files && folders", null);
            foreach (var l in Data.Links)
            {
                string path = l;
                links.DropDownItems.Add(MI(Path.GetFileName(path.TrimEnd('\\')) is string fn && fn.Length > 0 ? fn : path, () => SafeShell.OpenLinkedPath(this, path)));
            }
            if (Data.Links.Count > 0) links.DropDownItems.Add(new ToolStripSeparator());
            links.DropDownItems.Add(MI("Link a file…", () =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Title = "Link files to this note" })
                    if (d.ShowDialog(this) == DialogResult.OK) AddLinks(d.FileNames);
            }));
            links.DropDownItems.Add(MI("Link a folder…", () =>
            {
                using (var d = new FolderBrowserDialog())
                    if (d.ShowDialog(this) == DialogResult.OK) AddLinks(new[] { d.SelectedPath });
            }));
            if (Data.Links.Count > 0) links.DropDownItems.Add(MI("Remove all links", () => { Data.Links.Clear(); Changed(); _title.Invalidate(); }));
            _menu.Items.Add(links);
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(MI("Lock (read-only)", () => { Data.ReadOnlyLock = !Data.ReadOnlyLock; ApplyAppearance(); Changed(); }, check: Data.ReadOnlyLock));
            if (!Data.Encrypted) _menu.Items.Add(MI("Encrypt with master password", Encrypt));
            else
            {
                if (locked) _menu.Items.Add(MI("Unlock…", Unlock));
                _menu.Items.Add(MI("Remove encryption", RemoveEncryption, enabled: !locked));
            }
            _menu.Items.Add(MI("Hide note", HideNote));
            _menu.Items.Add(MI("Delete note", () => _app.DeleteNote(this, true), Keys.Control | Keys.D));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MI("Notes browser…", () => _app.ShowBrowser(false), Keys.Control | Keys.B));
            _menu.Items.Add(MI("Properties", ShowProperties, Keys.Alt | Keys.Enter));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var tbx = Tb;
            bool text = tbx != null && tbx.Focused && !tbx.ReadOnly;
            switch (keyData)
            {
                case Keys.Control | Keys.N: _app.NewNote(NoteKind.Text, Location + new Size(UI.S(30), UI.S(30))); return true;
                case Keys.Control | Keys.Shift | Keys.N: _app.NewNote(NoteKind.Checklist, Location + new Size(UI.S(30), UI.S(30))); return true;
                case Keys.Control | Keys.Shift | Keys.G: _app.NewNote(NoteKind.Toggle, Location + new Size(UI.S(30), UI.S(30))); return true;
                case Keys.Control | Keys.F2: EditTitle(); return true;
                case Keys.Control | Keys.R: EditNoteDue(); return true;
                case Keys.Control | Keys.W: StickToWindow(); return true;
                case Keys.Control | Keys.D: _app.DeleteNote(this, true); return true;
                case Keys.Alt | Keys.Enter: ShowProperties(); return true;
                case Keys.Control | Keys.B when text: Tb.ToggleEffect(Native.CFM_BOLD); UpdateFormatButtons(); return true;
                case Keys.Control | Keys.I when text: Tb.ToggleEffect(Native.CFM_ITALIC); UpdateFormatButtons(); return true;
                case Keys.Control | Keys.U when text: Tb.ToggleEffect(Native.CFM_UNDERLINE); UpdateFormatButtons(); return true;
                case Keys.Control | Keys.T when text: Tb.ToggleEffect(Native.CFM_STRIKEOUT); UpdateFormatButtons(); return true;
                case Keys.Control | Keys.Shift | Keys.C when text: tbx.MakeCheckboxes(); return true;
                case Keys.Control | Keys.Space when text: if (tbx.ToggleBoxNearCaret()) return true; break;
                case Keys.Control | Keys.Shift | Keys.L when text: Tb.SelectionBullet = !Tb.SelectionBullet; return true;
                case Keys.Control | Keys.Shift | Keys.V when text: Tb.PastePlain(); return true;
                case Keys.Control | Keys.V when text: PasteSmart(); return true;
                case Keys.Control | Keys.F: Find(false); return true;
                case Keys.F3: Find(true); return true;
                case Keys.F5 when text: InsertDateTime(); return true;
                case Keys.Control | Keys.B: _app.ShowBrowser(false); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------------------------------------------------------- actions

        private string CurrentPlainText() => Data.Kind == NoteKind.Text ? _rtb.Text : Data.Kind == NoteKind.Checklist ? _checklist.PlainText : _toggle.PlainText;

        private ContextMenuStrip BuildTypeMenu()
        {
            var m = new ContextMenuStrip();
            bool can = !IsContentLocked && !Data.ReadOnlyLock;
            m.Items.Add(MI("Text note", () => ChangeKind(NoteKind.Text), check: Data.Kind == NoteKind.Text, enabled: can));
            m.Items.Add(MI("Checklist (with deadlines && sections)", () => ChangeKind(NoteKind.Checklist), check: Data.Kind == NoteKind.Checklist, enabled: can));
            m.Items.Add(MI("Toggle list (expand / collapse titles)", () => ChangeKind(NoteKind.Toggle), check: Data.Kind == NoteKind.Toggle, enabled: can));
            return m;
        }

        public void ExpandAll(bool expand)
        {
            if (IsContentLocked) return;
            if (Data.Kind == NoteKind.Toggle) _toggle.ExpandAll(expand);
            else if (Data.Kind == NoteKind.Checklist) _checklist.ExpandAll(expand);
        }

        public void EditTitle()
        {
            var t = UI.InputBox(this, "Note title", "Title of this note (leave empty to use the first line):", Data.Title);
            if (t == null) return;
            Data.Title = InputValidator.CleanLine(t, InputValidator.MaxTitle);
            _title.Invalidate(); Changed();
        }

        public void EditNoteDue()
        {
            using (var d = new DeadlineDialog(Data.DisplayTitle, Data.Due, "Due date for the whole note"))
            {
                if (d.ShowDialog(this) == DialogResult.OK && d.Result != null)
                {
                    Data.Due = d.Result; _title.Invalidate(); Changed();
                }
            }
        }

        private void StickToDesktop()
        {
            Data.Mode = StickMode.Desktop;
            AttachedHwnd = IntPtr.Zero;
            ApplyAppearance();
            if (!Visible) { Data.Hidden = false; Show(); }
            Changed();
        }

        public void StickToWindow()
        {
            using (var d = new StickToWindowDialog(Data))
            {
                var res = d.ShowDialog(this);
                if (res == DialogResult.OK)
                {
                    Data.Mode = StickMode.Window;
                    Data.WinProcess = InputValidator.CleanPattern(d.ProcessPattern);
                    Data.WinTitle = InputValidator.CleanPattern(d.TitlePattern);
                    if (d.TargetHandle != IntPtr.Zero)
                    {
                        var r = Native.GetVisibleRect(d.TargetHandle);
                        Data.WinOffsetX = Math.Max(UI.S(10), r.Width - Width - UI.S(60));
                        Data.WinOffsetY = UI.S(90);
                    }
                    AttachedHwnd = IntPtr.Zero;
                    ApplyAppearance();
                    Changed();
                    _title.Invalidate();
                    if (d.TargetHandle != IntPtr.Zero)
                    {
                        if (Native.IsIconic(d.TargetHandle)) Native.ShowWindow(d.TargetHandle, Native.SW_RESTORE);
                        Native.SetForegroundWindow(d.TargetHandle);
                    }
                }
                else if (res == DialogResult.Abort) StickToDesktop();
            }
        }

        private void ConvertKind() => ChangeKind(Data.Kind == NoteKind.Text ? NoteKind.Checklist : NoteKind.Text);

        private static ChecklistItem ParseItemLine(string l)
        {
            bool done = l.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) || l.StartsWith("☑") || l.StartsWith("✔");
            string t = l.TrimStart('•', '-', '*', '☐', '☑', '✔', '▸', ' ');
            if (t.StartsWith("[ ]") || t.StartsWith("[x]") || t.StartsWith("[X]")) t = t.Substring(3).Trim();
            return new ChecklistItem { Text = t, Done = done };
        }

        /// <summary>One line → one or more items ("☐ lec1   ☐ study" gives two items; text before the first box becomes a section).</summary>
        private static IEnumerable<ChecklistItem> ParseLine(string l)
        {
            int boxes = l.Count(NoteTextBox.IsBox);
            if (boxes < 2 && !(boxes == 1 && !NoteTextBox.IsBox(l.TrimStart()[0])))
            {
                yield return ParseItemLine(l);
                yield break;
            }
            var parts = System.Text.RegularExpressions.Regex.Split(l, "(?=[☐☑])");
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if (p.Length == 0) continue;
                if (!NoteTextBox.IsBox(p[0])) { yield return new ChecklistItem { Text = p.TrimEnd(':', ' '), IsHeading = true }; continue; }
                yield return ParseItemLine(p);
            }
        }

        private static string ItemLine(ChecklistItem i) =>
            (i.Done ? "☑ " : "☐ ") + i.Text + (i.Due.Enabled ? $"   (deadline: {i.Due.At:ddd d MMM yyyy HH:mm})" : "");

        /// <summary>Converts the note between text, checklist and toggle list, keeping the content.</summary>
        public void ChangeKind(NoteKind target)
        {
            if (IsContentLocked || Data.ReadOnlyLock || target == Data.Kind) return;
            if (Data.Kind == NoteKind.Toggle) _toggle.SyncToModel();
            var src = Data.Kind;
            var newItems = new List<ChecklistItem>();
            var newToggles = new List<ToggleItem>();
            string newRtf = "";
            _loading = true;
            try
            {
                if (target == NoteKind.Checklist)
                {
                    if (src == NoteKind.Text)
                    {
                        foreach (var raw in _rtb.Text.Split('\n'))
                        {
                            var l = raw.Trim();
                            if (l.Length == 0) continue;
                            if (l.StartsWith("## ") || l.StartsWith("■ ")) newItems.Add(new ChecklistItem { Text = l.Substring(l.IndexOf(' ') + 1).Trim(), IsHeading = true });
                            else newItems.AddRange(ParseLine(l));
                        }
                    }
                    else // toggle list: each toggle title becomes a section, each line of its content an item
                    {
                        foreach (var t in LiveToggles)
                        {
                            newItems.Add(new ChecklistItem { Text = t.Title ?? "", IsHeading = true, Collapsed = !t.Expanded });
                            foreach (var l in (t.PlainText ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
                                newItems.AddRange(ParseLine(l));
                        }
                    }
                }
                else if (target == NoteKind.Toggle)
                {
                    if (src == NoteKind.Text)
                    {
                        // blocks separated by blank lines: first line = title, the rest = content
                        ToggleItem cur = null;
                        foreach (var l in _rtb.Text.Split('\n').Select(x => x.TrimEnd('\r')))
                        {
                            if (l.Trim().Length == 0) { cur = null; continue; }
                            if (cur == null) { cur = new ToggleItem { Title = l.Trim().TrimStart('▸', '▾', '#', ' ') }; newToggles.Add(cur); }
                            else cur.PlainText += (cur.PlainText.Length > 0 ? "\n" : "") + l;
                        }
                    }
                    else // checklist
                    {
                        var items = LiveItems;
                        if (items.Any(i => i.IsHeading))
                        {
                            ToggleItem cur = null;
                            foreach (var i in items)
                            {
                                if (i.IsHeading) { cur = new ToggleItem { Title = i.Text, Expanded = !i.Collapsed }; newToggles.Add(cur); continue; }
                                if (cur == null) { cur = new ToggleItem { Title = "Tasks" }; newToggles.Add(cur); }
                                cur.PlainText += (cur.PlainText.Length > 0 ? "\n" : "") + ItemLine(i);
                            }
                        }
                        else
                            foreach (var i in items) newToggles.Add(new ToggleItem { Title = i.Text, Expanded = false });
                    }
                    if (newToggles.Count == 0) newToggles.Add(new ToggleItem());
                }
                else // -> text
                {
                    _rtb.Clear();
                    try { _rtb.Font = new Font(Data.FontName, Data.FontSize); } catch { }
                    var bold = new List<(int start, int len)>();
                    var sb = new System.Text.StringBuilder();
                    if (src == NoteKind.Checklist)
                    {
                        foreach (var i in LiveItems)
                        {
                            if (i.IsHeading)
                            {
                                if (sb.Length > 0) sb.Append("\n");
                                bold.Add((sb.Length, i.Text.Length));
                                sb.Append(i.Text).Append("\n");
                            }
                            else sb.Append(ItemLine(i)).Append("\n");
                        }
                    }
                    else
                    {
                        foreach (var t in LiveToggles)
                        {
                            if (sb.Length > 0) sb.Append("\n");
                            bold.Add((sb.Length, (t.Title ?? "").Length));
                            sb.Append(t.Title ?? "").Append("\n");
                            if (!string.IsNullOrWhiteSpace(t.PlainText)) sb.Append(t.PlainText.Replace("\r", "").TrimEnd('\n')).Append("\n");
                        }
                    }
                    _rtb.Text = sb.ToString().TrimEnd('\n');
                    foreach (var (st, len) in bold)
                    {
                        if (len == 0 || st + len > _rtb.TextLength) continue;
                        _rtb.Select(st, len);
                        _rtb.SelectionFont = new Font(_rtb.Font, FontStyle.Bold);
                    }
                    _rtb.Select(0, 0);
                    _rtb.RefreshBoxFormatting();
                    newRtf = _rtb.Rtf;
                }

                Data.Kind = target;
                if (Data.Encrypted)
                {
                    _decryptedItems = newItems; _decryptedToggles = newToggles; _decryptedRtf = newRtf;
                }
                else
                {
                    Data.Items = newItems; Data.Toggles = newToggles; Data.Rtf = newRtf;
                }
            }
            finally { _loading = false; }
            LoadContent();
            ApplyAppearance();
            Changed();
            FocusEditor();
        }

        private void ExportFile(bool rtf)
        {
            using (var d = new SaveFileDialog { Filter = rtf ? "Rich text|*.rtf" : "Text file|*.txt", FileName = MakeSafeFileName(Data.DisplayTitle) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (rtf) _rtb.SaveFile(d.FileName, RichTextBoxStreamType.RichText);
                    else File.WriteAllText(d.FileName, CurrentPlainText().Replace("\n", "\r\n"));
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            }
        }

        private static string MakeSafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        private void SendEmail()
        {
            try
            {
                string body = CurrentPlainText();
                if (body.Length > 1800) body = body.Substring(0, 1800) + "…";
                Process.Start("mailto:?subject=" + Uri.EscapeDataString(Data.DisplayTitle) + "&body=" + Uri.EscapeDataString(body.Replace("\n", "\r\n")));
            }
            catch (Exception ex) { MessageBox.Show(this, "No e-mail program found: " + ex.Message); }
        }

        private void PrintNote()
        {
            string text = CurrentPlainText();
            var doc = new System.Drawing.Printing.PrintDocument { DocumentName = Data.DisplayTitle };
            int pos = 0;
            doc.PrintPage += (s, e) =>
            {
                using (var f = new Font(Data.FontName ?? "Segoe UI", 11f))
                using (var tf = new Font("Segoe UI", 14f, FontStyle.Bold))
                {
                    var r = e.MarginBounds;
                    if (pos == 0) { e.Graphics.DrawString(Data.DisplayTitle, tf, Brushes.Black, r.Left, r.Top); r.Y += 40; r.Height -= 40; }
                    string rest = text.Substring(pos);
                    e.Graphics.MeasureString(rest, f, r.Size, StringFormat.GenericTypographic, out int chars, out _);
                    e.Graphics.DrawString(rest.Substring(0, chars), f, Brushes.Black, r, StringFormat.GenericTypographic);
                    pos += Math.Max(1, chars);
                    e.HasMorePages = pos < text.Length;
                }
            };
            using (var pd = new PrintDialog { Document = doc, UseEXDialog = true })
                if (pd.ShowDialog(this) == DialogResult.OK) { try { doc.Print(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } }
        }

        /// <summary>Adds validated file / folder links (drag-drop and dialogs).</summary>
        private void AddLinks(IEnumerable<string> paths)
        {
            int rejected = 0;
            foreach (var p in paths ?? new string[0])
            {
                if (Data.Links.Count >= InputValidator.MaxLinks) { rejected++; continue; }
                if (!InputValidator.TryValidatePath(p, out var full, out _)) { rejected++; continue; }
                if (!Data.Links.Contains(full, StringComparer.OrdinalIgnoreCase)) Data.Links.Add(full);
            }
            if (rejected > 0) MessageBox.Show(this, $"{rejected} item(s) could not be linked (invalid path or too many links).", "Linked files", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Changed(); _title.Invalidate();
        }

        private void EditTags()
        {
            using (var d = new TagsDialog(Data.Tags, _app.AllTags()))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    Data.Tags = d.Tags.Select(x => InputValidator.CleanLine(x, InputValidator.MaxTag)).Where(x => x.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(InputValidator.MaxTags).ToList();
                    Changed();
                }
            }
        }

        private void Encrypt()
        {
            if (!_app.EnsureMasterKey(this, true)) return;
            SyncToData();
            _decryptedRtf = Data.Rtf;
            _decryptedItems = Data.Items.Select(i => i).ToList();
            _decryptedToggles = Data.Toggles.Select(i => i).ToList();
            Data.Encrypted = true;
            _decrypted = true;
            if (Data.Kind == NoteKind.Checklist) _checklist.Bind(_decryptedItems);
            if (Data.Kind == NoteKind.Toggle) _toggle.Bind(_decryptedToggles);
            SyncToData();
            _title.Invalidate();
            _app.MarkDirty();
            UI.Info(this, "This note is now encrypted with your master password.\nUse \"Lock encrypted notes\" in the tray menu to lock it again.");
        }

        private void RemoveEncryption()
        {
            if (IsContentLocked) return;
            SyncToData();
            Data.Encrypted = false;
            Data.EncryptedPayload = "";
            Data.Rtf = _decryptedRtf;
            Data.Items = _decryptedItems ?? new List<ChecklistItem>();
            Data.Toggles = _decryptedToggles ?? new List<ToggleItem>();
            _decrypted = false; _decryptedItems = null; _decryptedToggles = null;
            LoadContent();
            SyncToData();
            _title.Invalidate();
            _app.MarkDirty();
        }

        public void Unlock()
        {
            if (!Data.Encrypted || _decrypted) return;
            if (!_app.EnsureMasterKey(this, false)) return;
            try
            {
                var c = Storage.DeserializeContent(Crypto.Decrypt(Data.EncryptedPayload, _app.MasterKey));
                _decryptedRtf = c.Rtf ?? "";
                _decryptedItems = c.Items ?? new List<ChecklistItem>();
                foreach (var i in _decryptedItems) i.Due = i.Due ?? new Deadline();
                _decryptedToggles = c.Toggles ?? new List<ToggleItem>();
                _decrypted = true;
                LoadContent();
                ApplyAppearance();
            }
            catch { MessageBox.Show(this, "Could not decrypt this note (wrong master password?)."); }
        }

        /// <summary>Re-lock an unlocked encrypted note (content is saved first).</summary>
        public void Relock()
        {
            if (!Data.Encrypted || !_decrypted) return;
            SyncToData();
            _decrypted = false; _decryptedItems = null; _decryptedRtf = ""; _decryptedToggles = null;
            _rtb.Clear();
            _checklist.Bind(new List<ChecklistItem>());
            _toggle.Bind(new List<ToggleItem>());
            LoadContent();
            ApplyAppearance();
        }

        private void ShowProperties()
        {
            string where = Data.Mode == StickMode.Window
                ? $"Stuck to window: process \"{Data.WinProcess}\", title \"{Data.WinTitle}\""
                : "Stuck to desktop";
            var realItems = LiveItems.Where(i => !i.IsHeading).ToList();
            int words = Data.Kind == NoteKind.Text ? _rtb.WordCount : Data.Kind == NoteKind.Toggle ? LiveToggles.Count : realItems.Count;
            string info =
                $"Title: {Data.DisplayTitle}\n" +
                $"Type: {(Data.Kind == NoteKind.Text ? "Text note" : Data.Kind == NoteKind.Toggle ? "Toggle list note" : "Checklist note")}\n" +
                $"Created: {Data.Created:f}\nModified: {Data.Modified:f}\n" +
                $"{where}\n" +
                $"{(Data.Kind == NoteKind.Text ? "Words" : Data.Kind == NoteKind.Toggle ? "Toggles" : "Items")}: {words}" +
                (Data.Kind == NoteKind.Checklist ? $"  (done: {realItems.Count(i => i.Done)}, with deadline: {realItems.Count(i => i.Due.Enabled)}, sections: {LiveItems.Count(i => i.IsHeading)})" : "") + "\n" +
                $"Tags: {(Data.Tags.Count == 0 ? "-" : string.Join(", ", Data.Tags))}\n" +
                $"Due date: {(Data.Due.Enabled ? Data.Due.At.ToString("f") + " (" + Data.Due.AlertText() + ")" : "-")}\n" +
                $"Encrypted: {(Data.Encrypted ? "yes" : "no")}   Read-only: {(Data.ReadOnlyLock ? "yes" : "no")}\n" +
                $"Linked files: {Data.Links.Count}";
            MessageBox.Show(this, info, "Note properties", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void HideNote()
        {
            Data.Hidden = true;
            Hide();
            _app.MarkDirty();
        }

        public void ShowNote(bool activate)
        {
            Data.Hidden = false;
            if (Data.RolledUp && activate) ToggleRoll();
            EnsureOnScreen();
            if (!Visible) Show();
            if (activate)
            {
                Activate();
                BringToFront();
                if (!IsContentLocked) FocusEditor();
            }
            _app.MarkDirty();
        }

        public void FocusEditor()
        {
            if (IsContentLocked) return;
            if (Data.Kind == NoteKind.Text) _rtb.Focus();
            else if (Data.Kind == NoteKind.Toggle) _toggle.FocusFirst();
            else _checklist.FocusFirst();
        }

        public void ToggleRoll()
        {
            if (Data.RolledUp)
            {
                Data.RolledUp = false;
                Height = Math.Max(UI.S(80), Data.H);
            }
            else
            {
                Data.H = Height;
                Data.RolledUp = true;
                Height = TitleH + Border * 2;
            }
            UpdateToolbarVisibility(Form.ActiveForm == this);
            _app.MarkDirty();
        }

        // ---------------------------------------------------------------- dragging & resizing

        private void Title_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                base.WndProc(ref m);
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                int g = Math.Max(Border + 2, UI.S(6));
                bool l = p.X <= g, r = p.X >= ClientSize.Width - g, t = p.Y <= g, b = p.Y >= ClientSize.Height - g;
                bool corner = p.X >= ClientSize.Width - UI.S(14) && p.Y >= ClientSize.Height - UI.S(14);
                if (Data.RolledUp) { if (l) m.Result = (IntPtr)Native.HTLEFT; else if (r) m.Result = (IntPtr)Native.HTRIGHT; return; }
                if (t && l) m.Result = (IntPtr)Native.HTTOPLEFT;
                else if (t && r) m.Result = (IntPtr)Native.HTTOPRIGHT;
                else if ((b && r) || (corner && (b || r))) m.Result = (IntPtr)Native.HTBOTTOMRIGHT;
                else if (b && l) m.Result = (IntPtr)Native.HTBOTTOMLEFT;
                else if (l) m.Result = (IntPtr)Native.HTLEFT;
                else if (r) m.Result = (IntPtr)Native.HTRIGHT;
                else if (t) m.Result = (IntPtr)Native.HTTOP;
                else if (b) m.Result = (IntPtr)Native.HTBOTTOM;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_app.Exiting)
            {
                e.Cancel = true;
                HideNote();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    internal class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }
}
