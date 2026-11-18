using System;
using System.Text;
using System.Text.RegularExpressions;

namespace StickyNotesPro.Security
{
    /// <summary>
    /// Removes active content from RTF before it reaches the RichEdit control.
    ///  • {\object …}           embedded / linked OLE objects (Packager, Equation Editor, OLE2Link, ActiveX…) are
    ///                          replaced by their static {\result} picture – nothing can be activated or executed.
    ///  • {\*\objdata …}, {\*\objclass …}, {\*\datastore …}, {\*\oleclsid …} and similar destinations are dropped.
    ///  • {\field …}            only HYPERLINK fields to http/https/mailto survive; any other field keeps only its
    ///                          visible result text.
    ///  • Structural limits     size, nesting depth and \bin lengths are bounded to stop parser-exhaustion attacks.
    /// Plain formatting, pictures ({\pict}), colours, fonts, bullets etc. are preserved.
    /// </summary>
    internal static class RtfSanitizer
    {
        public const int MaxRtfChars = 64 * 1024 * 1024;
        private const int MaxDepth = 400;
        private const int MaxRecursion = 16;

        private static readonly string[] DropDestinations =
        {
            "objdata", "objclass", "objname", "objalias", "objsect", "oleclsid", "datastore", "objtime",
            "xmlopen", "mmathPict", "themedata", "colorschememapping", "datafield", "fldtype", "pgdsctbl",
        };

        public static bool LooksLikeRtf(string s) => !string.IsNullOrEmpty(s) && s.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);

        /// <summary>Returns safe RTF. Throws <see cref="FormatException"/> when the input is not usable RTF.</summary>
        public static string Sanitize(string rtf)
        {
            if (rtf == null) return "";
            if (rtf.Length > MaxRtfChars) throw new FormatException("Rich text is too large.");
            if (!LooksLikeRtf(rtf)) throw new FormatException("Not rich text.");
            // fast path: nothing active present
            if (rtf.IndexOf(@"\object", StringComparison.Ordinal) < 0 && rtf.IndexOf(@"\field", StringComparison.Ordinal) < 0 &&
                rtf.IndexOf(@"\obj", StringComparison.Ordinal) < 0 && rtf.IndexOf(@"\datastore", StringComparison.Ordinal) < 0 &&
                rtf.IndexOf(@"\oleclsid", StringComparison.Ordinal) < 0 && !HasDeepNesting(rtf))
                return rtf;
            var sb = new StringBuilder(rtf.Length);
            Process(rtf, 0, rtf.Length, sb, 0);
            return sb.ToString();
        }

        /// <summary>Like <see cref="Sanitize"/>, but returns null instead of throwing.</summary>
        public static string TrySanitize(string rtf)
        {
            try { return Sanitize(rtf); } catch { return null; }
        }

