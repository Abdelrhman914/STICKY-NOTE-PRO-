using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>Export / import of all data (*.snpbackup) to move notes between versions or computers.</summary>
    internal partial class AppController
    {
        public const string BackupExt = ".snpbackup";
        public const string BackupFilter = "StickyNotes Pro backup (*.snpbackup)|*.snpbackup";

        /// <summary>Exports all notes (or only <paramref name="subset"/>) plus settings to a backup file.</summary>
        public void ExportBackup(IWin32Window owner, IList<NoteData> subset = null)
        {
            Save();
            string name = subset == null
                ? $"StickyNotesPro-backup-{DateTime.Now:yyyy-MM-dd}{BackupExt}"
                : $"StickyNotesPro-{subset.Count}-notes-{DateTime.Now:yyyy-MM-dd}{BackupExt}";
            using (var d = new SaveFileDialog
            {
                Title = subset == null ? "Export all notes (backup / transfer)" : "Export selected notes",
                Filter = BackupFilter, FileName = name, DefaultExt = "snpbackup", AddExtension = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            })
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    var b = MakeBackup(subset);
                    Storage.SaveBackup(d.FileName, b);
                    int enc = b.Data.Notes.Count(n => n.Encrypted);
                    string msg = $"Exported {b.Data.Notes.Count} note(s) to:\n{d.FileName}\n\n" +
                                 "To move your notes to another computer or a newer version, install StickyNotes Pro there and use " +
                                 "\"Backup & transfer → Import…\" (or just double-click the file)." +
                                 (enc > 0 ? $"\n\n{enc} encrypted note(s) stay encrypted – you will need your master password to open them." : "") +
                                 "\n\nOpen the folder now?";
                    if (MessageBox.Show(owner, msg, "Export complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Security.SafeShell.RevealInExplorer(d.FileName);
                }
                catch (Exception ex)
                {
                    Program.Log(ex);
                    MessageBox.Show(owner, "Export failed: " + ex.Message, "StickyNotes Pro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private BackupFile MakeBackup(IList<NoteData> subset)
        {
            // deep copy through XML so the live objects are never touched
            var copy = Storage.Normalize(Storage.DeepCopy(Data));
            if (subset != null)
            {
                var ids = new HashSet<string>(subset.Select(n => n.Id));
                copy.Notes = copy.Notes.Where(n => ids.Contains(n.Id)).ToList();
            }
            return new BackupFile { Data = copy };
        }

        /// <summary>Silent backup into %APPDATA%\StickyNotesPro\backups.</summary>
        public string AutoBackup(string prefix)
        {
            try
            {
                Directory.CreateDirectory(Storage.BackupFolder);
                string path = Path.Combine(Storage.BackupFolder, $"{prefix}-{DateTime.Now:yyyy-MM-dd_HHmmss}{BackupExt}");
                Storage.SaveBackup(path, MakeBackup(null));
                // keep the 10 newest files of each kind
                foreach (var old in new DirectoryInfo(Storage.BackupFolder).GetFiles(prefix + "-*" + BackupExt)
                             .OrderByDescending(f => f.LastWriteTime).Skip(10))
                    try { old.Delete(); } catch { }
                return path;
            }
            catch (Exception ex) { Program.Log(ex); return null; }
        }

        /// <summary>One automatic backup per day (called at start-up and when the date changes).</summary>
        public void DailyBackup()
        {
            if (Data.Settings.LastAutoBackup.Date == DateTime.Today || Data.Notes.Count == 0) return;
            Save();
            if (AutoBackup("auto") != null) { Data.Settings.LastAutoBackup = DateTime.Now; MarkDirty(); }
        }

        public void OpenBackupFolder()
        {
            try { Directory.CreateDirectory(Storage.BackupFolder); Security.SafeShell.OpenFolder(Storage.BackupFolder); } catch { }
        }

        /// <summary>Imports a backup file (from any version) – merge with or replace the current notes.</summary>
        public void ImportBackup(IWin32Window owner, string path = null)
        {
            if (path == null)
            {
                using (var d = new OpenFileDialog
                {
                    Title = "Import notes from a backup",
                    Filter = BackupFilter + "|StickyNotes Pro data file (notes.xml)|*.xml|All files|*.*",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                })
                {
                    if (d.ShowDialog(owner) != DialogResult.OK) return;
                    path = d.FileName;
                }
            }

            BackupFile b;
            try { b = Storage.LoadBackup(path); }
            catch (Exception ex)
            {
                Program.Log(ex);
                MessageBox.Show(owner, "This file could not be read as a StickyNotes Pro backup.\n\n" + ex.Message, "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (b.Data.Notes.Count == 0 && !UI.Confirm(owner, "The backup does not contain any notes. Import its settings only?")) return;

            bool replace, withSettings;
            using (var dlg = new ImportDialog(b, path, Data.Notes.Count))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                replace = dlg.Replace; withSettings = dlg.ImportSettings;
            }

            // safety copy of the current state
            Save();
            string safety = AutoBackup("before-import");

            var incoming = b.Data.Notes;
            var bs = b.Data.Settings;
            int skippedEncrypted = 0;
            bool adoptedMaster = false;

            // ---- encrypted notes: make them readable with the current master password
            var enc = incoming.Where(n => n.Encrypted && !string.IsNullOrEmpty(n.EncryptedPayload)).ToList();
            var st = Data.Settings;
            bool sameMaster = bs.MasterVerifier == st.MasterVerifier && bs.MasterSalt == st.MasterSalt;
            if (enc.Count > 0 && !sameMaster)
            {
                bool currentHasMaster = !string.IsNullOrEmpty(st.MasterVerifier);
                bool currentHasEncrypted = Forms.Any(f => f.Data.Encrypted);
                if (!currentHasMaster || !currentHasEncrypted)
                {
                    // nothing here is encrypted yet → simply take over the backup's master password
                    LockAll();
                    st.MasterSalt = bs.MasterSalt; st.MasterVerifier = bs.MasterVerifier;
                    MasterKey = null;
                    adoptedMaster = true;
                }
                else
                {
                    byte[] oldKey = null;
                    while (oldKey == null)
                    {
                        var p = UI.InputBox(owner, "Encrypted notes in backup",
                            $"{enc.Count} note(s) in the backup are encrypted with a different master password.\nEnter the master password that was used when the backup was made\n(Cancel = skip the encrypted notes):", "", true);
                        if (p == null) break;
                        if (!Crypto.Verify(p, bs.MasterSalt, bs.MasterVerifier, out oldKey))
                        { oldKey = null; MessageBox.Show(owner, "Wrong password for this backup.", "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    }
                    if (oldKey != null && EnsureMasterKey(owner, false))
                    {
                        foreach (var n in enc)
                        {
                            try { n.EncryptedPayload = Crypto.Encrypt(Crypto.Decrypt(n.EncryptedPayload, oldKey), MasterKey); }
                            catch (Exception ex) { Program.Log(ex); incoming.Remove(n); skippedEncrypted++; }
                        }
                    }
                    else
                    {
                        foreach (var n in enc) incoming.Remove(n);
                        skippedEncrypted = enc.Count;
                    }
                }
            }

            // ---- notes
            int added = 0, updated = 0, kept = 0;
            foreach (var r in _reminders.Values.ToList()) r.Close();
            if (replace)
            {
                foreach (var f in Forms.ToList()) f.Dispose();
                Forms.Clear();
                Data.Notes.Clear();
                foreach (var n in incoming) { Data.Notes.Add(n); CreateAndShow(n); added++; }
            }
            else
            {
                foreach (var n in incoming)
                {
                    var existing = Forms.FirstOrDefault(f => f.Data.Id == n.Id);
                    if (existing == null)
                    {
                        // don't put an imported note exactly on top of an existing one
                        while (Data.Notes.Any(o => Math.Abs(o.X - n.X) < UI.S(10) && Math.Abs(o.Y - n.Y) < UI.S(10))) { n.X += UI.S(30); n.Y += UI.S(30); }
                        Data.Notes.Add(n); CreateAndShow(n); added++; continue;
                    }
                    if (n.Modified > existing.Data.Modified)
                    {
                        int idx = Data.Notes.IndexOf(existing.Data);
                        Forms.Remove(existing);
                        existing.Dispose();
                        if (idx >= 0) Data.Notes[idx] = n; else Data.Notes.Add(n);
                        CreateAndShow(n);
                        updated++;
                    }
                    else kept++;
                }
            }

            // ---- settings (appearance, alerts and e-mail – never the start-up choice of this PC)
            if (withSettings)
            {
                st.DefaultColorArgb = bs.DefaultColorArgb; st.DefaultFont = bs.DefaultFont; st.DefaultFontSize = bs.DefaultFontSize;
                st.DefaultAlertDays = bs.DefaultAlertDays; st.PlaySound = bs.PlaySound;
                st.EmailEnabled = bs.EmailEnabled; st.SmtpHost = bs.SmtpHost; st.SmtpPort = bs.SmtpPort; st.SmtpSsl = bs.SmtpSsl;
                st.SmtpUser = bs.SmtpUser; st.EmailFrom = bs.EmailFrom; st.EmailTo = bs.EmailTo;
                // the SMTP password is protected per Windows user – only usable on the same PC/account
                if (!string.IsNullOrEmpty(Crypto.Unprotect(bs.SmtpPasswordProtected))) st.SmtpPasswordProtected = bs.SmtpPasswordProtected;
            }

            Save();
            NotesChanged?.Invoke();
            _browser?.RefreshLists();

            var sb = new StringBuilder();
            sb.AppendLine(replace ? $"Replaced all notes with {added} note(s) from the backup." : $"Added {added} new note(s), updated {updated}, kept {kept} unchanged (your copy was newer or the same).");
            if (skippedEncrypted > 0) sb.AppendLine($"{skippedEncrypted} encrypted note(s) were skipped.");
            if (adoptedMaster) sb.AppendLine("Encrypted notes: the master password from the backup is now your master password.");
            if (withSettings) sb.AppendLine("Settings were imported.");
            if (safety != null) sb.AppendLine().AppendLine("Your previous notes were saved first to:").AppendLine(safety);
            MessageBox.Show(owner, sb.ToString(), "Import complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CreateAndShow(NoteData n)
        {
            var f = CreateForm(n);
            if (n.Mode == StickMode.Desktop && !n.Hidden) f.Show();
        }

        /// <summary>Exports every note as readable text (one file).</summary>
        public void ExportAllText(IWin32Window owner)
        {
            Save();
            using (var d = new SaveFileDialog { Title = "Export all notes as text", Filter = "Text file|*.txt", FileName = $"StickyNotesPro-notes-{DateTime.Now:yyyy-MM-dd}.txt" })
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine($"StickyNotes Pro {Program.Version} – {Forms.Count} notes – exported {DateTime.Now:f}").AppendLine();
                foreach (var f in Forms.OrderBy(x => x.Data.DisplayTitle))
                {
                    var n = f.Data;
                    sb.AppendLine(new string('=', 60));
                    sb.AppendLine(n.DisplayTitle);
                    sb.AppendLine($"Type: {n.Kind}   Modified: {n.Modified:g}" + (n.Tags.Count > 0 ? "   Tags: " + string.Join(", ", n.Tags) : "") +
                                  (n.Due.Enabled ? $"   Due: {n.Due.At:g}" : ""));
                    sb.AppendLine(new string('-', 60));
                    sb.AppendLine(n.Encrypted ? "[encrypted note]" : (n.PlainText ?? ""));
                    sb.AppendLine();
                }
                try { File.WriteAllText(d.FileName, sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.UTF8); }
                catch (Exception ex) { MessageBox.Show(owner, ex.Message); }
            }
        }

        /// <summary>Menu used by the tray, Notes browser and Settings.</summary>
        public ToolStripMenuItem BuildBackupMenu(IWin32Window owner, Func<IList<NoteData>> selected = null)
        {
            var m = new ToolStripMenuItem("Backup && transfer");
            m.DropDownItems.Add(new ToolStripMenuItem("Export all notes (backup file)…", null, (s, e) => ExportBackup(owner)));
            if (selected != null)
                m.DropDownItems.Add(new ToolStripMenuItem("Export selected notes…", null, (s, e) =>
                {
                    var sel = selected();
                    if (sel == null || sel.Count == 0) { UI.Info(owner, "Select one or more notes in the list first."); return; }
                    ExportBackup(owner, sel);
                }));
            m.DropDownItems.Add(new ToolStripMenuItem("Import from backup…", null, (s, e) => ImportBackup(owner)));
            m.DropDownItems.Add(new ToolStripSeparator());
            m.DropDownItems.Add(new ToolStripMenuItem("Export all as text file…", null, (s, e) => ExportAllText(owner)));
            m.DropDownItems.Add(new ToolStripMenuItem("Open automatic backups folder", null, (s, e) => OpenBackupFolder()));
            return m;
        }
    }

    /// <summary>Shows what is inside a backup and asks how to import it.</summary>
    internal class ImportDialog : Form
    {
        private readonly RadioButton _merge, _replace;
        private readonly CheckBox _settings;
        public bool Replace => _replace.Checked;
        public bool ImportSettings => _settings.Checked;

        public ImportDialog(BackupFile b, string path, int currentCount)
        {
            Text = "Import notes"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen; Font = UI.UiFont; Icon = AppController.AppIcon; TopMost = true;
            ClientSize = new Size(UI.S(460), UI.S(330));
            int x = UI.S(16), y = UI.S(14), w = UI.S(428);

            var head = new Label { Text = "Backup file", Font = new Font("Segoe UI", 11f, FontStyle.Bold), Left = x, Top = y, AutoSize = true };
            y += UI.S(28);
            var d = b.Data;
            int checklists = d.Notes.Count(n => n.Kind == NoteKind.Checklist), toggles = d.Notes.Count(n => n.Kind == NoteKind.Toggle), enc = d.Notes.Count(n => n.Encrypted);
            var info = new Label
            {
                Left = x, Top = y, Width = w, Height = UI.S(96), BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(UI.S(6)),
                BackColor = Color.FromArgb(250, 250, 245),
                Text = $"{Path.GetFileName(path)}\n" +
                       $"Notes: {d.Notes.Count}  ({d.Notes.Count - checklists - toggles} text, {checklists} checklist(s), {toggles} toggle list(s)" + (enc > 0 ? $", {enc} encrypted" : "") + ")\n" +
                       $"Made with version: {b.AppVersion}    Exported: {b.ExportedOn:g}\n" +
                       (string.IsNullOrEmpty(b.ComputerName) ? "" : $"Computer: {b.ComputerName}\n") +
                       $"You currently have {currentCount} note(s)."
            };
            y += UI.S(108);
            _merge = new RadioButton
            {
                Left = x, Top = y, Width = w, Height = UI.S(36), Checked = true,
                Text = "Merge – add the notes to my current notes (if the same note exists, the newer copy is kept)"
            };
            y += UI.S(40);
            _replace = new RadioButton
            {
                Left = x, Top = y, Width = w, Height = UI.S(36),
                Text = "Replace – delete my current notes and use only the notes from the backup"
            };
            y += UI.S(44);
            _settings = new CheckBox { Left = x, Top = y, Width = w, Height = UI.S(24), Text = "Also import settings (default color/font, alerts, e-mail settings)" };
            y += UI.S(28);
            var note = new Label { Left = x, Top = y, Width = w, Height = UI.S(20), ForeColor = Color.DimGray, Font = UI.SmallFont, Text = "A safety copy of your current notes is saved automatically before importing." };

            var ok = new Button { Text = "Import", DialogResult = DialogResult.OK, Width = UI.S(96), Height = UI.S(28), Left = ClientSize.Width - UI.S(212), Top = ClientSize.Height - UI.S(40), FlatStyle = FlatStyle.System };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = UI.S(96), Height = UI.S(28), Left = ClientSize.Width - UI.S(108), Top = ClientSize.Height - UI.S(40), FlatStyle = FlatStyle.System };
            _replace.CheckedChanged += (s, e) => ok.Text = _replace.Checked ? "Replace all" : "Import";
            ok.Click += (s, e) =>
            {
                if (_replace.Checked && currentCount > 0 &&
                    MessageBox.Show(this, $"Delete your {currentCount} current note(s) and replace them with the backup?\n(A safety copy is saved first.)",
                        "Replace all notes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    DialogResult = DialogResult.None;
            };
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { head, info, _merge, _replace, _settings, note, ok, cancel });
        }
    }
}
