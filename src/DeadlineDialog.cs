using System;
using System.Drawing;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>Dialog for setting a deadline + alert (used for checklist items and for whole notes).</summary>
    internal class DeadlineDialog : Form
    {
        public Deadline Result { get; private set; }
        public bool Removed { get; private set; }

        private readonly CheckBox _chkEnable, _chkAlert, _chkAtTime, _chkEmail;
        private readonly DateTimePicker _date, _time;
        private readonly ComboBox _cmbBefore;
        private readonly Label _lblDays, _lblAlertWhen;
        private readonly Deadline _work;
        private static readonly int[] BeforeDays = { 0, 1, 2, 3, 4, 5, 7 };

        public DeadlineDialog(string taskText, Deadline current, string header = "Deadline for task")
        {
            _work = (current ?? new Deadline()).Clone();
            bool isNew = !_work.Enabled;
            if (isNew)
            {
                _work.At = DateTime.Today.AddDays(3).AddHours(18);
                _work.Alert = true;
                _work.AlertAtTime = true;
                _work.DaysBefore = AppController.I?.Data.Settings.DefaultAlertDays ?? 1;
            }

            Text = "Deadline & alert";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; TopMost = true;
            Font = UI.UiFont; BackColor = Color.White;
            ClientSize = new Size(UI.S(460), UI.S(440));

            int x = UI.S(16), w = UI.S(428), y = UI.S(12);

            var lblHeader = new Label { Text = header, Left = x, Top = y, AutoSize = true, ForeColor = Color.Gray };
            y += UI.S(20);
            string t = string.IsNullOrWhiteSpace(taskText) ? "(untitled)" : taskText.Trim();
            if (t.Length > 80) t = t.Substring(0, 80) + "…";
            var lblTask = new Label { Text = t, Left = x, Top = y, Width = w, Height = UI.S(24), Font = new Font("Segoe UI", 11f, FontStyle.Bold), AutoEllipsis = true };
            y += UI.S(32);

            _chkEnable = new CheckBox { Text = "This task has a deadline", Left = x, Top = y, AutoSize = true, Checked = true, Font = UI.UiFontBold };
            y += UI.S(28);

            var lblDate = new Label { Text = "Deadline:", Left = x + UI.S(18), Top = y + UI.S(4), AutoSize = true };
            _date = new DateTimePicker { Left = x + UI.S(90), Top = y, Width = UI.S(210), Format = DateTimePickerFormat.Long, Value = _work.At.Date };
            _time = new DateTimePicker { Left = x + UI.S(308), Top = y, Width = UI.S(90), Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Today.Add(_work.At.TimeOfDay) };
            y += UI.S(34);

            var quick = new FlowLayoutPanel { Left = x + UI.S(16), Top = y, Width = w - UI.S(16), Height = UI.S(30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            void AddQuick(string text, Func<DateTime> f)
            {
                var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, Height = UI.S(24), Margin = new Padding(0, 0, UI.S(4), 0), BackColor = Color.FromArgb(240, 240, 240), Font = UI.SmallFont };
                b.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
                b.Click += (s, e) => { _date.Value = f(); _chkEnable.Checked = true; };
                quick.Controls.Add(b);
            }
            AddQuick("Today", () => DateTime.Today);
            AddQuick("Tomorrow", () => DateTime.Today.AddDays(1));
            AddQuick("+2 days", () => DateTime.Today.AddDays(2));
            AddQuick("+3 days", () => DateTime.Today.AddDays(3));
            AddQuick("+1 week", () => DateTime.Today.AddDays(7));
            AddQuick("+1 month", () => DateTime.Today.AddMonths(1));
            y += UI.S(36);

            _lblDays = new Label { Left = x, Top = y, Width = w, Height = UI.S(46), Font = new Font("Segoe UI", 10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(UI.S(8), 0, 0, 0), BackColor = Color.FromArgb(245, 248, 255) };
            y += UI.S(58);

            var grp = new GroupBox { Text = "Alert", Left = x, Top = y, Width = w, Height = UI.S(170) };
            int gy = UI.S(22), gx = UI.S(12);
            _chkAlert = new CheckBox { Text = "Alert me with a popup reminder", Left = gx, Top = gy, AutoSize = true, Checked = _work.Alert };
            gy += UI.S(30);
            var lblBefore = new Label { Text = "First alert:", Left = gx + UI.S(18), Top = gy + UI.S(4), AutoSize = true };
            _cmbBefore = new ComboBox { Left = gx + UI.S(100), Top = gy, Width = UI.S(170), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbBefore.Items.AddRange(new object[] { "No early alert", "1 day before", "2 days before", "3 days before", "4 days before", "5 days before", "1 week before" });
            int idx = Array.IndexOf(BeforeDays, _work.DaysBefore);
            _cmbBefore.SelectedIndex = idx < 0 ? 1 : idx;
            gy += UI.S(32);
            _chkAtTime = new CheckBox { Text = "Also alert when the deadline arrives", Left = gx + UI.S(18), Top = gy, AutoSize = true, Checked = _work.AlertAtTime };
            gy += UI.S(26);
            _chkEmail = new CheckBox { Text = "Also send the alert to my e-mail (set up in Settings)", Left = gx + UI.S(18), Top = gy, AutoSize = true, Checked = _work.Email };
            gy += UI.S(28);
            _lblAlertWhen = new Label { Left = gx + UI.S(18), Top = gy, Width = w - UI.S(40), Height = UI.S(20), ForeColor = Color.FromArgb(0, 90, 170) };
            grp.Controls.AddRange(new Control[] { _chkAlert, lblBefore, _cmbBefore, _chkAtTime, _chkEmail, _lblAlertWhen });
            y += UI.S(182);

            var ok = UI.MakeButton("OK", 90); ok.Left = ClientSize.Width - UI.S(16) - ok.Width; ok.Top = y;
            var cancel = UI.MakeButton("Cancel", 90); cancel.Left = ok.Left - UI.S(8) - cancel.Width; cancel.Top = y; cancel.DialogResult = DialogResult.Cancel;
            var remove = UI.MakeButton("Remove deadline", 130); remove.Left = x; remove.Top = y; remove.Visible = !isNew;
            ok.Click += (s, e) => { Commit(); DialogResult = DialogResult.OK; };
            remove.Click += (s, e) => { Removed = true; Result = _work.Clone(); Result.Enabled = false; DialogResult = DialogResult.OK; };

            Controls.AddRange(new Control[] { lblHeader, lblTask, _chkEnable, lblDate, _date, _time, quick, _lblDays, grp, ok, cancel, remove });
            AcceptButton = ok; CancelButton = cancel;

            EventHandler upd = (s, e) => UpdateInfo();
            _chkEnable.CheckedChanged += upd; _chkAlert.CheckedChanged += upd; _chkAtTime.CheckedChanged += upd;
            _date.ValueChanged += upd; _time.ValueChanged += upd; _cmbBefore.SelectedIndexChanged += upd;
            UpdateInfo();
        }

        private DateTime Selected => _date.Value.Date.Add(_time.Value.TimeOfDay);

        private void UpdateInfo()
        {
            bool en = _chkEnable.Checked;
            _date.Enabled = _time.Enabled = _chkAlert.Enabled = en;
            bool al = en && _chkAlert.Checked;
            _cmbBefore.Enabled = _chkAtTime.Enabled = _chkEmail.Enabled = al;

            if (!en) { _lblDays.Text = "No deadline"; _lblDays.ForeColor = Color.Gray; _lblAlertWhen.Text = ""; return; }

            var dt = Selected;
            int days = (dt.Date - DateTime.Today).Days;
            string range = $"(today {DateTime.Today:ddd d MMM} → {dt:ddd d MMM yyyy, HH:mm})";
            if (dt < DateTime.Now) { _lblDays.Text = $"⚠ This deadline has already passed\n{range}"; _lblDays.ForeColor = Color.FromArgb(200, 30, 30); }
            else if (days == 0) { _lblDays.Text = $"Due TODAY — you have {(dt - DateTime.Now).TotalHours:0.#} hours left\n{range}"; _lblDays.ForeColor = Color.FromArgb(215, 95, 0); }
            else
            {
                _lblDays.Text = $"You have {days} day{(days == 1 ? "" : "s")} to finish this work\n{range}";
                _lblDays.ForeColor = days <= 3 ? Color.FromArgb(190, 110, 0) : Color.FromArgb(20, 120, 50);
            }

            if (!al) { _lblAlertWhen.Text = "No popup alert for this deadline."; return; }
            int before = BeforeDays[Math.Max(0, _cmbBefore.SelectedIndex)];
            var parts = new System.Collections.Generic.List<string>();
            if (before > 0) parts.Add("⏰ " + dt.AddDays(-before).ToString("ddd d MMM, HH:mm"));
            if (_chkAtTime.Checked) parts.Add("⏰ " + dt.ToString("ddd d MMM, HH:mm"));
            _lblAlertWhen.Text = parts.Count == 0 ? "No alert time selected." : "Alerts: " + string.Join("   ", parts);
        }

        private void Commit()
        {
            var r = _work.Clone();
            r.Enabled = _chkEnable.Checked;
            var newAt = Selected;
            int newBefore = BeforeDays[Math.Max(0, _cmbBefore.SelectedIndex)];
            bool changed = !(_work.Enabled && _work.At == newAt && _work.DaysBefore == newBefore && _work.AlertAtTime == _chkAtTime.Checked && _work.Alert == _chkAlert.Checked);
            r.At = newAt;
            r.Alert = _chkAlert.Checked;
            r.DaysBefore = newBefore;
            r.AlertAtTime = _chkAtTime.Checked;
            r.Email = _chkEmail.Checked;
            if (r.SetOn == default || !_work.Enabled) r.SetOn = DateTime.Today;
            if (changed || !_work.Enabled) r.ResetAlerts();
            Result = r;
        }
    }
}