        private static bool HasDeepNesting(string s)
        {
            int d = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') { i++; continue; }
                if (c == '{' && ++d > MaxDepth) return true;
                if (c == '}') d--;
            }
            return false;
        }

        private static void Process(string s, int start, int end, StringBuilder o, int rec)
        {
            if (rec > MaxRecursion) throw new FormatException("Rich text is nested too deeply.");
            int depth = 0;
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (TryBin(s, i, end, out int binEnd)) { o.Append(s, i, binEnd - i); i = binEnd; continue; }
                    o.Append(c);
                    if (i + 1 < end) o.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '{')
                {
                    int close = MatchingBrace(s, i, end);
                    LeadingWords(s, i + 1, close, out bool star, out string w1);
                    if (w1 == "object")
                    {
                        // keep only the static picture representation
                        int r = FindChildGroup(s, i + 1, close, "result");
                        if (r >= 0)
                        {
                            int rc = MatchingBrace(s, r, close);
                            int body = SkipControlWord(s, r + 1, rc);
                            o.Append('{');
                            Process(s, body, rc, o, rec + 1);
                            o.Append('}');
                        }
                        i = close + 1;
                        continue;
                    }
                    if (w1 == "field")
                    {
                        if (IsSafeHyperlinkField(s, i, close)) { o.Append('{'); Process(s, i + 1, close, o, rec + 1); o.Append('}'); }
                        else
                        {
                            int r = FindChildGroup(s, i + 1, close, "fldrslt");
                            if (r >= 0)
                            {
                                int rc = MatchingBrace(s, r, close);
                                int body = SkipControlWord(s, r + 1, rc);
                                o.Append('{');
                                Process(s, body, rc, o, rec + 1);
                                o.Append('}');
                            }
                        }
                        i = close + 1;
                        continue;
                    }
                    if (Array.IndexOf(DropDestinations, w1) >= 0 || (w1 != null && w1.StartsWith("obj", StringComparison.Ordinal) && star))
                    {
                        i = close + 1;
                        continue;
                    }
                    if (++depth > MaxDepth) throw new FormatException("Rich text is nested too deeply.");
                    o.Append(c);
                    i++;
                    continue;
                }
                if (c == '}') depth--;
                o.Append(c);
                i++;
            }
        }

        /// <summary>\binN – N raw bytes follow; skip them so braces inside binary data are not interpreted.</summary>
        private static bool TryBin(string s, int i, int end, out int after)
        {
            after = i;
            if (i + 4 > end || string.CompareOrdinal(s, i, @"\bin", 0, 4) != 0) return false;
            int p = i + 4; long n = 0; int digits = 0;
            while (p < end && char.IsDigit(s[p]) && digits < 10) { n = n * 10 + (s[p] - '0'); p++; digits++; }
            if (digits == 0) return false;
            if (p < end && s[p] == ' ') p++;
            if (n > end - p) throw new FormatException("Invalid binary block in rich text.");
            after = p + (int)n;
            return true;
        }

        private static int MatchingBrace(string s, int open, int end)
        {
            int d = 0;
            for (int i = open; i < end; i++)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (TryBin(s, i, end, out int be)) { i = be - 1; continue; }
                    i++;
                    continue;
                }
                if (c == '{') { if (++d > MaxDepth * 4) throw new FormatException("Rich text is nested too deeply."); }
                else if (c == '}' && --d == 0) return i;
            }
            return end - 1 >= open ? end - 1 : open;   // unbalanced: treat rest as the group
        }

        private static void LeadingWords(string s, int p, int end, out bool star, out string word)
        {
            star = false; word = null;
            while (p < end && (s[p] == '\r' || s[p] == '\n' || s[p] == ' ')) p++;
            if (p + 1 < end && s[p] == '\\' && s[p + 1] == '*') { star = true; p += 2; while (p < end && (s[p] == '\r' || s[p] == '\n' || s[p] == ' ')) p++; }
            if (p < end && s[p] == '\\')
            {
                int q = p + 1;
                while (q < end && q - p < 40 && char.IsLetter(s[q])) q++;
                if (q > p + 1) word = s.Substring(p + 1, q - p - 1);
            }
        }

        private static int SkipControlWord(string s, int p, int end)
        {
            while (p < end && (s[p] == '\r' || s[p] == '\n' || s[p] == ' ')) p++;
            if (p < end && s[p] == '\\')
            {
                p++;
                while (p < end && char.IsLetter(s[p])) p++;
                while (p < end && (char.IsDigit(s[p]) || s[p] == '-')) p++;
                if (p < end && s[p] == ' ') p++;
            }
            return p;
        }

        /// <summary>Finds a direct child group {\word …} (also {\*\word …}).</summary>
        private static int FindChildGroup(string s, int from, int end, string word)
        {
            int i = from;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\') { if (TryBin(s, i, end, out int be)) { i = be; continue; } i += 2; continue; }
                if (c == '{')
                {
                    int close = MatchingBrace(s, i, end);
                    LeadingWords(s, i + 1, close, out _, out string w);
                    if (w == word) return i;
                    i = close + 1;
                    continue;
                }
                i++;
            }
            return -1;
        }

        private static readonly Regex HyperlinkRx = new Regex(@"^\s*HYPERLINK\s+""?([^""\s]+)""?\s*$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

        private static bool IsSafeHyperlinkField(string s, int open, int close)
        {
            int inst = FindChildGroup(s, open + 1, close, "fldinst");
            if (inst < 0) return false;
            int ic = MatchingBrace(s, inst, close);
            string text = PlainText(s, inst + 1, ic);
            if (text.Length > 4096) return false;
            try
            {
                var m = HyperlinkRx.Match(text);
                return m.Success && InputValidator.IsSafeWebUrl(m.Groups[1].Value, out _);
            }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>Visible text of an RTF fragment (control words and group braces removed).</summary>
        private static string PlainText(string s, int start, int end)
        {
            var sb = new StringBuilder();
            for (int i = start; i < end && sb.Length < 8192; i++)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (i + 1 < end && (s[i + 1] == '\\' || s[i + 1] == '{' || s[i + 1] == '}')) { sb.Append(s[i + 1]); i++; continue; }
                    if (i + 1 < end && s[i + 1] == '*') { i++; continue; }
                    int q = i + 1;
                    while (q < end && char.IsLetter(s[q])) q++;
                    while (q < end && (char.IsDigit(s[q]) || s[q] == '-')) q++;
                    if (q < end && s[q] == ' ') q++;
                    i = q - 1;
                    continue;
                }
                if (c == '{' || c == '}' || c == '\r' || c == '\n') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
