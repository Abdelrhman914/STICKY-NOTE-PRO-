using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>Popup shown when a deadline alert fires.</summary>
    internal class ReminderForm : Form
    {
        public readonly string Key;
        private readonly AppController _app;
        private readonly NoteForm _note;
        private readonly ChecklistItem _item;   // null = whole-note due date
        private readonly Deadline _due;
        private readonly ComboBox _cmbSnooze;
        private static readonly (string, TimeSpan)[] SnoozeOptions =
        {
            ("5 minutes", TimeSpan.FromMinutes(5)), ("10 minutes", TimeSpan.FromMinutes(10)), ("15 minutes", TimeSpan.FromMinutes(15)),
            ("30 minutes", TimeSpan.FromMinutes(30)), ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2)),
            ("4 hours", TimeSpan.FromHours(4)), ("Tomorrow (1 day)", TimeSpan.FromDays(1)), ("2 days", TimeSpan.FromDays(2)), ("1 week", TimeSpan.FromDays(7)),
        };

        public ReminderForm(AppController app, NoteForm note, ChecklistItem item, Deadline due, string headline, string key)
        {
            _app = app; _note = note; _item = item; _due = due; Key = key;
            Text = "StickyNotes Pro Reminder";
            Icon = AppController.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = true;
            TopMost = true; ShowInTaskbar = true; StartPosition = FormStartPosition.Manual; Font = UI.UiFont;
            BackColor = Color.FromArgb(245, 245, 245);
            ClientSize = new Size(UI.S(470), UI.S(290));

            Color noteColor = note.Data.Skin == SkinType.Antique ? Palette.Antique : note.Data.Color;
            var paper = new Panel { Left = UI.S(14), Top = UI.S(14), Width = UI.S(300), Height = UI.S(212), BackColor = noteColor, Padding = new Padding(UI.S(10)) };
            paper.Paint += (s, e) => { using (var p = new Pen(UI.Darken(noteColor, 0.25f))) e.Graphics.DrawRectangle(p, 0, 0, paper.Width - 1, paper.Height - 1); };

            due.StatusText(item?.Done ?? false, out Color statusColor);
            var lblHead = new Label { Text = headline, Dock = DockStyle.Top, Height = UI.S(26), Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = statusColor, AutoEllipsis = true };
            string task = item != null ? item.Text : note.Data.DisplayTitle;
            var lblTask = new Label { Text = task, Dock = DockStyle.Top, Height = UI.S(64), Font = new Font(note.Data.FontName ?? "Segoe UI", 12f), AutoEllipsis = true };
            int daysLeft = due.DaysLeft;
            string daysTxt = due.At < DateTime.Now ? $"Deadline passed {(DateTime.Now - due.At).TotalDays:0} day(s) ago"
                : daysLeft == 0 ? $"Due today – {(due.At - DateTime.Now).TotalHours:0.#} hours left"
                : $"{daysLeft} day{(daysLeft == 1 ? "" : "s")} left to do this work";
            var lblInfo = new Label
            {
                Text = $"Deadline: {due.At:dddd, d MMMM yyyy  HH:mm}\n{daysTxt}\n" + (item != null ? $"Note: {note.Data.DisplayTitle}" : ""),
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(60, 60, 60)
            };
            paper.Controls.Add(lblInfo);
            paper.Controls.Add(lblTask);
            paper.Controls.Add(lblHead);

            int bx = UI.S(326), bw = UI.S(130), by = UI.S(14), bh = UI.S(34), gap = UI.S(8);
            Button B(string text, string glyph, Action act)
            {
                var b = new Button { Text = text, Left = bx, Top = by, Width = bw, Height = bh, FlatStyle = FlatStyle.Flat, BackColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(UI.S(4), 0, 0, 0) };
                b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                b.Text = "      " + text;
                b.Paint += (s, e) => TextRenderer.DrawText(e.Graphics, glyph, UI.IconFont(10f), new Rectangle(UI.S(8), 0, UI.S(20), b.Height), Color.FromArgb(50, 50, 50), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                b.Click += (s, e) => act();
                Controls.Add(b);
                by += bh + gap;
                return b;
            }
            B("Go to note", UI.GWindow, () => { _app.GoToNote(_note); Close(); });
            if (item != null) B("Mark as done", UI.GCheck, () => { _item.Done = true; _item.DoneOn = DateTime.Now; _note.RefreshItem(_item); _app.MarkDirty(); Close(); });
            else B("Delete note", UI.GDelete, () => { Close(); _app.DeleteNote(_note, true); });
            B("Reschedule…", UI.GCalendar, Reschedule);
            B("Browse all", UI.GList, () => { _app.ShowBrowser(true); Close(); });

            var sep = new Label { Left = UI.S(14), Top = UI.S(238), Width = UI.S(442), Height = 1, BorderStyle = BorderStyle.Fixed3D };
            _cmbSnooze = new ComboBox { Left = UI.S(14), Top = UI.S(252), Width = UI.S(150), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var o in SnoozeOptions) _cmbSnooze.Items.Add(o.Item1);
            _cmbSnooze.SelectedIndex = 0;
            var snooze = new Button { Text = "Snooze", Left = UI.S(200), Top = UI.S(249), Width = UI.S(120), Height = UI.S(30), FlatStyle = FlatStyle.System };
            var dismiss = new Button { Text = "Dismiss", Left = UI.S(336), Top = UI.S(249), Width = UI.S(120), Height = UI.S(30), FlatStyle = FlatStyle.System };
            snooze.Click += (s, e) =>
            {
                _due.SnoozeUntil = DateTime.Now + SnoozeOptions[_cmbSnooze.SelectedIndex].Item2;
                _app.MarkDirty();
                Close();
            };
            dismiss.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { paper, sep, _cmbSnooze, snooze, dismiss });
            AcceptButton = dismiss;

            var wa = Screen.PrimaryScreen.WorkingArea;
            int offset = app.OpenReminderCount * UI.S(24);
            Location = new Point(wa.Right - Width - UI.S(16) - offset, wa.Bottom - Height - UI.S(16) - offset);
        }

        private void Reschedule()
        {
            using (var d = new DeadlineDialog(_item?.Text ?? _note.Data.DisplayTitle, _due, _item != null ? "Reschedule task" : "Reschedule note"))
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.Result == null) return;
                if (_item != null) { _item.Due = d.Result; _note.RefreshItem(_item); }
                else _note.Data.Due = d.Result;
                _note.RefreshDeadlines();
                _app.MarkDirty();
                Close();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_app.Data.Settings.PlaySound) System.Media.SystemSounds.Exclamation.Play();
        }
    }
}
