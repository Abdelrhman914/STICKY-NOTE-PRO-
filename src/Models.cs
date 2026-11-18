using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using StickyNotesPro.Security;

namespace StickyNotesPro
{
    public enum NoteKind { Text, Checklist, Toggle }
    public enum StickMode { Desktop, Window }
    public enum SkinType { Plain, Ruled, Notebook, Grid, Dotted, Antique }

    /// <summary>A deadline / due date with alert settings. Used by checklist items and whole notes.</summary>
    public class Deadline
    {
        public bool Enabled;
        public DateTime At;
        public bool Alert = true;
        public int DaysBefore = 1;          // 0 = no early alert
        public bool AlertAtTime = true;      // also alert when the deadline arrives
        public bool Email;                   // also send by e-mail (if e-mail configured)
        public DateTime SetOn;               // date the deadline was set (for "x of y days" progress)
        public bool PreFired;
        public bool DueFired;
        public DateTime SnoozeUntil = DateTime.MinValue;

        public Deadline Clone() => (Deadline)MemberwiseClone();

        public void ResetAlerts()
        {
            PreFired = false; DueFired = false; SnoozeUntil = DateTime.MinValue;
            var now = DateTime.Now;
            // if early alert time already passed when deadline was set, don't fire it immediately
            if (DaysBefore > 0 && At.AddDays(-DaysBefore) <= now && At > now) { /* fire once - it's useful */ }
        }

        public int DaysLeft => (At.Date - DateTime.Today).Days;
        public int TotalDays => Math.Max(0, (At.Date - (SetOn == default ? DateTime.Today : SetOn.Date)).Days);

        public string StatusText(bool done, out Color color)
        {
            if (done) { color = Color.FromArgb(90, 120, 90); return "Completed"; }
            int d = DaysLeft;
            if (At < DateTime.Now && d <= 0)
            {
                color = Color.FromArgb(200, 30, 30);
                return d == 0 ? "Deadline passed today" : $"Overdue by {-d} day{(d == -1 ? "" : "s")}";
            }
            if (d == 0) { color = Color.FromArgb(215, 95, 0); return "Due today " + At.ToString("HH:mm"); }
            if (d == 1) { color = Color.FromArgb(215, 95, 0); return "1 day left (tomorrow)"; }
            color = d <= 3 ? Color.FromArgb(190, 110, 0) : Color.FromArgb(20, 120, 50);
            int total = TotalDays;
            return total > d ? $"{d} of {total} days left" : $"{d} days left";
        }

        public string AlertText()
        {
            if (!Alert) return "no alert";
            var parts = new List<string>();
            if (DaysBefore > 0) parts.Add(DaysBefore == 7 ? "1 week before" : $"{DaysBefore} day{(DaysBefore > 1 ? "s" : "")} before");
            if (AlertAtTime) parts.Add("at deadline");
            return parts.Count == 0 ? "no alert" : "alert " + string.Join(" + ", parts);
        }
    }

