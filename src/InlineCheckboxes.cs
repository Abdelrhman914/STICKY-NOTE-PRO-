using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace StickyNotesPro
{
    /// <summary>
    /// Inline checkboxes inside rich text: several clickable ☐ / ☑ boxes can be on the same line,
    /// e.g. "☐ lec1   ☐ study   ☐ solving".  Typing "( ) " or "[ ] " creates a box automatically.
    /// </summary>
    internal partial class NoteTextBox
    {
        public const char Box = '☐', BoxChecked = '☑';
        public static bool IsBox(char c) => c == Box || c == BoxChecked;

        /// <summary>Raised after a box was checked / unchecked by the user.</summary>
        public event Action CheckboxToggled;

        private string _textCache;
        private string CachedText => _textCache ?? (_textCache = Text ?? "");

        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        private const int WM_SETREDRAW = 0x000B, WM_SETCURSOR = 0x0020, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203;

        /// <summary>done / total inline boxes.</summary>
        public void CountBoxes(out int done, out int total)
        {
            done = total = 0;
            foreach (char c in CachedText)
            {
                if (c == Box) total++;
                else if (c == BoxChecked) { total++; done++; }
            }
        }

        /// <summary>Index of the checkbox character under a client point, or -1.</summary>
        public int BoxAt(Point p)
        {
            try
            {
                string t = CachedText;
                if (t.Length == 0) return -1;
                int ci = GetCharIndexFromPosition(p);
                foreach (int idx in new[] { ci, ci - 1 })
                {
                    if (idx < 0 || idx >= t.Length || !IsBox(t[idx])) continue;
                    var a = GetPositionFromCharIndex(idx);
                    var b = GetPositionFromCharIndex(idx + 1);
                    int w = (b.Y == a.Y && b.X > a.X) ? b.X - a.X : Font.Height;
                    int h = (int)(Font.Height * 1.4);
                    if (p.X >= a.X - 2 && p.X <= a.X + w + 2 && p.Y >= a.Y - 2 && p.Y <= a.Y + h) return idx;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>Handles clicks / cursor for boxes. Returns true when the message was consumed.</summary>
        private bool HandleCheckboxMessage(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK)
            {
                if (ReadOnly || (ModifierKeys & (Keys.Shift | Keys.Control)) != 0) return false;
                long lp = m.LParam.ToInt64();
                var p = new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                int idx = BoxAt(p);
                if (idx < 0) return false;
                if (!Focused) Focus();
                ToggleBox(idx);
                m.Result = IntPtr.Zero;
                return true;
            }
            if (m.Msg == WM_SETCURSOR && !ReadOnly && (m.LParam.ToInt64() & 0xFFFF) == 1 /*HTCLIENT*/)
            {
                var p = PointToClient(Cursor.Position);
                if (BoxAt(p) >= 0) { SetCursor(Cursors.Hand.Handle); m.Result = (IntPtr)1; return true; }
            }
            return false;
        }

        private void Redraw(bool on)
        {
            if (!IsHandleCreated) return;
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
            if (on) Invalidate();
        }

        private void SetSelectionEffect(uint mask, bool on)
        {
            var set = new Native.CHARFORMAT2 { cbSize = Marshal.SizeOf(typeof(Native.CHARFORMAT2)), dwMask = mask, dwEffects = on ? mask : 0, szFaceName = "" };
            Native.SendMessage(Handle, Native.EM_SETCHARFORMAT, (IntPtr)Native.SCF_SELECTION, ref set);
        }

        /// <summary>End of the text that belongs to the box at idx (up to the next box or the end of the line).</summary>
        private int SegmentEnd(string t, int idx)
        {
            int e = idx + 1;
            while (e < t.Length && t[e] != '\n' && t[e] != '\r' && !IsBox(t[e])) e++;
            while (e > idx + 1 && char.IsWhiteSpace(t[e - 1])) e--;
            return e;
        }

        /// <summary>Checks / unchecks the box at idx; checked items are crossed out and greyed.</summary>
        public void ToggleBox(int idx)
        {
            string t = CachedText;
            if (idx < 0 || idx >= t.Length || !IsBox(t[idx])) return;
            bool check = t[idx] == Box;
            int selStart = SelectionStart, selLen = SelectionLength;
            Redraw(false);
            try
            {
                Select(idx, 1);
                SelectedText = check ? BoxChecked.ToString() : Box.ToString();
                FormatSegment(idx, check);
                Select(selStart, selLen);
            }
            finally { Redraw(true); }
            CheckboxToggled?.Invoke();
        }

        private void FormatSegment(int idx, bool done)
        {
            string t = CachedText;
            int s = idx + 1, e = SegmentEnd(t, idx);
            while (s < e && t[s] == ' ') s++;
            if (e <= s) return;
            Select(s, e - s);
            SetSelectionEffect(Native.CFM_STRIKEOUT, done);
            SelectionColor = done ? Color.FromArgb(130, 130, 130) : ForeColor;
        }

        /// <summary>Checks or unchecks every box in the selection (or the whole note when nothing is selected).</summary>
        public void SetAllBoxes(bool check)
        {
            string t = CachedText;
            int from = SelectionLength > 0 ? SelectionStart : 0, to = SelectionLength > 0 ? SelectionStart + SelectionLength : t.Length;
            int selStart = SelectionStart, selLen = SelectionLength;
            Redraw(false);
            try
            {
                for (int i = from; i < to && i < CachedText.Length; i++)
                {
                    char c = CachedText[i];
                    if (!IsBox(c) || (c == BoxChecked) == check) continue;
                    Select(i, 1);
                    SelectedText = check ? BoxChecked.ToString() : Box.ToString();
                    FormatSegment(i, check);
                }
                Select(selStart, selLen);
            }
            finally { Redraw(true); }
            CheckboxToggled?.Invoke();
        }

        /// <summary>Ctrl+Space: ticks / unticks the box in front of the caret (or all boxes in the selection).</summary>
        public bool ToggleBoxNearCaret()
        {
            if (ReadOnly) return false;
            string t = CachedText;
            if (SelectionLength > 0)
            {
                string sel = t.Substring(SelectionStart, Math.Min(SelectionLength, t.Length - SelectionStart));
                if (sel.IndexOf(Box) < 0 && sel.IndexOf(BoxChecked) < 0) return false;
                SetAllBoxes(sel.IndexOf(Box) >= 0);
                return true;
            }
            int c = Math.Min(SelectionStart, t.Length), ls = c;
            while (ls > 0 && t[ls - 1] != '\n') ls--;
            for (int i = c - 1; i >= ls; i--) if (IsBox(t[i])) { ToggleBox(i); return true; }
            for (int i = c; i < t.Length && t[i] != '\n'; i++) if (IsBox(t[i])) { ToggleBox(i); return true; }
            return false;
        }

        /// <summary>Re-applies "done" formatting (crossed out) to every box segment.</summary>
        public void RefreshBoxFormatting()
        {
            string t = CachedText;
            if (t.IndexOf(Box) < 0 && t.IndexOf(BoxChecked) < 0) return;
            int ss = SelectionStart, sl = SelectionLength;
            Redraw(false);
            try
            {
                for (int i = 0; i < t.Length; i++) if (IsBox(t[i])) FormatSegment(i, t[i] == BoxChecked);
                Select(ss, sl);
            }
            finally { Redraw(true); }
        }

        private static readonly Regex MarkerRx = new Regex(@"\(\s?\)|\[\s?\]|\((?:x|X|✓|✔)\)|\[(?:x|X|✓|✔)\]", RegexOptions.Compiled);
        private static readonly Regex SplitRx = new Regex(@"\t+| {2,}|\s*[,;،؛]\s*", RegexOptions.Compiled);

        /// <summary>
        /// Toolbar "checkbox" button: with no selection inserts a box at the caret; with a selection turns it into
        /// checkboxes – "( )" / "[ ]" markers become boxes, otherwise every line and every part separated by
        /// commas, tabs or 2+ spaces gets its own box (all on the same line). Pressing it again removes the boxes.
        /// </summary>
        public void MakeCheckboxes()
        {
            if (ReadOnly) return;
            if (SelectionLength == 0)
            {
                InsertBoxAtCaret();
                return;
            }
            // extend the selection to whole words so a box never lands in the middle of a word
            string all = CachedText;
            int s = SelectionStart, e = Math.Min(all.Length, SelectionStart + SelectionLength);
            while (s > 0 && !char.IsWhiteSpace(all[s - 1]) && !IsBox(all[s - 1])) s--;
            while (e < all.Length && !char.IsWhiteSpace(all[e]) && all[e] != '\n') e++;
            string sel = all.Substring(s, e - s);

            string result;
            bool hasBoxes = sel.IndexOf(Box) >= 0 || sel.IndexOf(BoxChecked) >= 0;
            if (MarkerRx.IsMatch(sel))
            {
                result = MarkerRx.Replace(sel, mm => mm.Value.IndexOfAny(new[] { 'x', 'X', '✓', '✔' }) >= 0 ? BoxChecked.ToString() : Box.ToString());
                result = Regex.Replace(result, "([☐☑])(?=[^ \\n])", "$1 ");
            }
            else if (hasBoxes)
            {
                // already checkboxes → remove them
                result = Regex.Replace(sel, "[☐☑] ?", "");
            }
            else
            {
                var lines = sel.Split('\n');
                var sb = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    var parts = SplitRx.Split(lines[i]).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    if (i > 0) sb.Append('\n');
                    sb.Append(string.Join("   ", parts.Select(x => Box + " " + x)));
                }
                result = sb.ToString();
            }

            Redraw(false);
            try
            {
                Select(s, e - s);
                SelectedText = result;
                // make removed / new boxes look right: strike done items, clear strike on open items
                int end = s + result.Length;
                string t = CachedText;
                if (hasBoxes && !MarkerRx.IsMatch(sel))
                {
                    Select(s, result.Length);
                    SetSelectionEffect(Native.CFM_STRIKEOUT, false);
                    SelectionColor = ForeColor;
                }
                else
                    for (int i = s; i < end && i < t.Length; i++)
                        if (IsBox(t[i])) FormatSegment(i, t[i] == BoxChecked);
                Select(s, result.Length);
            }
            finally { Redraw(true); }
        }

        private void InsertBoxAtCaret()
        {
            string t = CachedText;
            int p = SelectionStart;
            bool needSpaceBefore = p > 0 && !char.IsWhiteSpace(t[p - 1]);
            string ins = (needSpaceBefore ? "   " : "") + Box + " ";
            SelectedText = ins;
            // the new box should never inherit the strikeout of a done item
            int boxIdx = p + ins.Length - 2;
            int caret = SelectionStart;
            Select(boxIdx, 2);
            SetSelectionEffect(Native.CFM_STRIKEOUT, false);
            SelectionColor = ForeColor;
            Select(caret, 0);
            SetSelectionEffect(Native.CFM_STRIKEOUT, false);
            SelectionColor = ForeColor;
        }

        /// <summary>"( ) " / "[ ] " typed → ☐,  "(x) " / "[x] " → ☑.</summary>
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar == ' ' && !ReadOnly && SelectionLength == 0 && SelectionStart >= 2)
            {
                string t = CachedText;
                int p = SelectionStart;
                foreach (int len in new[] { 3, 2 })
                {
                    if (p < len) continue;
                    string prev = t.Substring(p - len, len);
                    bool empty = prev == "( )" || prev == "[ ]" || prev == "()" || prev == "[]";
                    bool done = prev == "(x)" || prev == "[x]" || prev == "(X)" || prev == "[X]";
                    if (!empty && !done) continue;
                    // only at the start of a line or after a space – so "f()" in code is left alone
                    if (p - len > 0 && !char.IsWhiteSpace(t[p - len - 1])) continue;
                    Select(p - len, len);
                    SelectedText = done ? BoxChecked.ToString() : Box.ToString();
                    break;
                }
            }
            base.OnKeyPress(e);
        }
    }
}
