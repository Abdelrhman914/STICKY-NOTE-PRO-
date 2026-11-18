using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace StickyNotesPro.Security
{
    /// <summary>
    /// Central, strict validation for every piece of external input:
    /// data files and backups, command-line arguments, clipboard / drag-drop, dialogs, hyperlinks,
    /// file links, window-match patterns and e-mail settings.
    /// Rule: whitelist what is allowed, bound every size, never pass unvalidated text to the shell,
    /// the XML parser, the regex engine or the RichEdit control.
    /// </summary>
    internal static class InputValidator
    {
        public const int MaxTitle = 200, MaxTag = 50, MaxTags = 50, MaxLinks = 200, MaxPath = 1024;
        public const int MaxNotes = 20000, MaxItems = 10000, MaxItemText = 4000, MaxToggles = 2000;
        public const int MaxPlainText = 8 * 1024 * 1024, MaxPattern = 260, MaxFontName = 64;
        public const long MaxDataFileBytes = 512L * 1024 * 1024;
        public const int MaxEncryptedPayload = 96 * 1024 * 1024;

        private static readonly Regex HexId = new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex HostRx = new Regex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant);
        private static readonly string[] ReservedNames = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        /// <summary>File types that execute code when opened.</summary>
        private static readonly HashSet<string> ExecutableExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".wsc",
            ".msi", ".msp", ".mst", ".msc", ".hta", ".cpl", ".jar", ".reg", ".lnk", ".url", ".scf", ".inf", ".application", ".appref-ms",
            ".gadget", ".dll", ".ocx", ".sys", ".drv", ".chm", ".hlp", ".iso", ".img", ".vhd", ".vhdx", ".appx", ".msix", ".appinstaller",
            ".library-ms", ".search-ms", ".settingcontent-ms", ".diagcab", ".py", ".pyw", ".rb", ".pl", ".sh", ".xll", ".xlam", ".docm", ".xlsm", ".pptm",
        };

        // ------------------------------------------------------------------ text

        /// <summary>Single-line text: removes control characters and bidi-override characters (filename spoofing), trims, bounds length.</summary>
        public static string CleanLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (char c in s)
            {
                if (sb.Length >= max) break;
                if (char.IsControl(c)) { if (c == '\t') sb.Append(' '); continue; }
                if ((c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069') || c == '\uFEFF' || c == '\uFFFE' || c == '\uFFFF') continue;
                sb.Append(c);
            }
            return StripLoneSurrogates(sb.ToString()).Trim();
        }

        /// <summary>Multi-line text: keeps TAB / CR / LF, removes other control characters, bounds length.</summary>
        public static string CleanMultiline(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > max) s = s.Substring(0, max);
            bool dirty = false;
            foreach (char c in s) if ((char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') || c == '\uFFFE' || c == '\uFFFF') { dirty = true; break; }
            if (!dirty) return StripLoneSurrogates(s);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!((char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') || c == '\uFFFE' || c == '\uFFFF')) sb.Append(c);
            return StripLoneSurrogates(sb.ToString());
        }

        private static string StripLoneSurrogates(string s)
        {
            bool bad = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i])) { if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; } bad = true; break; }
                if (char.IsLowSurrogate(s[i])) { bad = true; break; }
            }
            if (!bad) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(s[i]).Append(s[i + 1]); i++; }
                else if (!char.IsSurrogate(s[i])) sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>Text for an e-mail header: no CR/LF (header injection), bounded.</summary>
        public static string SafeHeader(string s, int max = 200) => CleanLine((s ?? "").Replace('\r', ' ').Replace('\n', ' '), max);

        /// <summary>Rich text for a RichEdit control. Returns sanitized RTF, or null when it is not acceptable RTF.</summary>
        public static string SafeRtf(string rtf) => RtfSanitizer.TrySanitize(rtf);

        // ------------------------------------------------------------------ URLs, files, e-mail, patterns

        /// <summary>Only http, https and mailto links may be opened from note text.</summary>
        public static bool IsSafeWebUrl(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) return false;
            url = url.Trim();
            foreach (char c in url) if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '"' || c == '<' || c == '>' || c == '`') return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            {
                if (string.IsNullOrEmpty(u.Host) || u.HostNameType == UriHostNameType.Unknown) return false;
                uri = u; return true;
            }
            if (u.Scheme == Uri.UriSchemeMailto)
            {
                string addr = Uri.UnescapeDataString(u.GetComponents(UriComponents.UserInfo | UriComponents.Host, UriFormat.UriEscaped) ?? "");
                if (addr.Length == 0) addr = u.AbsoluteUri.Substring(7).Split('?')[0];
                if (!IsValidEmail(Uri.UnescapeDataString(addr))) return false;
                uri = u; return true;
            }
            return false;
        }

        /// <summary>
        /// Validates a local / UNC path: absolute, no device namespaces (\\?\, \\.\), no alternate data streams,
        /// no reserved device names, no control characters, bounded length. Returns the normalised full path.
        /// </summary>
        public static bool TryValidatePath(string path, out string full, out string error)
        {
            full = null; error = "Invalid path.";
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = path.Trim().Trim('"');
            if (path.Length > MaxPath) { error = "Path is too long."; return false; }
            foreach (char c in path) if (char.IsControl(c) || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?') return false;
            if (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") || path.StartsWith("//?/") || path.StartsWith("//./")) { error = "Device paths are not allowed."; return false; }
            bool unc = path.StartsWith(@"\\");
            bool drive = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            if (!unc && !drive) { error = "Only absolute paths are allowed."; return false; }
            if (path.IndexOf(':', 2) >= 0) { error = "Alternate data streams are not allowed."; return false; }
            try { full = Path.GetFullPath(path); } catch { return false; }
            foreach (var seg in full.Split('\\', '/'))
            {
                if (seg.Length == 0) continue;
                string name = seg.Split('.')[0].TrimEnd(' ');
                if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { error = "Reserved device names are not allowed."; full = null; return false; }
            }
            error = "";
            return true;
        }

        public static bool IsExecutableType(string path)
        {
            try { return ExecutableExt.Contains(Path.GetExtension(path ?? "").TrimEnd('.', ' ')); }
            catch { return true; }
        }

        public static bool IsValidEmail(string e)
        {
            if (string.IsNullOrWhiteSpace(e) || e.Length > 254) return false;
            if (e.IndexOfAny(new[] { '\r', '\n', ',', ';', '<', '>', '"', ' ' }) >= 0) return false;
            try { var m = new MailAddress(e); return m.Address == e.Trim() && m.Host.Contains("."); }
            catch { return false; }
        }

        public static bool IsValidHost(string h) => !string.IsNullOrWhiteSpace(h) && HostRx.IsMatch(h.Trim());

        /// <summary>Window-match wildcard pattern: printable, bounded, no runs of '*'.</summary>
        public static string CleanPattern(string p)
        {
            p = CleanLine(p, MaxPattern);
            while (p.Contains("**")) p = p.Replace("**", "*");
            return p;
        }

        // ------------------------------------------------------------------ XML

        /// <summary>XML reader that refuses DTDs / external entities (XXE, billion laughs) and bounds document size.</summary>
        public static XmlReader CreateSafeXmlReader(Stream s)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = MaxDataFileBytes,
                MaxCharactersInDocument = MaxDataFileBytes,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                CloseInput = false,
            };
            return XmlReader.Create(s, settings);
        }

        public static XmlReader CreateSafeXmlReader(TextReader t)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = MaxDataFileBytes,
                MaxCharactersInDocument = MaxDataFileBytes,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
            };
            return XmlReader.Create(t, settings);
        }

        /// <summary>Opens a data / backup file for reading after checking type and size.</summary>
        public static FileStream OpenDataFile(string path)
        {
            if (!TryValidatePath(path, out var full, out var err)) throw new InvalidDataException(err);
            var fi = new FileInfo(full);
            if (!fi.Exists) throw new FileNotFoundException("File not found.", full);
            if ((fi.Attributes & FileAttributes.Directory) != 0) throw new InvalidDataException("Not a file.");
            if (fi.Length > MaxDataFileBytes) throw new InvalidDataException("The file is too large to be a StickyNotes Pro data file.");
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        // ------------------------------------------------------------------ data model

        /// <summary>Validates and repairs a whole data set loaded from disk or from a backup. Never throws.</summary>
        public static void ValidateData(AppData d)
        {
            if (d.Notes.Count > MaxNotes) d.Notes.RemoveRange(MaxNotes, d.Notes.Count - MaxNotes);
            d.Notes.RemoveAll(n => n == null);
            var ids = new HashSet<string>();
            foreach (var n in d.Notes) ValidateNote(n, ids);
            ValidateSettings(d.Settings);
        }

        public static void ValidateNote(NoteData n, HashSet<string> ids = null)
        {
            if (!HexId.IsMatch(n.Id ?? "") || (ids != null && !ids.Add(n.Id))) { n.Id = Guid.NewGuid().ToString("N"); ids?.Add(n.Id); }
            if (!Enum.IsDefined(typeof(NoteKind), n.Kind)) n.Kind = NoteKind.Text;
            if (!Enum.IsDefined(typeof(SkinType), n.Skin)) n.Skin = SkinType.Plain;
            if (!Enum.IsDefined(typeof(StickMode), n.Mode)) n.Mode = StickMode.Desktop;
            n.Title = CleanLine(n.Title, MaxTitle);
            n.PlainText = CleanMultiline(n.PlainText, MaxPlainText);
            n.Rtf = ValidateRtfField(n.Rtf);
            n.ColorArgb = (int)(0xFF000000u | ((uint)n.ColorArgb & 0x00FFFFFFu));
            n.Opacity = Clamp(n.Opacity, 20, 100);
            n.FontName = CleanLine(n.FontName, MaxFontName);
            if (n.FontName.Length == 0) n.FontName = "Segoe UI";
            if (float.IsNaN(n.FontSize) || float.IsInfinity(n.FontSize)) n.FontSize = 11f;
            n.FontSize = Math.Max(6f, Math.Min(96f, n.FontSize));
            n.X = Clamp(n.X, -32000, 32000); n.Y = Clamp(n.Y, -32000, 32000);
            n.W = Clamp(n.W, 60, 6000); n.H = Clamp(n.H, 30, 6000);
            n.WinOffsetX = Clamp(n.WinOffsetX, -10000, 10000); n.WinOffsetY = Clamp(n.WinOffsetY, -10000, 10000);
            n.WinProcess = CleanPattern(n.WinProcess);
            n.WinTitle = CleanPattern(n.WinTitle);

            n.Tags = (n.Tags ?? new List<string>()).Select(t => CleanLine(t, MaxTag)).Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTags).ToList();
            var links = new List<string>();
            foreach (var l in n.Links ?? new List<string>())
                if (links.Count < MaxLinks && TryValidatePath(l, out var full, out _) && !links.Contains(full, StringComparer.OrdinalIgnoreCase)) links.Add(full);
            n.Links = links;

            if (n.Encrypted)
            {
                if (string.IsNullOrEmpty(n.EncryptedPayload) || n.EncryptedPayload.Length > MaxEncryptedPayload || !IsBase64(n.EncryptedPayload))
                    n.EncryptedPayload = "";      // stays "encrypted" but unreadable – never decrypt garbage
            }
            else n.EncryptedPayload = "";

            ValidateDeadline(n.Due);
            ValidateItems(n.Items);
            ValidateToggles(n.Toggles);
        }

        public static void ValidateContent(EncryptedContent c)
        {
            if (c == null) return;
            c.Rtf = ValidateRtfField(c.Rtf);
            c.Items = c.Items ?? new List<ChecklistItem>();
            c.Toggles = c.Toggles ?? new List<ToggleItem>();
            ValidateItems(c.Items);
            ValidateToggles(c.Toggles);
        }

        private static void ValidateItems(List<ChecklistItem> items)
        {
            if (items == null) return;
            items.RemoveAll(i => i == null);
            if (items.Count > MaxItems) items.RemoveRange(MaxItems, items.Count - MaxItems);
            foreach (var i in items)
            {
                if (!HexId.IsMatch(i.Id ?? "")) i.Id = Guid.NewGuid().ToString("N");
                i.Text = CleanLine(i.Text, MaxItemText);
                i.Due = i.Due ?? new Deadline();
                ValidateDeadline(i.Due);
            }
        }

        private static void ValidateToggles(List<ToggleItem> toggles)
        {
            if (toggles == null) return;
            toggles.RemoveAll(t => t == null);
            if (toggles.Count > MaxToggles) toggles.RemoveRange(MaxToggles, toggles.Count - MaxToggles);
            foreach (var t in toggles)
            {
                if (!HexId.IsMatch(t.Id ?? "")) t.Id = Guid.NewGuid().ToString("N");
                t.Title = CleanLine(t.Title, MaxItemText);
                t.PlainText = CleanMultiline(t.PlainText, MaxPlainText);
                t.Rtf = ValidateRtfField(t.Rtf);
            }
        }

        private static string ValidateRtfField(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return "";
            if (!RtfSanitizer.LooksLikeRtf(rtf)) return CleanMultiline(rtf, MaxPlainText);    // stored as plain text
            return RtfSanitizer.TrySanitize(rtf) ?? "";
        }

        private static void ValidateDeadline(Deadline d)
        {
            if (d == null) return;
            d.DaysBefore = Clamp(d.DaysBefore, 0, 30);
        }

        private static void ValidateSettings(AppSettings s)
        {
            if (s == null) return;
            if (!IsBase64(s.MasterSalt ?? "") || !IsBase64(s.MasterVerifier ?? "")) { s.MasterSalt = ""; s.MasterVerifier = ""; }
            s.DefaultColorArgb = (int)(0xFF000000u | ((uint)s.DefaultColorArgb & 0x00FFFFFFu));
            s.DefaultFont = CleanLine(s.DefaultFont, MaxFontName);
            if (s.DefaultFont.Length == 0) s.DefaultFont = "Segoe UI";
            if (float.IsNaN(s.DefaultFontSize) || float.IsInfinity(s.DefaultFontSize)) s.DefaultFontSize = 11f;
            s.DefaultFontSize = Math.Max(6f, Math.Min(96f, s.DefaultFontSize));
            s.DefaultAlertDays = Clamp(s.DefaultAlertDays, 0, 30);
            s.SmtpHost = CleanLine(s.SmtpHost, 253);
            if (s.SmtpHost.Length > 0 && !IsValidHost(s.SmtpHost)) { s.SmtpHost = ""; s.EmailEnabled = false; }
            if (s.SmtpPort < 1 || s.SmtpPort > 65535) s.SmtpPort = 587;
            s.SmtpUser = CleanLine(s.SmtpUser, 254);
            s.EmailFrom = CleanLine(s.EmailFrom, 254);
            s.EmailTo = CleanLine(s.EmailTo, 254);
            if (s.EmailFrom.Length > 0 && !IsValidEmail(s.EmailFrom)) s.EmailFrom = "";
            if (s.EmailTo.Length > 0 && !IsValidEmail(s.EmailTo)) s.EmailTo = "";
            if (s.SmtpPasswordProtected != null && s.SmtpPasswordProtected.Length > 0 && (!IsBase64(s.SmtpPasswordProtected) || s.SmtpPasswordProtected.Length > 8192)) s.SmtpPasswordProtected = "";
        }

        public static bool IsBase64(string s)
        {
            if (s == null) return false;
            if (s.Length == 0) return true;
            if (s.Length % 4 != 0) return false;
            foreach (char c in s) if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '+' || c == '/' || c == '=')) return false;
            return true;
        }

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