    public class ChecklistItem
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Text = "";
        public bool Done;
        public DateTime DoneOn;
        public bool IsHeading;      // collapsible section title inside a checklist
        public bool Collapsed;      // section collapsed (items under it hidden)
        public Deadline Due = new Deadline();
        public ChecklistItem Clone()
        {
            var c = (ChecklistItem)MemberwiseClone();
            c.Due = Due.Clone();
            return c;
        }
    }

    /// <summary>One expandable / collapsible block of a toggle-list note.</summary>
    public class ToggleItem
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Title = "";
        public string Rtf = "";
        public string PlainText = "";
        public bool Expanded = true;
    }

    public class NoteData
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Title = "";
        public NoteKind Kind = NoteKind.Text;
        public string Rtf = "";
        public string PlainText = "";
        public List<ChecklistItem> Items = new List<ChecklistItem>();
        public List<ToggleItem> Toggles = new List<ToggleItem>();
        public int ColorArgb = Palette.Default.ToArgb();
        public SkinType Skin = SkinType.Plain;
        public int Opacity = 100;
        public string FontName = "Segoe UI";
        public float FontSize = 11f;
        public int X = 200, Y = 200, W = 260, H = 240;
        public bool TopMost;
        public bool RolledUp;
        public bool Hidden;
        public bool Starred;
        public List<string> Tags = new List<string>();
        public List<string> Links = new List<string>();
        public bool ReadOnlyLock;
        public bool Encrypted;
        public string EncryptedPayload = "";
        public StickMode Mode = StickMode.Desktop;
        public string WinProcess = "";
        public string WinTitle = "";
        public int WinOffsetX = 40, WinOffsetY = 80;
        public Deadline Due = new Deadline();
        public DateTime Created = DateTime.Now;
        public DateTime Modified = DateTime.Now;

        [XmlIgnore] public Color Color { get => Color.FromArgb(ColorArgb); set => ColorArgb = value.ToArgb(); }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
                if (Encrypted) return "Encrypted note";
                string first = (PlainText ?? "").Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
                if (first != null) first = System.Text.RegularExpressions.Regex.Replace(first, @"^(\[[ xX]\]|##|[\u2610\u2611])\s*", "").Trim();
                if (string.IsNullOrEmpty(first)) return Kind == NoteKind.Checklist ? "Checklist" : Kind == NoteKind.Toggle ? "Toggle list" : "Note";
                return first.Length > 40 ? first.Substring(0, 40) + "…" : first;
            }
        }

        public bool MatchesWindow(string process, string title)
        {
            if (Mode != StickMode.Window) return false;
            if (string.IsNullOrWhiteSpace(WinProcess) && string.IsNullOrWhiteSpace(WinTitle)) return false;
            if (!string.IsNullOrWhiteSpace(WinProcess))
            {
                string p = WinProcess.Trim();
                if (!p.Contains("*") && !p.Contains("?") && !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p += ".exe";
                if (!Wildcard.IsMatch(process, p, false)) return false;
            }
            if (!string.IsNullOrWhiteSpace(WinTitle))
                if (!Wildcard.IsMatch(title, WinTitle.Trim(), true)) return false;
            return true;
        }
    }

    public class EncryptedContent
    {
        public string Rtf = "";
        public List<ChecklistItem> Items = new List<ChecklistItem>();
        public List<ToggleItem> Toggles = new List<ToggleItem>();
    }

    /// <summary>Export / transfer file (*.snpbackup). Newer versions can read older files and vice versa
    /// (unknown fields are ignored, missing fields get default values).</summary>
    [XmlRoot("StickyNotesProBackup")]
    public class BackupFile
    {
        public int FormatVersion = 1;
        public string AppVersion = Program.Version;
        public DateTime ExportedOn = DateTime.Now;
        public string ComputerName = Environment.MachineName;
        public int NoteCount;
        public AppData Data = new AppData();
    }

    public class AppSettings
    {
        public string MasterSalt = "";
        public string MasterVerifier = "";
        public bool StartWithWindows = true;
        public int DefaultColorArgb = Palette.Default.ToArgb();
        public string DefaultFont = "Segoe UI";
        public float DefaultFontSize = 11f;
        public int DefaultAlertDays = 1;
        public bool PlaySound = true;
        public bool EmailEnabled;
        public string SmtpHost = "smtp.gmail.com";
        public int SmtpPort = 587;
        public bool SmtpSsl = true;
        public string SmtpUser = "";
        public string SmtpPasswordProtected = "";
        public string EmailFrom = "";
        public string EmailTo = "";
        public bool FirstRunDone;
        public DateTime LastAutoBackup;
    }

    public class AppData
    {
        public List<NoteData> Notes = new List<NoteData>();
        public AppSettings Settings = new AppSettings();
    }

    public static class Palette
    {
        public static readonly Color Default = Color.FromArgb(255, 255, 247, 140);
        public static readonly (string Name, Color Color)[] Colors =
        {
            ("Yellow", Color.FromArgb(255, 247, 140)),
            ("Lemon", Color.FromArgb(255, 255, 190)),
            ("Green", Color.FromArgb(204, 240, 150)),
            ("Lime", Color.FromArgb(220, 240, 120)),
            ("Blue", Color.FromArgb(160, 232, 245)),
            ("Sky", Color.FromArgb(200, 225, 255)),
            ("Pink", Color.FromArgb(255, 180, 200)),
            ("Orange", Color.FromArgb(255, 200, 110)),
            ("Purple", Color.FromArgb(215, 190, 245)),
            ("White", Color.FromArgb(255, 255, 255)),
            ("Gray", Color.FromArgb(225, 225, 225)),
        };
        public static readonly Color Antique = Color.FromArgb(243, 229, 196);
    }

    public static class Wildcard
    {
        /// <summary>Wildcard match (* and ?). If the pattern has no wildcard and containsIfPlain is true, a substring match is used.</summary>
        public static bool IsMatch(string text, string pattern, bool containsIfPlain)
        {
            text = text ?? "";
            if (string.IsNullOrEmpty(pattern)) return true;
            pattern = InputValidator.CleanPattern(pattern);
            if (text.Length > 1024) text = text.Substring(0, 1024);
            if (!pattern.Contains("*") && !pattern.Contains("?"))
                return containsIfPlain
                    ? text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try { return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }

    public static class Storage
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickyNotesPro");
        public static string DataFile => Path.Combine(Folder, "notes.xml");

        public static AppData Load()
        {
            Directory.CreateDirectory(Folder);
            foreach (var f in new[] { DataFile, DataFile + ".bak" })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    using (var s = InputValidator.OpenDataFile(f))
                    using (var x = InputValidator.CreateSafeXmlReader(s))
                    {
                        var d = (AppData)new XmlSerializer(typeof(AppData)).Deserialize(x);
                        if (d != null) return Normalize(d);
                    }
                }
                catch { }
            }
            return new AppData();
        }

        public static AppData Normalize(AppData d)
        {
            d.Notes = d.Notes ?? new List<NoteData>();
            d.Settings = d.Settings ?? new AppSettings();
            foreach (var n in d.Notes)
            {
                n.Items = n.Items ?? new List<ChecklistItem>();
                n.Toggles = n.Toggles ?? new List<ToggleItem>();
                n.Tags = n.Tags ?? new List<string>();
                n.Links = n.Links ?? new List<string>();
                n.Due = n.Due ?? new Deadline();
                if (string.IsNullOrEmpty(n.Id)) n.Id = Guid.NewGuid().ToString("N");
                foreach (var i in n.Items) i.Due = i.Due ?? new Deadline();
            }
            InputValidator.ValidateData(d);      // strict validation of everything that came from disk
            return d;
        }

        public static AppData DeepCopy(AppData d)
        {
            var ser = new XmlSerializer(typeof(AppData));
            using (var ms = new MemoryStream())
            {
                ser.Serialize(ms, d);
                ms.Position = 0;
                return (AppData)ser.Deserialize(ms);
            }
        }

        public static string BackupFolder => Path.Combine(Folder, "backups");

        public static void SaveBackup(string path, BackupFile b)
        {
            b.NoteCount = b.Data.Notes.Count;
            string tmp = path + ".tmp";
            using (var s = File.Create(tmp))
                new XmlSerializer(typeof(BackupFile)).Serialize(s, b);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Reads a *.snpbackup file, or a raw notes.xml from any version.</summary>
        public static BackupFile LoadBackup(string path)
        {
            string root = "";
            using (var s0 = InputValidator.OpenDataFile(path))
            using (var x0 = InputValidator.CreateSafeXmlReader(s0))
                if (x0.MoveToContent() == System.Xml.XmlNodeType.Element) root = x0.LocalName;
            using (var s = InputValidator.OpenDataFile(path))
            using (var x = InputValidator.CreateSafeXmlReader(s))
            {
                if (root == "StickyNotesProBackup")
                {
                    var b = (BackupFile)new XmlSerializer(typeof(BackupFile)).Deserialize(x);
                    b.AppVersion = InputValidator.CleanLine(b.AppVersion, 40);
                    b.ComputerName = InputValidator.CleanLine(b.ComputerName, 64);
                    b.Data = Normalize(b.Data ?? new AppData());
                    return b;
                }
                if (root == "AppData")
                {
                    var d = Normalize((AppData)new XmlSerializer(typeof(AppData)).Deserialize(x));
                    return new BackupFile { AppVersion = "notes.xml", ExportedOn = File.GetLastWriteTime(path), ComputerName = "", Data = d, NoteCount = d.Notes.Count };
                }
            }
            throw new InvalidDataException("This file is not a StickyNotes Pro backup.");
        }

        public static void Save(AppData data)
        {
            Directory.CreateDirectory(Folder);
            string tmp = DataFile + ".tmp";
            using (var s = File.Create(tmp))
                new XmlSerializer(typeof(AppData)).Serialize(s, data);
            if (File.Exists(DataFile))
            {
                try { File.Copy(DataFile, DataFile + ".bak", true); } catch { }
                File.Delete(DataFile);
            }
            File.Move(tmp, DataFile);
        }

        public static string SerializeContent(EncryptedContent c)
        {
            using (var sw = new StringWriter())
            {
                new XmlSerializer(typeof(EncryptedContent)).Serialize(sw, c);
                return sw.ToString();
            }
        }

        public static EncryptedContent DeserializeContent(string xml)
        {
            using (var sr = new StringReader(xml))
            using (var x = InputValidator.CreateSafeXmlReader(sr))
            {
                var c = (EncryptedContent)new XmlSerializer(typeof(EncryptedContent)).Deserialize(x);
                InputValidator.ValidateContent(c);
                return c;
            }
        }
    }

    public static class Crypto
    {
        public static void CreateMaster(string password, out string saltB64, out string verifierB64, out byte[] key)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            Derive(password, salt, out key, out var ver);
            saltB64 = Convert.ToBase64String(salt);
            verifierB64 = Convert.ToBase64String(ver);
        }

        public static bool Verify(string password, string saltB64, string verifierB64, out byte[] key)
        {
            key = null;
            try
            {
                Derive(password, Convert.FromBase64String(saltB64), out var k, out var ver);
                if (Convert.ToBase64String(ver) != verifierB64) return false;
                key = k; return true;
            }
            catch { return false; }
        }

        private static void Derive(string password, byte[] salt, out byte[] key, out byte[] verifier)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256))
            {
                var bytes = kdf.GetBytes(64);
                key = bytes.Take(32).ToArray();
                verifier = bytes.Skip(32).ToArray();
            }
        }

        public static string Encrypt(string plain, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.GenerateIV();
                using (var enc = aes.CreateEncryptor())
                {
                    var data = Encoding.UTF8.GetBytes(plain);
                    var cipher = enc.TransformFinalBlock(data, 0, data.Length);
                    return Convert.ToBase64String(aes.IV.Concat(cipher).ToArray());
                }
            }
        }

        public static string Decrypt(string b64, byte[] key)
        {
            var all = Convert.FromBase64String(b64);
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.IV = all.Take(16).ToArray();
                using (var dec = aes.CreateDecryptor())
                {
                    var plain = dec.TransformFinalBlock(all, 16, all.Length - 16);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }

        public static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        public static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }
}
