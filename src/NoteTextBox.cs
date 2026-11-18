using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>RichTextBox using RichEdit 8 (msftedit.dll) with Windows spell checking and paper "skins".</summary>
    internal partial class NoteTextBox : RichTextBox
    {
        private static bool _msftLoaded;
        private SkinType _skin;

        public SkinType Skin
        {
            get => _skin;
            set { _skin = value; ApplyMargins(); Invalidate(); }
        }

        public NoteTextBox()
        {
            BorderStyle = BorderStyle.None;
            DetectUrls = true;
            ScrollBars = RichTextBoxScrollBars.Vertical;
            HideSelection = false;
            ShortcutsEnabled = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                try
                {
                    if (!_msftLoaded) _msftLoaded = Native.LoadLibrary("msftedit.dll") != IntPtr.Zero;
                    if (_msftLoaded) cp.ClassName = "RICHEDIT50W";
                }
                catch { }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                var v = Environment.OSVersion.Version;
                if (v.Major > 6 || (v.Major == 6 && v.Minor >= 2))
                {
                    int style = Native.SES_USECTF | Native.SES_CTFALLOWEMBED | Native.SES_CTFALLOWSMARTTAG | Native.SES_CTFALLOWPROOFING;
                    Native.SendMessage(Handle, Native.EM_SETEDITSTYLE, (IntPtr)style, (IntPtr)style);
                    int opts = (int)Native.SendMessage(Handle, Native.EM_GETLANGOPTIONS, IntPtr.Zero, IntPtr.Zero);
                    opts |= Native.IMF_SPELLCHECKING;
                    Native.SendMessage(Handle, Native.EM_SETLANGOPTIONS, IntPtr.Zero, (IntPtr)opts);
                }
            }
            catch { }
            ApplyMargins();
        }

        private void ApplyMargins()
        {
            if (!IsHandleCreated) return;
            int left = _skin == SkinType.Notebook ? UI.S(30) : UI.S(4);
            int right = UI.S(4);
            Native.SendMessage(Handle, Native.EM_SETMARGINS, (IntPtr)(Native.EC_LEFTMARGIN | Native.EC_RIGHTMARGIN), (IntPtr)((right << 16) | left));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            _textCache = null;
            base.OnTextChanged(e);
            if (_skin != SkinType.Plain && _skin != SkinType.Antique) Invalidate();
        }

        protected override void OnVScroll(EventArgs e)
        {
            base.OnVScroll(e);
            if (_skin != SkinType.Plain && _skin != SkinType.Antique) Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0302 /*WM_PASTE*/) { SafePaste(); m.Result = IntPtr.Zero; return; }
            if (HandleCheckboxMessage(ref m)) return;
            base.WndProc(ref m);
            if (m.Msg == Native.WM_PAINT && _skin != SkinType.Plain && _skin != SkinType.Antique)
            {
                try { DrawSkin(); } catch { }
            }
        }

        private void DrawSkin()
        {
            using (var g = Graphics.FromHwnd(Handle))
            {
                int lineH = Math.Max(UI.S(12), TextRenderer.MeasureText("Ag", Font).Height);
                int startY = 0;
                try
                {
                    int ci = GetCharIndexFromPosition(new Point(2, 2));
                    int line = GetLineFromCharIndex(ci);
                    int first = GetFirstCharIndexFromLine(line);
                    if (first >= 0) startY = GetPositionFromCharIndex(first).Y;
                }
                catch { }
                int y = startY + lineH - 1;
                while (y - lineH > 0) y -= lineH;
                var lineColor = Color.FromArgb(70, 60, 110, 180);
                using (var pen = new Pen(lineColor))
                using (var dot = new SolidBrush(Color.FromArgb(110, 90, 90, 90)))
                {
                    if (_skin == SkinType.Ruled || _skin == SkinType.Notebook || _skin == SkinType.Grid)
                        for (int yy = y; yy < ClientSize.Height; yy += lineH) g.DrawLine(pen, 0, yy, ClientSize.Width, yy);
                    if (_skin == SkinType.Grid)
                        for (int xx = UI.S(4); xx < ClientSize.Width; xx += lineH) g.DrawLine(pen, xx, 0, xx, ClientSize.Height);
                    if (_skin == SkinType.Dotted)
                    {
                        int step = Math.Max(UI.S(10), lineH / 2 + 2);
                        for (int yy = y; yy < ClientSize.Height; yy += lineH)
                            for (int xx = UI.S(4); xx < ClientSize.Width; xx += step) g.FillRectangle(dot, xx, yy, Math.Max(1, UI.S(1.5)), Math.Max(1, UI.S(1.5)));
                    }
                    if (_skin == SkinType.Notebook)
                        using (var red = new Pen(Color.FromArgb(150, 220, 60, 60)))
                        {
                            g.DrawLine(red, UI.S(22), 0, UI.S(22), ClientSize.Height);
                            g.DrawLine(red, UI.S(24), 0, UI.S(24), ClientSize.Height);
                        }
                }
            }
        }

        // ---------- Formatting helpers ----------

        public void ToggleEffect(uint mask)
        {
            var cf = new Native.CHARFORMAT2 { cbSize = Marshal.SizeOf(typeof(Native.CHARFORMAT2)), szFaceName = "" };
            Native.SendMessage(Handle, Native.EM_GETCHARFORMAT, (IntPtr)Native.SCF_SELECTION, ref cf);
            bool allOn = (cf.dwMask & mask) != 0 && (cf.dwEffects & mask) != 0;
            var set = new Native.CHARFORMAT2 { cbSize = Marshal.SizeOf(typeof(Native.CHARFORMAT2)), dwMask = mask, dwEffects = allOn ? 0 : mask, szFaceName = "" };
            Native.SendMessage(Handle, Native.EM_SETCHARFORMAT, (IntPtr)Native.SCF_SELECTION, ref set);
            OnTextChanged(EventArgs.Empty);
        }

        public bool HasEffect(uint mask)
        {
            if (!IsHandleCreated) return false;
            var cf = new Native.CHARFORMAT2 { cbSize = Marshal.SizeOf(typeof(Native.CHARFORMAT2)), szFaceName = "" };
            Native.SendMessage(Handle, Native.EM_GETCHARFORMAT, (IntPtr)Native.SCF_SELECTION, ref cf);
            return (cf.dwMask & mask) != 0 && (cf.dwEffects & mask) != 0;
        }

        public int WordCount => Regex.Matches(Text ?? "", @"\S+").Count;

        public void InsertImage(Image img)
        {
            int maxW = Math.Max(UI.S(60), ClientSize.Width - UI.S(20));
            Image toInsert = img;
            if (img.Width > maxW)
            {
                int h = (int)(img.Height * (maxW / (double)img.Width));
                toInsert = new Bitmap(img, new Size(maxW, Math.Max(1, h)));
            }
            System.Windows.Forms.IDataObject backup = null;
            try { backup = Clipboard.GetDataObject(); } catch { }
            string backupText = null;
            try { if (backup != null && backup.GetDataPresent(DataFormats.UnicodeText)) backupText = (string)backup.GetData(DataFormats.UnicodeText); } catch { }
            try
            {
                Clipboard.SetImage(toInsert);
                Paste(DataFormats.GetFormat(DataFormats.Bitmap));
            }
            finally
            {
                try { if (backupText != null) Clipboard.SetText(backupText); } catch { }
            }
        }

        public void PastePlain()
        {
            if (ReadOnly) return;
            try { if (Clipboard.ContainsText()) SelectedText = Security.InputValidator.CleanMultiline(Clipboard.GetText(), Security.InputValidator.MaxPlainText); } catch { }
        }

        /// <summary>
        /// The only paste path. Rich text from the clipboard is sanitized (embedded OLE objects / packages,
        /// non-web hyperlink fields etc. are removed) before it reaches RichEdit; images are inserted as plain
        /// bitmaps; anything else is pasted as plain text.
        /// </summary>
        public void SafePaste()
        {
            if (ReadOnly) return;
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                if (data.GetDataPresent(DataFormats.Rtf))
                {
                    string safe = Security.InputValidator.SafeRtf(data.GetData(DataFormats.Rtf) as string);
                    if (safe != null) { SelectedRtf = safe; return; }
                }
                if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)) { PastePlain(); return; }
                if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage()) if (img != null) InsertImage(img);
                }
            }
            catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert)) { SafePaste(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.V)) { PastePlain(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>Returns start/length of the word at a character index.</summary>
        public bool WordAt(int index, out int start, out int length)
        {
            string t = Text ?? "";
            start = 0; length = 0;
            if (index < 0 || index > t.Length) return false;
            int s = index, e = index;
            while (s > 0 && (char.IsLetter(t[s - 1]) || t[s - 1] == '\'')) s--;
            while (e < t.Length && (char.IsLetter(t[e]) || t[e] == '\'')) e++;
            if (e <= s) return false;
            start = s; length = e - s;
            return true;
        }
    }

    // ---------------- Windows Spell Checking API (Windows 8+) ----------------

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellCheckerFactory
    {
        IEnumString get_SupportedLanguages();
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
        [return: MarshalAs(UnmanagedType.IUnknown)] object Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString value);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    internal class SpellCheckerFactoryClass { }

    internal static class SpellService
    {
        private static ISpellChecker _checker;
        private static bool _tried;

        private static ISpellChecker Checker
        {
            get
            {
                if (_tried) return _checker;
                _tried = true;
                try
                {
                    var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                    string lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
                    if (string.IsNullOrEmpty(lang) || factory.IsSupported(lang) == 0) lang = "en-US";
                    if (factory.IsSupported(lang) != 0) _checker = factory.CreateSpellChecker(lang);
                }
                catch { _checker = null; }
                return _checker;
            }
        }

        /// <summary>Returns null if the word is spelled correctly (or no checker), otherwise suggestions.</summary>
        public static List<string> Suggest(string word)
        {
            var ck = Checker;
            if (ck == null || string.IsNullOrWhiteSpace(word)) return null;
            try
            {
                int hr = ck.Suggest(word, out IEnumString en);
                if (hr != 0 || en == null) return null; // S_FALSE = correct
                var list = new List<string>();
                var arr = new string[1];
                while (list.Count < 8 && en.Next(1, arr, IntPtr.Zero) == 0) list.Add(arr[0]);
                return list;
            }
            catch { return null; }
        }

        public static void Add(string word) { try { Checker?.Add(word); } catch { } }
        public static void Ignore(string word) { try { Checker?.Ignore(word); } catch { } }
    }
}
