using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StickyNotesPro
{
    /// <summary>Choose which program / window a note is attached to.</summary>
    internal class StickToWindowDialog : Form
    {
        public string ProcessPattern => _txtProc.Text.Trim();
        public string TitlePattern => _txtTitle.Text.Trim();
        public IntPtr TargetHandle { get; private set; }

        private readonly ListView _list;
        private readonly TextBox _txtProc, _txtTitle;

        public StickToWindowDialog(NoteData note)
        {
            Text = "Stick note to a window";
            FormBorderStyle = FormBorderStyle.Sizable; MinimizeBox = false; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; TopMost = true; Font = UI.UiFont;
            ClientSize = new Size(UI.S(620), UI.S(480));
            MinimumSize = new Size(UI.S(500), UI.S(420));

            var lbl = new Label
            {
                Text = "The note will automatically appear on top of the window whenever that window is active, and hide when you switch away.\n" +
                       "1) Pick an open window below (or type the rules yourself).  2) Optionally use * wildcards, e.g. title  *YouTube*  or  *.docx*",
                Left = UI.S(12), Top = UI.S(10), Width = UI.S(596), Height = UI.S(66), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _list = new ListView
            {
                Left = UI.S(12), Top = UI.S(80), Width = UI.S(596), Height = UI.S(232), View = View.Details, FullRowSelect = true,
                HideSelection = false, MultiSelect = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            _list.Columns.Add("Window title", UI.S(420));
            _list.Columns.Add("Program", UI.S(150));
            foreach (var w in Native.GetOpenWindows())
                _list.Items.Add(new ListViewItem(new[] { w.Title, w.Process }) { Tag = w });

            int y = UI.S(322);
            var l1 = new Label { Text = "Program (process):", Left = UI.S(12), Top = y + UI.S(3), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _txtProc = new TextBox { Left = UI.S(150), Top = y, Width = UI.S(200), Text = note.WinProcess, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var l1b = new Label { Text = "e.g. chrome.exe, msedge.exe, WINWORD.EXE, explorer.exe  (empty = any program)", Left = UI.S(150), Top = y + UI.S(24), AutoSize = true, ForeColor = Color.Gray, Font = UI.SmallFont, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            y += UI.S(50);
            var l2 = new Label { Text = "Window title contains:", Left = UI.S(12), Top = y + UI.S(3), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _txtTitle = new TextBox { Left = UI.S(150), Top = y, Width = UI.S(458), Text = note.WinTitle, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var l2b = new Label { Text = "Use * as wildcard. \"Google\" = any window whose title contains Google. Empty = any window.", Left = UI.S(150), Top = y + UI.S(24), AutoSize = true, ForeColor = Color.Gray, Font = UI.SmallFont, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

            var ok = UI.MakeButton("Stick to window", 130); ok.Left = ClientSize.Width - UI.S(12) - ok.Width; ok.Top = ClientSize.Height - UI.S(40); ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            var cancel = UI.MakeButton("Cancel", 90); cancel.Left = ok.Left - UI.S(8) - cancel.Width; cancel.Top = ok.Top; cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; cancel.DialogResult = DialogResult.Cancel;
            var desk = UI.MakeButton("Unstick (stick to desktop)", 190); desk.Left = UI.S(12); desk.Top = ok.Top; desk.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; desk.DialogResult = DialogResult.Abort;
            desk.Visible = note.Mode == StickMode.Window;

            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedItems.Count == 0) return;
                var w = (Native.WindowInfo)_list.SelectedItems[0].Tag;
                TargetHandle = w.Handle;
                _txtProc.Text = w.Process;
                _txtTitle.Text = SuggestTitle(w.Title, w.Process);
            };
            _list.DoubleClick += (s, e) => ok.PerformClick();
            ok.Click += (s, e) =>
            {
                if (ProcessPattern.Length == 0 && TitlePattern.Length == 0)
                { MessageBox.Show(this, "Please choose a window, or enter a program name and/or window title."); return; }
                DialogResult = DialogResult.OK;
            };

            Controls.AddRange(new Control[] { lbl, _list, l1, _txtProc, l1b, l2, _txtTitle, l2b, ok, cancel, desk });
            AcceptButton = ok; CancelButton = cancel;
        }

        /// <summary>Strip the browser / app suffix (" - Google Chrome") so the rule survives small changes.</summary>
        internal static string SuggestTitle(string title, string process)
        {
            if (string.IsNullOrEmpty(title)) return "";
            string[] suffixes = { " - Google Chrome", " - Microsoft Edge", " — Mozilla Firefox", " - Mozilla Firefox", " - Opera", " - Brave", " - Word", " - Excel", " - PowerPoint", " - Notepad", " - File Explorer" };
            foreach (var s in suffixes)
                if (title.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return title.Substring(0, title.Length - s.Length).Trim();
            int i = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (i > 0 && title.Length - i < 30) return title.Substring(0, i).Trim();
            return title.Trim();
        }
    }

    internal class TagsDialog : Form
    {
        public List<string> Tags { get; private set; }
        private readonly TextBox _txt;
        private readonly CheckedListBox _list;

        public TagsDialog(List<string> current, List<string> all)
        {
            Text = "Tags"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; TopMost = true; Font = UI.UiFont;
            ClientSize = new Size(UI.S(340), UI.S(330));
            var l = new Label { Text = "Tick existing tags or type new ones (comma separated):", Left = UI.S(12), Top = UI.S(10), Width = UI.S(316), Height = UI.S(20) };
            _list = new CheckedListBox { Left = UI.S(12), Top = UI.S(34), Width = UI.S(316), Height = UI.S(190), CheckOnClick = true };
            var defaults = new[] { "work", "personal", "family", "travel", "urgent", "ideas" };
            foreach (var t in all.Union(defaults, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                _list.Items.Add(t, current.Contains(t, StringComparer.OrdinalIgnoreCase));
            _txt = new TextBox { Left = UI.S(12), Top = UI.S(236), Width = UI.S(316) };
            var ok = UI.MakeButton("OK", 80); ok.Left = UI.S(160); ok.Top = UI.S(286);
            var cancel = UI.MakeButton("Cancel", 80); cancel.Left = UI.S(248); cancel.Top = UI.S(286); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) =>
            {
                var tags = _list.CheckedItems.Cast<string>().ToList();
                tags.AddRange(_txt.Text.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
                Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                DialogResult = DialogResult.OK;
            };
            Controls.AddRange(new Control[] { l, _list, _txt, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    internal class SettingsForm : Form
    {
        private readonly AppController _app;
        private readonly CheckBox _chkStartup, _chkSound, _chkEmail, _chkSsl;
        private readonly ComboBox _cmbColor, _cmbAlert;
        private readonly TextBox _host, _port, _user, _pass, _from, _to;
        private Font _defaultFont;

        public SettingsForm(AppController app)
        {
            _app = app;
            var st = app.Data.Settings;
            Text = "StickyNotes Pro – Settings"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; Font = UI.UiFont; Icon = AppController.AppIcon;
            ClientSize = new Size(UI.S(480), UI.S(470));

            var tabs = new TabControl { Left = UI.S(10), Top = UI.S(10), Width = UI.S(460), Height = UI.S(410) };
            var tGen = new TabPage("General"); var tSec = new TabPage("Security"); var tMail = new TabPage("E-mail reminders"); var tKeys = new TabPage("Hotkeys"); var tBak = new TabPage("Backup & transfer");
            tabs.TabPages.AddRange(new[] { tGen, tSec, tMail, tKeys, tBak });

            // General
            int y = UI.S(16), x = UI.S(14);
            _chkStartup = new CheckBox { Text = "Start StickyNotes Pro when Windows starts", Left = x, Top = y, AutoSize = true, Checked = st.StartWithWindows };
            y += UI.S(30);
            _chkSound = new CheckBox { Text = "Play a sound with reminder popups", Left = x, Top = y, AutoSize = true, Checked = st.PlaySound };
            y += UI.S(38);
            tGen.Controls.Add(new Label { Text = "Default color for new notes:", Left = x, Top = y + UI.S(3), AutoSize = true });
            _cmbColor = new ComboBox { Left = x + UI.S(200), Top = y, Width = UI.S(140), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var c in Palette.Colors) _cmbColor.Items.Add(c.Name);
            int ci = Array.FindIndex(Palette.Colors, c => c.Color.ToArgb() == st.DefaultColorArgb);
            _cmbColor.SelectedIndex = ci < 0 ? 0 : ci;
            y += UI.S(34);
            tGen.Controls.Add(new Label { Text = "Default font for new notes:", Left = x, Top = y + UI.S(3), AutoSize = true });
            try { _defaultFont = new Font(st.DefaultFont, st.DefaultFontSize); } catch { _defaultFont = new Font("Segoe UI", 11f); }
            var btnFont = new Button { Text = $"{_defaultFont.Name}, {_defaultFont.SizeInPoints:0.#}pt", Left = x + UI.S(200), Top = y - UI.S(2), Width = UI.S(200), Height = UI.S(26), FlatStyle = FlatStyle.System };
            btnFont.Click += (s, e) =>
            {
                using (var d = new FontDialog { Font = _defaultFont })
                    if (d.ShowDialog(this) == DialogResult.OK) { _defaultFont = d.Font; btnFont.Text = $"{_defaultFont.Name}, {_defaultFont.SizeInPoints:0.#}pt"; }
            };
            y += UI.S(34);
            tGen.Controls.Add(new Label { Text = "Default early alert for deadlines:", Left = x, Top = y + UI.S(3), AutoSize = true });
            _cmbAlert = new ComboBox { Left = x + UI.S(200), Top = y, Width = UI.S(140), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbAlert.Items.AddRange(new object[] { "No early alert", "1 day before", "2 days before", "3 days before" });
            _cmbAlert.SelectedIndex = UI.Clamp(st.DefaultAlertDays, 0, 3);
            y += UI.S(46);
            var dataLbl = new LinkLabel { Text = "Open data folder (" + Storage.Folder + ")", Left = x, Top = y, Width = UI.S(420), Height = UI.S(36) };
            dataLbl.LinkClicked += (s, e) => Security.SafeShell.OpenFolder(Storage.Folder);
            tGen.Controls.AddRange(new Control[] { _chkStartup, _chkSound, _cmbColor, btnFont, _cmbAlert, dataLbl });

            // Backup & transfer
            y = UI.S(16);
            var bakInfo = new Label
            {
                Text = "Export all your notes, checklists, toggle lists, deadlines and settings into one file (*.snpbackup).\n" +
                       "Use it to move your notes to another computer or to a newer version of StickyNotes Pro – " +
                       "on the other side choose \"Import from backup\" (or double-click the file).",
                Left = x, Top = y, Width = UI.S(420), Height = UI.S(70)
            };
            y += UI.S(78);
            var btnExp = new Button { Text = "Export all notes…", Left = x, Top = y, Width = UI.S(200), Height = UI.S(30), FlatStyle = FlatStyle.System };
            btnExp.Click += (s, e) => _app.ExportBackup(this);
            var btnImp = new Button { Text = "Import from backup…", Left = x + UI.S(210), Top = y, Width = UI.S(200), Height = UI.S(30), FlatStyle = FlatStyle.System };
            btnImp.Click += (s, e) => _app.ImportBackup(this);
            y += UI.S(40);
            var btnTxt = new Button { Text = "Export all as text file…", Left = x, Top = y, Width = UI.S(200), Height = UI.S(30), FlatStyle = FlatStyle.System };
            btnTxt.Click += (s, e) => _app.ExportAllText(this);
            var btnFolder = new Button { Text = "Open automatic backups", Left = x + UI.S(210), Top = y, Width = UI.S(200), Height = UI.S(30), FlatStyle = FlatStyle.System };
            btnFolder.Click += (s, e) => _app.OpenBackupFolder();
            y += UI.S(48);
            var autoInfo = new Label
            {
                Text = "Automatic backups: one per day, plus one before every import (the last 10 are kept).\n" +
                       (st.LastAutoBackup == DateTime.MinValue ? "No automatic backup yet." : "Last automatic backup: " + st.LastAutoBackup.ToString("f")) +
                       "\nFolder: " + Storage.BackupFolder,
                Left = x, Top = y, Width = UI.S(420), Height = UI.S(80), ForeColor = Color.DimGray
            };
            tBak.Controls.AddRange(new Control[] { bakInfo, btnExp, btnImp, btnTxt, btnFolder, autoInfo });

            // Security
            y = UI.S(16);
            var secInfo = new Label
            {
                Text = "The master password is used to encrypt (AES-256) notes you choose with \"Encrypt with master password\".\n\n" +
                       "IMPORTANT: If you forget the master password, encrypted notes cannot be recovered.",
                Left = x, Top = y, Width = UI.S(420), Height = UI.S(80)
            };
            y += UI.S(90);
            var lblState = new Label { Text = string.IsNullOrEmpty(st.MasterVerifier) ? "No master password set." : "A master password is set.", Left = x, Top = y, AutoSize = true, Font = UI.UiFontBold };
            y += UI.S(30);
            var btnPwd = new Button { Text = string.IsNullOrEmpty(st.MasterVerifier) ? "Set master password…" : "Change master password…", Left = x, Top = y, Width = UI.S(200), Height = UI.S(28), FlatStyle = FlatStyle.System };
            btnPwd.Click += (s, e) => { if (_app.ChangeMasterPassword(this)) { lblState.Text = "A master password is set."; btnPwd.Text = "Change master password…"; } };
            var btnLock = new Button { Text = "Lock all encrypted notes now", Left = x + UI.S(210), Top = y, Width = UI.S(200), Height = UI.S(28), FlatStyle = FlatStyle.System };
            btnLock.Click += (s, e) => _app.LockAll();
            tSec.Controls.AddRange(new Control[] { secInfo, lblState, btnPwd, btnLock });

            // program integrity (tamper protection status)
            y += UI.S(46);
            tSec.Controls.Add(new Label { Text = "Program integrity (tamper protection)", Left = x, Top = y, AutoSize = true, Font = UI.UiFontBold });
            y += UI.S(24);
            var integ = new TextBox { Left = x, Top = y, Width = UI.S(420), Height = UI.S(118), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.25f), BackColor = SystemColors.Window };
            Func<string> integText = () =>
                "Status:     " + Security.IntegrityGuard.Status + "\r\n" +
                "Version:    " + Program.Version + "\r\n" +
                "Signed by:  " + (Security.IntegrityGuard.SignerSubject.Length > 0 ? Security.IntegrityGuard.SignerSubject : "-") + "\r\n" +
                "Pinned cert SHA-256:\r\n  " + (Security.SigningPin.CertSha256.Length > 0 ? Security.SigningPin.CertSha256 : "-") + "\r\n" +
                "Executable SHA-256:\r\n  " + (Security.IntegrityGuard.FileSha256.Length > 0 ? Security.IntegrityGuard.FileSha256 : "-") + "\r\n" +
                "Last check: " + (Security.IntegrityGuard.LastCheck == default(DateTime) ? "-" : Security.IntegrityGuard.LastCheck.ToString("yyyy-MM-dd HH:mm:ss"));
            integ.Text = integText();
            y += UI.S(124);
            var btnVerify = new Button { Text = "Verify now", Left = x, Top = y, Width = UI.S(120), Height = UI.S(26), FlatStyle = FlatStyle.System, Enabled = Security.IntegrityGuard.Enforced };
            btnVerify.Click += (s, e) =>
            {
                string r = Security.IntegrityGuard.VerifyNow();
                integ.Text = integText();
                if (r == null) MessageBox.Show(this, "The program's digital signature, SHA-256 hash and in-memory code were verified successfully.", "Integrity check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            tSec.Controls.AddRange(new Control[] { integ, btnVerify });

            // Mail
            y = UI.S(12);
            _chkEmail = new CheckBox { Text = "Enable e-mail reminders (for deadlines marked \"send to my e-mail\")", Left = x, Top = y, AutoSize = true, Checked = st.EmailEnabled };
            y += UI.S(32);
            TextBox Row(string label, string value, bool pwd = false)
            {
                tMail.Controls.Add(new Label { Text = label, Left = x, Top = y + UI.S(3), AutoSize = true });
                var tb = new TextBox { Left = x + UI.S(130), Top = y, Width = UI.S(290), Text = value, UseSystemPasswordChar = pwd };
                tMail.Controls.Add(tb);
                y += UI.S(32);
                return tb;
            }
            _host = Row("SMTP server:", st.SmtpHost);
            _port = Row("Port:", st.SmtpPort.ToString());
            _chkSsl = new CheckBox { Text = "Use SSL / TLS", Left = x + UI.S(130), Top = y, AutoSize = true, Checked = st.SmtpSsl };
            tMail.Controls.Add(_chkSsl); y += UI.S(30);
            _user = Row("User name:", st.SmtpUser);
            _pass = Row("Password:", Crypto.Unprotect(st.SmtpPasswordProtected), true);
            _from = Row("From address:", st.EmailFrom);
            _to = Row("Send alerts to:", st.EmailTo);
            var tip = new Label { Text = "Gmail: smtp.gmail.com, port 587, SSL on, use an \"App password\".  Outlook: smtp.office365.com, 587.", Left = x, Top = y, Width = UI.S(420), Height = UI.S(34), ForeColor = Color.Gray, Font = UI.SmallFont };
            var test = new Button { Text = "Send test e-mail", Left = x, Top = y + UI.S(36), Width = UI.S(150), Height = UI.S(28), FlatStyle = FlatStyle.System };
            test.Click += (s, e) =>
            {
                SaveSettings();
                try { AppController.SendMailSync(_app.Data.Settings, "StickyNotes Pro test", "E-mail reminders are working!"); UI.Info(this, "Test e-mail sent."); }
                catch (Exception ex) { MessageBox.Show(this, "Sending failed: " + ex.Message, "E-mail", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            tMail.Controls.AddRange(new Control[] { _chkEmail, tip, test });

            // Hotkeys
            tKeys.Controls.Add(new Label
            {
                Left = x, Top = UI.S(8), Width = UI.S(430), Height = UI.S(395), Font = new Font("Consolas", 8.2f),
                Text = "Global hotkeys (work anywhere in Windows):\n" +
                       "  Ctrl+Alt+N  new note        Ctrl+Alt+C  new checklist\n" +
                       "  Ctrl+Alt+T  new toggle list Ctrl+Alt+B  notes browser\n" +
                       "  Ctrl+Alt+W  note stuck to the ACTIVE window\n" +
                       "  Ctrl+Alt+D  deadlines list  Ctrl+Alt+H  hide/show all\n\n" +
                       "Inside a note:\n" +
                       "  Ctrl+N  new note      Ctrl+F2  edit title\n" +
                       "  Ctrl+Shift+N checklist  Ctrl+Shift+G toggle list\n" +
                       "  Ctrl+R  due date      Ctrl+W   stick to window\n" +
                       "  Ctrl+D  delete        Alt+Enter properties\n" +
                       "  Ctrl+B/I/U/T  bold/italic/underline/strike\n" +
                       "  Ctrl+Shift+L  bullets  F5  insert date/time\n" +
                       "  Ctrl+F / F3   find     Ctrl+Shift+V plain paste\n" +
                       "  Ctrl+Shift+C  checkboxes on the same line ( ) a  ( ) b\n" +
                       "  Ctrl+Space    tick / untick the checkbox at the cursor\n\n" +
                       "Checklist: Enter = new item, Ctrl+Enter = done,\n" +
                       "  Ctrl+Shift+D = deadline, Alt+Up/Down = move,\n" +
                       "  Ctrl+Shift+H = item <-> section title,\n" +
                       "  Ctrl+Enter on a section = expand/collapse\n\n" +
                       "Toggle list: Enter in title = go to content,\n" +
                       "  Ctrl+Enter = expand/collapse, Ctrl+Up = title,\n" +
                       "  Ctrl+Shift+Enter = new toggle, Alt+Up/Down = move"
            });

            var ok = UI.MakeButton("OK", 90); ok.Left = ClientSize.Width - UI.S(10) - ok.Width; ok.Top = UI.S(430);
            var cancel = UI.MakeButton("Cancel", 90); cancel.Left = ok.Left - UI.S(8) - cancel.Width; cancel.Top = ok.Top; cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => { if (!ValidateInput()) return; SaveSettings(); DialogResult = DialogResult.OK; };
            Controls.AddRange(new Control[] { tabs, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }

        /// <summary>Strict validation of the e-mail settings before they are stored.</summary>
        private bool ValidateInput()
        {
            string err = null;
            string host = _host.Text.Trim(), from = _from.Text.Trim(), to = _to.Text.Trim();
            if (host.Length > 0 && !Security.InputValidator.IsValidHost(host)) err = "The SMTP server name is not valid (letters, digits, '-' and '.' only).";
            else if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) err = "The SMTP port must be a number between 1 and 65535.";
            else if (from.Length > 0 && !Security.InputValidator.IsValidEmail(from)) err = "The 'From' e-mail address is not valid.";
            else if (to.Length > 0 && !Security.InputValidator.IsValidEmail(to)) err = "The 'To' e-mail address is not valid.";
            else if (_user.Text.Trim().Length > 254 || _user.Text.IndexOfAny(new[] { '\r', '\n' }) >= 0) err = "The user name is not valid.";
            else if (_chkEmail.Checked && (host.Length == 0 || to.Length == 0)) err = "To send e-mail reminders, enter the SMTP server and the 'To' address.";
            if (err == null) return true;
            MessageBox.Show(this, err, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void SaveSettings()
        {
            var st = _app.Data.Settings;
            st.StartWithWindows = _chkStartup.Checked;
            st.PlaySound = _chkSound.Checked;
            st.DefaultColorArgb = Palette.Colors[Math.Max(0, _cmbColor.SelectedIndex)].Color.ToArgb();
            st.DefaultFont = _defaultFont.Name; st.DefaultFontSize = _defaultFont.SizeInPoints;
            st.DefaultAlertDays = Math.Max(0, _cmbAlert.SelectedIndex);
            st.EmailEnabled = _chkEmail.Checked;
            st.SmtpHost = _host.Text.Trim();
            st.SmtpPort = int.TryParse(_port.Text.Trim(), out int p) ? p : 587;
            st.SmtpSsl = _chkSsl.Checked;
            st.SmtpUser = _user.Text.Trim();
            st.SmtpPasswordProtected = Crypto.Protect(_pass.Text);
            st.EmailFrom = _from.Text.Trim();
            st.EmailTo = _to.Text.Trim();
            AppController.SetStartup(st.StartWithWindows);
            _app.MarkDirty();
        }
    }
}
