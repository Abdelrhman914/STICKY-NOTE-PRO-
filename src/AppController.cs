using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StickyNotesPro
{
    internal partial class AppController : ApplicationContext
    {
        public static AppController I;
        public static Icon AppIcon;

        public AppData Data;
        public readonly List<NoteForm> Forms = new List<NoteForm>();
        public byte[] MasterKey;
        public bool Exiting;
        public event Action NotesChanged;

        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hotkeys;
        private readonly Timer _saveTimer = new Timer { Interval = 1500 };
        private readonly Timer _reminderTimer = new Timer { Interval = 15000 };
        private readonly Timer _watchTimer = new Timer { Interval = 250 };
        private readonly Dictionary<string, ReminderForm> _reminders = new Dictionary<string, ReminderForm>();
        private NotesBrowserForm _browser;
        private SettingsForm _settings;
        private readonly Control _invoker = new Control();
        private readonly uint _myPid = (uint)Process.GetCurrentProcess().Id;
        private DateTime _lastRefresh = DateTime.Now;

        public int OpenReminderCount => _reminders.Count;

        public AppController()
        {
            I = this;
            _invoker.CreateControl();
            try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { AppIcon = SystemIcons.Application; }

            Data = Storage.Load();
            if (!Data.Settings.FirstRunDone)
            {
                ApplyInstallerChoices();
                CreateWelcomeNotes();
            }

            foreach (var n in Data.Notes.ToList()) CreateForm(n);
            foreach (var f in Forms)
                if (f.Data.Mode == StickMode.Desktop && !f.Data.Hidden) f.Show();

            // tray
            _tray = new NotifyIcon { Icon = AppIcon, Text = "StickyNotes Pro", Visible = true, ContextMenuStrip = BuildTrayMenu() };
            _tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowBrowser(false); };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowAll(true); };
            _tray.BalloonTipClicked += (s, e) => ShowBrowser(true);

            // hotkeys
            _hotkeys = new HotkeyWindow();
            _hotkeys.Register(1, Native.MOD_CONTROL | Native.MOD_ALT, Keys.N);
            _hotkeys.Register(2, Native.MOD_CONTROL | Native.MOD_ALT, Keys.C);
            _hotkeys.Register(3, Native.MOD_CONTROL | Native.MOD_ALT, Keys.W);
            _hotkeys.Register(4, Native.MOD_CONTROL | Native.MOD_ALT, Keys.B);
            _hotkeys.Register(5, Native.MOD_CONTROL | Native.MOD_ALT, Keys.D);
            _hotkeys.Register(6, Native.MOD_CONTROL | Native.MOD_ALT, Keys.H);
            _hotkeys.Register(7, Native.MOD_CONTROL | Native.MOD_ALT, Keys.T);
            _hotkeys.HotkeyPressed += OnHotkey;

            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Save(); };
            _reminderTimer.Tick += (s, e) => CheckReminders();
            _watchTimer.Tick += (s, e) => WatchWindows();
            _reminderTimer.Start();
            _watchTimer.Start();

            SetStartup(Data.Settings.StartWithWindows);
            if (!Data.Settings.FirstRunDone)
            {
                Data.Settings.FirstRunDone = true;
                _tray.ShowBalloonTip(6000, "StickyNotes Pro is running", "Right-click the tray icon for options. Ctrl+Alt+N = new note, Ctrl+Alt+W = note for the active window.", ToolTipIcon.Info);
                Save();
            }
            // check shortly after start (missed alerts while PC was off)
            var t = new Timer { Interval = 3000 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); CheckReminders(); DailyBackup(); };
            t.Start();
        }

        public void InvokeOnUi(Action a)
        {
            try { if (_invoker.IsHandleCreated) _invoker.BeginInvoke(a); } catch { }
        }

        // ------------------------------------------------------------------ notes

        private NoteForm CreateForm(NoteData d)
        {
            var f = new NoteForm(this, d);
            Forms.Add(f);
            return f;
        }

        public NoteForm NewNote(NoteKind kind, Point? location)
        {
            var st = Data.Settings;
            var d = new NoteData
            {
                Kind = kind, ColorArgb = st.DefaultColorArgb, FontName = st.DefaultFont, FontSize = st.DefaultFontSize,
                W = UI.S(kind == NoteKind.Text ? 260 : 300), H = UI.S(kind == NoteKind.Checklist ? 260 : kind == NoteKind.Toggle ? 280 : 230)
            };
            if (kind == NoteKind.Toggle) d.Toggles.Add(new ToggleItem());
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            var p = location ?? new Point(wa.Left + wa.Width / 2 - d.W / 2 + new Random().Next(-80, 80), wa.Top + wa.Height / 3 + new Random().Next(-60, 60));
            d.X = UI.Clamp(p.X, wa.Left, wa.Right - d.W);
            d.Y = UI.Clamp(p.Y, wa.Top, wa.Bottom - d.H);
            Data.Notes.Add(d);
            var f = CreateForm(d);
            f.ShowNote(true);
            f.FocusEditor();
            MarkDirty();
            return f;
        }

        public void NewNoteForActiveWindow()
        {
            var fg = Native.GetForegroundWindow();
            string proc = Native.GetProcessName(fg, out uint pid);
            string title = Native.GetTitle(fg);
            if (fg == IntPtr.Zero || pid == _myPid || string.IsNullOrWhiteSpace(title) || title == "Program Manager")
            {
                NewNote(NoteKind.Text, null);
                return;
            }
            var st = Data.Settings;
            var r = Native.GetVisibleRect(fg);
            int existing = Forms.Count(x => x.Data.MatchesWindow(proc, title));
            var d = new NoteData
            {
                Kind = NoteKind.Text, ColorArgb = st.DefaultColorArgb, FontName = st.DefaultFont, FontSize = st.DefaultFontSize,
                W = UI.S(250), H = UI.S(200), Mode = StickMode.Window, WinProcess = proc,
                WinTitle = StickToWindowDialog.SuggestTitle(title, proc), Title = "",
            };
            d.WinOffsetX = Math.Max(UI.S(10), r.Width - d.W - UI.S(60) - existing * UI.S(30));
            d.WinOffsetY = UI.S(90) + existing * UI.S(30);
            Data.Notes.Add(d);
            var f = CreateForm(d);
            f.AttachedHwnd = fg;
            f.ProgrammaticMove = true;
            f.Location = new Point(r.Left + d.WinOffsetX, r.Top + d.WinOffsetY);
            f.ProgrammaticMove = false;
            f.ShowNote(true);
            f.FocusEditor();
            MarkDirty();
            _tray.ShowBalloonTip(3000, "Note stuck to window", $"This note will appear whenever \"{d.WinTitle}\" ({proc}) is active.", ToolTipIcon.Info);
        }

        public void DeleteNote(NoteForm f, bool confirm)
        {
            if (confirm)
            {
                bool empty = !f.Data.Encrypted && f.LiveItems.Count == 0 &&
                             (f.Data.Kind == NoteKind.Toggle ? f.LiveToggles.All(t => string.IsNullOrWhiteSpace(t.Title) && string.IsNullOrWhiteSpace(t.PlainText)) : string.IsNullOrWhiteSpace(f.Data.PlainText));
                if (!empty && !UI.Confirm(f, $"Delete the note \"{f.Data.DisplayTitle}\"?\nThis cannot be undone.")) return;
            }
            foreach (var k in _reminders.Keys.Where(k => k.StartsWith(f.Data.Id + ":")).ToList()) _reminders[k].Close();
            Forms.Remove(f);
            Data.Notes.Remove(f.Data);
            f.Dispose();
            MarkDirty();
        }

        public void GoToNote(NoteForm f)
        {
            if (f.Data.Mode == StickMode.Window && !f.Visible)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.ProgrammaticMove = true;
                f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + (wa.Height - f.Height) / 3);
                f.ProgrammaticMove = false;
                f.AttachedHwnd = IntPtr.Zero;
            }
            f.ShowNote(true);
        }

        public List<string> AllTags() =>
            Forms.SelectMany(f => f.Data.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();

        public void ShowAll(bool show)
        {
            foreach (var f in Forms)
            {
                if (show)
                {
                    if (f.Data.Mode == StickMode.Desktop) { f.Data.Hidden = false; if (!f.Visible) f.Show(); f.BringToFront(); }
                }
                else { if (f.Data.Mode == StickMode.Desktop) f.Data.Hidden = true; f.Hide(); }
            }
            MarkDirty();
        }

        private void ToggleAll()
        {
            bool anyVisible = Forms.Any(f => f.Visible && f.Data.Mode == StickMode.Desktop);
            ShowAll(!anyVisible);
        }

        public void Arrange(string mode)
        {
            var notes = Forms.Where(f => f.Data.Mode == StickMode.Desktop && !f.Data.Hidden).ToList();
            if (notes.Count == 0) return;
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = UI.S(12);
            if (mode == "cascade")
            {
                int x = wa.Left + UI.S(60), y = wa.Top + UI.S(60);
                foreach (var f in notes)
                {
                    f.Location = new Point(x, y); f.BringToFront();
                    x += UI.S(32); y += UI.S(32);
                    if (y + UI.S(200) > wa.Bottom) { y = wa.Top + UI.S(60); x += UI.S(60); }
                }
            }
            else if (mode == "tile")
            {
                int cols = (int)Math.Ceiling(Math.Sqrt(notes.Count));
                int rows = (int)Math.Ceiling(notes.Count / (double)cols);
                int cw = Math.Min(UI.S(320), (wa.Width - gap) / cols - gap);
                int ch = Math.Min(UI.S(300), (wa.Height - gap) / rows - gap);
                for (int i = 0; i < notes.Count; i++)
                {
                    var f = notes[i];
                    if (f.Data.RolledUp) f.ToggleRoll();
                    f.Bounds = new Rectangle(wa.Left + gap + (i % cols) * (cw + gap), wa.Top + gap + (i / cols) * (ch + gap), cw, ch);
                }
            }
            else
            {
                int x = wa.Right - gap, y = wa.Top + gap, colW = 0;
                foreach (var f in notes)
                {
                    if (y + f.Height > wa.Bottom && y > wa.Top + gap) { y = wa.Top + gap; x -= colW + gap; colW = 0; }
                    f.Location = new Point(x - f.Width, y);
                    y += f.Height + gap; colW = Math.Max(colW, f.Width);
                }
            }
            MarkDirty();
        }

        // ------------------------------------------------------------------ windows

        public void ShowBrowser(bool deadlines)
        {
            if (_browser == null || _browser.IsDisposed)
            {
                _browser = new NotesBrowserForm(this);
                _browser.FormClosed += (s, e) => _browser = null;
            }
            if (deadlines) _browser.ShowDeadlinesTab();
            _browser.Show();
            if (_browser.WindowState == FormWindowState.Minimized) _browser.WindowState = FormWindowState.Normal;
            _browser.Activate();
            _browser.RefreshLists();
        }

        public void ShowSettings()
        {
            if (_settings != null && !_settings.IsDisposed) { _settings.Activate(); return; }
            _settings = new SettingsForm(this);
            _settings.FormClosed += (s, e) => _settings = null;
            _settings.Show();
            _settings.Activate();
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add(new ToolStripMenuItem("New note", null, (s, e) => NewNote(NoteKind.Text, null)) { ShortcutKeyDisplayString = "Ctrl+Alt+N", Font = new Font(m.Font, FontStyle.Bold) });
            m.Items.Add(new ToolStripMenuItem("New checklist note", null, (s, e) => NewNote(NoteKind.Checklist, null)) { ShortcutKeyDisplayString = "Ctrl+Alt+C" });
            m.Items.Add(new ToolStripMenuItem("New toggle list note", null, (s, e) => NewNote(NoteKind.Toggle, null)) { ShortcutKeyDisplayString = "Ctrl+Alt+T" });
            m.Items.Add(new ToolStripMenuItem("Notes browser", null, (s, e) => ShowBrowser(false)) { ShortcutKeyDisplayString = "Ctrl+Alt+B" });
            m.Items.Add(new ToolStripMenuItem("Deadlines && alerts", null, (s, e) => ShowBrowser(true)) { ShortcutKeyDisplayString = "Ctrl+Alt+D" });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Show all desktop notes", null, (s, e) => ShowAll(true)));
            m.Items.Add(new ToolStripMenuItem("Hide all notes", null, (s, e) => ShowAll(false)) { ShortcutKeyDisplayString = "Ctrl+Alt+H" });
            m.Items.Add(new ToolStripMenuItem("Lock encrypted notes", null, (s, e) => LockAll()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(BuildBackupMenu(null));
            m.Items.Add(new ToolStripMenuItem("Settings…", null, (s, e) => ShowSettings()));
            m.Items.Add(new ToolStripMenuItem("About", null, (s, e) => UI.Info(null,
                "StickyNotes Pro " + Program.Version + "\n\nDesktop sticky notes with checklists, deadlines & alerts,\nnotes stuck to windows, tags, skins, encryption and more.\n\nData: " + Storage.Folder)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
            return m;
        }

        private void OnHotkey(int id)
        {
            switch (id)
            {
                case 1: NewNote(NoteKind.Text, null); break;
                case 2: NewNote(NoteKind.Checklist, null); break;
                case 3: NewNoteForActiveWindow(); break;
                case 4: ShowBrowser(false); break;
                case 5: ShowBrowser(true); break;
                case 6: ToggleAll(); break;
                case 7: NewNote(NoteKind.Toggle, null); break;
            }
        }

        /// <summary>Show / hide notes that are stuck to windows depending on the active window.</summary>
        private void WatchWindows()
        {
            try
            {
                var windowNotes = Forms.Where(f => f.Data.Mode == StickMode.Window).ToList();
                if (windowNotes.Count == 0) return;
                var fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                string proc = Native.GetProcessName(fg, out uint pid);
                if (pid == _myPid)
                {
                    // one of our windows is active – keep notes, but follow their target windows
                    foreach (var f in windowNotes)
                        if (f.Visible && f.AttachedHwnd != IntPtr.Zero && Native.IsWindow(f.AttachedHwnd) && !Native.IsIconic(f.AttachedHwnd))
                            PositionOn(f, f.AttachedHwnd);
                    return;
                }
                string title = Native.GetTitle(fg);
                bool iconic = Native.IsIconic(fg);
                foreach (var f in windowNotes)
                {
                    bool match = !f.Data.Hidden && !iconic && f.Data.MatchesWindow(proc, title);
                    if (match)
                    {
                        f.AttachedHwnd = fg;
                        PositionOn(f, fg);
                        if (!f.Visible) f.Show();
                        if (!f.TopMost) f.TopMost = true;
                    }
                    else if (f.Visible)
                    {
                        f.Hide();
                        f.AttachedHwnd = IntPtr.Zero;
                    }
                }
            }
            catch { }
        }

        private void PositionOn(NoteForm f, IntPtr target)
        {
            var r = Native.GetVisibleRect(target);
            if (r.Width <= 0) return;
            int x = r.Left + f.Data.WinOffsetX, y = r.Top + f.Data.WinOffsetY;
            x = UI.Clamp(x, r.Left, Math.Max(r.Left, r.Right - f.Width));
            y = UI.Clamp(y, r.Top, Math.Max(r.Top, r.Bottom - UI.S(30)));
            if (f.Left != x || f.Top != y)
            {
                f.ProgrammaticMove = true;
                f.Location = new Point(x, y);
                f.ProgrammaticMove = false;
            }
        }

        // ------------------------------------------------------------------ reminders

        private void CheckReminders()
        {
            var now = DateTime.Now;
            try
            {
                foreach (var f in Forms.ToList())
                {
                    CheckDeadline(f, null, f.Data.Due, now);
                    foreach (var it in f.LiveItems.ToList())
                        if (!it.Done && !it.IsHeading) CheckDeadline(f, it, it.Due, now);
                }
            }
            catch (Exception ex) { Program.Log(ex); }

            if (now.Minute != _lastRefresh.Minute || now.Date != _lastRefresh.Date)
            {
                if (now.Date != _lastRefresh.Date) DailyBackup();
                _lastRefresh = now;
                foreach (var f in Forms) f.RefreshDeadlines();
            }
        }

        private void CheckDeadline(NoteForm f, ChecklistItem item, Deadline d, DateTime now)
        {
            if (d == null || !d.Enabled || !d.Alert) return;
            string key = f.Data.Id + ":" + (item?.Id ?? "note");
            if (_reminders.ContainsKey(key)) return;

            if (d.SnoozeUntil != DateTime.MinValue)
            {
                if (now >= d.SnoozeUntil)
                {
                    d.SnoozeUntil = DateTime.MinValue;
                    Fire(f, item, d, key, now >= d.At ? "⏰ Deadline reached!" : $"⏰ Reminder – {DaysText(d)}");
                }
                return;
            }
            if (d.AlertAtTime && !d.DueFired && now >= d.At)
            {
                d.DueFired = true; d.PreFired = true;
                Fire(f, item, d, key, "⏰ Deadline reached!");
                return;
            }
            if (d.DaysBefore > 0 && !d.PreFired && now >= d.At.AddDays(-d.DaysBefore))
            {
                d.PreFired = true;
                Fire(f, item, d, key, now >= d.At ? "⏰ Deadline passed!" : $"⏰ Deadline in {DaysText(d)}");
            }
        }

        private static string DaysText(Deadline d)
        {
            int days = d.DaysLeft;
            if (days <= 0) return "today " + d.At.ToString("HH:mm");
            return days == 1 ? "1 day (tomorrow)" : $"{days} days";
        }

        private void Fire(NoteForm f, ChecklistItem item, Deadline d, string key, string headline)
        {
            MarkDirty();
            var rf = new ReminderForm(this, f, item, d, headline, key);
            _reminders[key] = rf;
            rf.FormClosed += (s, e) => { _reminders.Remove(key); MarkDirty(); };
            rf.Show();
            string what = item != null ? item.Text : f.Data.DisplayTitle;
            if (d.Email && Data.Settings.EmailEnabled)
            {
                var st = Data.Settings;
                string subject = $"{headline.Replace("⏰", "").Trim()}: {what}";
                string body = $"{headline.Replace("⏰", "").Trim()}\r\n\r\nTask: {what}\r\nNote: {f.Data.DisplayTitle}\r\nDeadline: {d.At:dddd, d MMMM yyyy HH:mm}\r\n{d.StatusText(false, out _)}\r\n\r\n– StickyNotes Pro";
                Task.Run(() =>
                {
                    try { SendMailSync(st, subject, body); }
                    catch (Exception ex) { Program.Log(ex); InvokeOnUi(() => _tray.ShowBalloonTip(4000, "E-mail reminder failed", ex.Message, ToolTipIcon.Warning)); }
                });
            }
        }

        public static void SendMailSync(AppSettings st, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(st.SmtpHost) || string.IsNullOrWhiteSpace(st.EmailTo))
                throw new InvalidOperationException("SMTP server and recipient address are required.");
            if (!Security.InputValidator.IsValidHost(st.SmtpHost) || !Security.InputValidator.IsValidEmail(st.EmailTo) || st.SmtpPort < 1 || st.SmtpPort > 65535)
                throw new InvalidOperationException("The e-mail settings are not valid.");
            subject = Security.InputValidator.SafeHeader(subject);
            body = Security.InputValidator.CleanMultiline(body, 100000);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new SmtpClient(st.SmtpHost, st.SmtpPort) { EnableSsl = st.SmtpSsl, Timeout = 20000 })
            {
                if (!string.IsNullOrEmpty(st.SmtpUser)) client.Credentials = new NetworkCredential(st.SmtpUser, Crypto.Unprotect(st.SmtpPasswordProtected));
                string from = string.IsNullOrWhiteSpace(st.EmailFrom) ? (string.IsNullOrWhiteSpace(st.SmtpUser) ? st.EmailTo : st.SmtpUser) : st.EmailFrom;
                if (!Security.InputValidator.IsValidEmail(from)) throw new InvalidOperationException("The sender address is not valid.");
                using (var msg = new MailMessage(from, st.EmailTo, subject, body))
                    client.Send(msg);
            }
        }

        // ------------------------------------------------------------------ security

        public bool EnsureMasterKey(IWin32Window owner, bool allowCreate)
        {
            if (MasterKey != null) return true;
            var st = Data.Settings;
            if (string.IsNullOrEmpty(st.MasterVerifier))
            {
                if (!allowCreate) { MessageBox.Show(owner, "No master password has been set."); return false; }
                UI.Info(owner, "To encrypt notes you first need to set a master password.\nIt will be required to unlock encrypted notes.\n\nDon't forget it – encrypted notes cannot be recovered without it!");
                return ChangeMasterPassword(owner);
            }
            var p = UI.InputBox(owner, "Unlock", "Enter your master password:", "", true);
            if (p == null) return false;
            if (Crypto.Verify(p, st.MasterSalt, st.MasterVerifier, out var key)) { MasterKey = key; return true; }
            MessageBox.Show(owner, "Wrong master password.", "StickyNotes Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        public bool ChangeMasterPassword(IWin32Window owner)
        {
            var st = Data.Settings;
            byte[] oldKey = null;
            if (!string.IsNullOrEmpty(st.MasterVerifier))
            {
                var old = UI.InputBox(owner, "Change master password", "Current master password:", "", true);
                if (old == null) return false;
                if (!Crypto.Verify(old, st.MasterSalt, st.MasterVerifier, out oldKey))
                { MessageBox.Show(owner, "Wrong master password."); return false; }
            }
            var p1 = UI.InputBox(owner, "Master password", "New master password (at least 4 characters):", "", true);
            if (string.IsNullOrEmpty(p1)) return false;
            if (p1.Length < 4) { MessageBox.Show(owner, "The password is too short."); return false; }
            var p2 = UI.InputBox(owner, "Master password", "Type the new master password again:", "", true);
            if (p2 != p1) { MessageBox.Show(owner, "The passwords do not match."); return false; }

            if (oldKey != null)
            {
                MasterKey = oldKey;
                foreach (var f in Forms) f.SyncToData();
            }
            Crypto.CreateMaster(p1, out var salt, out var ver, out var newKey);
            if (oldKey != null)
                foreach (var n in Data.Notes.Where(n => n.Encrypted && !string.IsNullOrEmpty(n.EncryptedPayload)))
                {
                    try { n.EncryptedPayload = Crypto.Encrypt(Crypto.Decrypt(n.EncryptedPayload, oldKey), newKey); }
                    catch (Exception ex) { Program.Log(ex); }
                }
            st.MasterSalt = salt; st.MasterVerifier = ver;
            MasterKey = newKey;
            Save();
            return true;
        }

        public void LockAll()
        {
            foreach (var f in Forms) f.Relock();
            MasterKey = null;
            Save();
        }

        // ------------------------------------------------------------------ persistence

        public void MarkDirty()
        {
            _saveTimer.Stop(); _saveTimer.Start();
            NotesChanged?.Invoke();
        }

        public void Save()
        {
            try
            {
                foreach (var f in Forms) f.SyncToData();
                Data.Notes = Forms.Select(f => f.Data).ToList();
                Storage.Save(Data);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>Reads options chosen in Setup (HKCU\Software\StickyNotesPro).</summary>
        private void ApplyInstallerChoices()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\StickyNotesPro"))
                {
                    if (k?.GetValue("StartWithWindows") is int v) Data.Settings.StartWithWindows = v != 0;
                }
            }
            catch { }
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue("StickyNotesPro", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("StickyNotesPro") != null) k.DeleteValue("StickyNotesPro");
                }
            }
            catch { }
        }

        public void ExitApp()
        {
            Exiting = true;
            _watchTimer.Stop(); _reminderTimer.Stop(); _saveTimer.Stop();
            Save();
            _tray.Visible = false;
            _tray.Dispose();
            _hotkeys.Dispose();
            foreach (var r in _reminders.Values.ToList()) r.Close();
            foreach (var f in Forms.ToList()) f.Dispose();
            _browser?.Close();
            ExitThread();
        }

        private void CreateWelcomeNotes()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var welcome = new NoteData
            {
                Kind = NoteKind.Text, Title = "Welcome to StickyNotes Pro",
                X = wa.Right - UI.S(620), Y = wa.Top + UI.S(60), W = UI.S(300), H = UI.S(330),
                ColorArgb = Palette.Colors[0].Color.ToArgb(),
                Rtf = "",
                PlainText =
                    "• Ctrl+Alt+N – new sticky note\n" +
                    "• Ctrl+Alt+C – new checklist\n" +
                    "• Ctrl+Alt+T – new toggle list (titles that expand / collapse)\n" +
                    "• Ctrl+Alt+W – new note stuck to the window you are using (e.g. Google Chrome). It pops up every time that window is active!\n" +
                    "• ☰ menu – colors, skins, transparency, tags, due date, lock & encrypt, stick to window…\n" +
                    "• Drag the title to move, drag the edges to resize, double-click the title to roll up.\n" +
                    "• Right-click the tray icon for the Notes browser & Deadlines list.\n" +
                    "• Tray → Backup & transfer – export all your notes to a file and import them on another PC or a newer version.",
            };
            welcome.Rtf = welcome.PlainText;
            var tasks = new NoteData
            {
                Kind = NoteKind.Checklist, Title = "My tasks",
                X = wa.Right - UI.S(310), Y = wa.Top + UI.S(60), W = UI.S(290), H = UI.S(260),
                ColorArgb = Palette.Colors[4].Color.ToArgb(), Skin = SkinType.Ruled,
            };
            tasks.Items.Add(new ChecklistItem
            {
                Text = "Finish the project report",
                Due = new Deadline { Enabled = true, At = DateTime.Today.AddDays(3).AddHours(18), Alert = true, DaysBefore = 1, AlertAtTime = true, SetOn = DateTime.Today }
            });
            tasks.Items.Add(new ChecklistItem
            {
                Text = "Click the calendar button to set a deadline",
                Due = new Deadline { Enabled = true, At = DateTime.Today.AddDays(7).AddHours(12), Alert = true, DaysBefore = 2, AlertAtTime = true, SetOn = DateTime.Today }
            });
            tasks.Items.Add(new ChecklistItem { Text = "Press Enter to add a new item" });
            tasks.Items.Add(new ChecklistItem { Text = "Tick the box when it's done", Done = true });
            tasks.Items.Insert(0, new ChecklistItem { Text = "This week", IsHeading = true });
            tasks.Items.Add(new ChecklistItem { Text = "Later (click the arrow to collapse)", IsHeading = true });
            tasks.Items.Add(new ChecklistItem { Text = "Ctrl+Shift+H turns an item into a section title" });

            var toggles = new NoteData
            {
                Kind = NoteKind.Toggle, Title = "Toggle list",
                X = wa.Right - UI.S(310), Y = wa.Top + UI.S(340), W = UI.S(290), H = UI.S(250),
                ColorArgb = Palette.Colors[2].Color.ToArgb(),
            };
            toggles.Toggles.Add(new ToggleItem { Title = "What is a toggle list?", Expanded = true, PlainText = "Each title expands and collapses.\nClick the arrow (or press Ctrl+Enter) to open or close it." });
            toggles.Toggles.Add(new ToggleItem { Title = "Meeting notes", Expanded = false, PlainText = "Content can have bold, colors, bullets and pictures – just like a normal note." });
            toggles.Toggles.Add(new ToggleItem { Title = "Shortcuts", Expanded = false, PlainText = "Enter in a title – jump into its content\nCtrl+Shift+Enter – new toggle below\nAlt+Up / Alt+Down – move a toggle\nCtrl+Up – back to the title" });
            Data.Notes.Add(welcome);
            Data.Notes.Add(tasks);
            Data.Notes.Add(toggles);
        }
    }

    internal class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> HotkeyPressed;
        private readonly List<int> _ids = new List<int>();

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(int id, uint mods, Keys key)
        {
            bool ok = Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, (uint)key);
            if (ok) _ids.Add(id);
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) HotkeyPressed?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
            _ids.Clear();
            DestroyHandle();
        }
    }
}
