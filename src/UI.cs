using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace StickyNotesPro
{
    internal static class UI
    {
        public static float Scale = 1f;
        public static int S(int v) => (int)Math.Round(v * Scale);
        public static int S(double v) => (int)Math.Round(v * Scale);

        public static readonly string IconFontName = DetectIconFont();
        private static readonly Dictionary<float, Font> _iconFonts = new Dictionary<float, Font>();

        private static string DetectIconFont()
        {
            using (var ifc = new System.Drawing.Text.InstalledFontCollection())
            {
                var names = ifc.Families.Select(f => f.Name).ToList();
                if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
                if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
            }
            return "Segoe UI Symbol";
        }

        public static Font IconFont(float size)
        {
            if (!_iconFonts.TryGetValue(size, out var f))
            {
                f = new Font(IconFontName, size, FontStyle.Regular, GraphicsUnit.Point);
                _iconFonts[size] = f;
            }
            return f;
        }

        public static readonly Font UiFont = new Font("Segoe UI", 9f);
        public static readonly Font UiFontBold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font SmallFont = new Font("Segoe UI", 8f);

        // MDL2 glyphs
        public const string GMenu = "\uE700", GAdd = "\uE710", GPin = "\uE718", GPinned = "\uE840", GMinimize = "\uE921",
            GClose = "\uE8BB", GCalendar = "\uE787", GDelete = "\uE74D", GCancel = "\uE711", GAlarm = "\uEA8F",
            GLock = "\uE72E", GList = "\uE8FD", GFontColor = "\uE8D3", GHighlight = "\uE7E6", GFont = "\uE8D2",
            GAlignLeft = "\uE8E4", GAlignCenter = "\uE8E3", GAlignRight = "\uE8E2", GClock = "\uE823", GPicture = "\uEB9F",
            GCheckList = "\uE762", GTag = "\uE8EC", GLink = "\uE71B", GWindow = "\uE737", GSearch = "\uE721", GSettings = "\uE713",
            GBell = "\uEA8F", GCheck = "\uE73E", GStar = "\uE734", GStarFill = "\uE735", GBold = "\uE8DD", GItalic = "\uE8DB",
            GUnderline = "\uE8DC", GStrike = "\uEDE0";

        public static Color Darken(Color c, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(c.A, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));
        }

        public static Color Lighten(Color c, float amount)
        {
            return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));
        }

        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Button MakeButton(string text, int width = 90)
        {
            return new Button { Text = text, Width = S(width), Height = S(28), FlatStyle = FlatStyle.System, Font = UiFont };
        }

        public static Label MakeLabel(string text, bool bold = false)
        {
            return new Label { Text = text, AutoSize = true, Font = bold ? UiFontBold : UiFont };
        }

        public static string InputBox(IWin32Window owner, string title, string prompt, string value = "", bool password = false)
        {
            using (var f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
                f.ShowInTaskbar = owner == null; f.TopMost = true; f.Font = UiFont;
                f.ClientSize = new Size(S(380), S(130));
                var lbl = new Label { Text = prompt, Left = S(12), Top = S(12), Width = S(356), Height = S(36) };
                var tb = new TextBox { Left = S(12), Top = S(50), Width = S(356), Text = value ?? "" };
                if (password) tb.UseSystemPasswordChar = true;
                var ok = MakeButton("OK", 80); ok.Left = S(200); ok.Top = S(90); ok.DialogResult = DialogResult.OK;
                var cancel = MakeButton("Cancel", 80); cancel.Left = S(288); cancel.Top = S(90); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => { tb.Focus(); tb.SelectAll(); };
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        /// <summary>Grey placeholder text in an empty TextBox.</summary>
        public static void SetCue(TextBox tb, string cue)
        {
            try { SendMessage(tb.Handle, 0x1501, (IntPtr)1, cue); } catch { }
        }

        public static bool Confirm(IWin32Window owner, string text, string title = "StickyNotes Pro")
        {
            return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public static void Info(IWin32Window owner, string text, string title = "StickyNotes Pro")
        {
            MessageBox.Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static Bitmap MakeColorSwatch(Color c, int size = 14)
        {
            var b = new Bitmap(S(size), S(size));
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(c);
                g.DrawRectangle(Pens.Gray, 0, 0, b.Width - 1, b.Height - 1);
            }
            return b;
        }
    }

    /// <summary>Flat button that shows an icon-font glyph (or text) with a hover highlight.</summary>
    internal class GlyphButton : Control
    {
        private bool _hover, _down;
        public bool Checked { get => _checked; set { _checked = value; Invalidate(); } }
        private bool _checked;
        public Color HoverColor = Color.FromArgb(45, 0, 0, 0);

        public GlyphButton(string glyph, string tooltip, ToolTip tt, float size = 10f, bool iconFont = true)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Text = glyph;
            Font = iconFont ? UI.IconFont(size) : new Font("Segoe UI", size, FontStyle.Bold);
            Size = new Size(UI.S(24), UI.S(22));
            Cursor = Cursors.Hand;
            ForeColor = Color.FromArgb(60, 60, 60);
            if (tt != null && !string.IsNullOrEmpty(tooltip)) tt.SetToolTip(this, tooltip);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasDown = _down;
            _down = false; Invalidate(); base.OnMouseUp(e);
            if (wasDown && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover || _checked || _down)
            {
                using (var path = UI.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), UI.S(3)))
                using (var b = new SolidBrush(_down ? Color.FromArgb(80, 0, 0, 0) : (_checked && !_hover ? Color.FromArgb(35, 0, 0, 0) : HoverColor)))
                    g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
