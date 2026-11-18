using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StickyNotesPro
{
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_PAINT = 0x000F;
        public const int WM_USER = 0x0400;
        public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_EXSTYLE = -20;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_RESTORE = 9;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        // RichEdit
        public const int EM_SETMARGINS = 0x00D3;
        public const int EC_LEFTMARGIN = 0x1, EC_RIGHTMARGIN = 0x2;
        public const int EM_GETCHARFORMAT = WM_USER + 58;
        public const int EM_SETCHARFORMAT = WM_USER + 68;
        public const int EM_SETLANGOPTIONS = WM_USER + 120;
        public const int EM_GETLANGOPTIONS = WM_USER + 121;
        public const int EM_SETEDITSTYLE = WM_USER + 204;
        public const int EM_GETEDITSTYLE = WM_USER + 205;
        public const int IMF_SPELLCHECKING = 0x0800;
        public const int SES_USECTF = 0x00010000, SES_CTFALLOWEMBED = 0x00200000,
            SES_CTFALLOWSMARTTAG = 0x00400000, SES_CTFALLOWPROOFING = 0x00800000;
        public const int SCF_SELECTION = 0x0001;
        public const uint CFM_BOLD = 0x1, CFM_ITALIC = 0x2, CFM_UNDERLINE = 0x4, CFM_STRIKEOUT = 0x8;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CHARFORMAT2
        {
            public int cbSize;
            public uint dwMask;
            public uint dwEffects;
            public int yHeight;
            public int yOffset;
            public int crTextColor;
            public byte bCharSet;
            public byte bPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szFaceName;
            public short wWeight;
            public short sSpacing;
            public int crBackColor;
            public int lcid;
            public int dwReserved;
            public short sStyle;
            public short wKerning;
            public byte bUnderlineType;
            public byte bAnimation;
            public byte bRevAuthor;
            public byte bReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CHARFORMAT2 lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string name);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        public static string GetTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 2);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static readonly Dictionary<uint, string> _procCache = new Dictionary<uint, string>();

        public static string GetProcessName(IntPtr hWnd, out uint pid)
        {
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == 0) return "";
            if (_procCache.TryGetValue(pid, out var name)) return name;
            try { name = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
            catch { name = ""; }
            if (_procCache.Count > 500) _procCache.Clear();
            _procCache[pid] = name;
            return name;
        }

        /// <summary>Visible bounds of a window (without invisible resize borders on Win10+).</summary>
        public static RECT GetVisibleRect(IntPtr hWnd)
        {
            try
            {
                if (DwmGetWindowAttribute(hWnd, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out RECT r, Marshal.SizeOf(typeof(RECT))) == 0 && r.Width > 0)
                    return r;
            }
            catch { }
            GetWindowRect(hWnd, out RECT rr);
            return rr;
        }

        public static bool IsCloaked(IntPtr hWnd)
        {
            try { return DwmGetWindowAttribute(hWnd, 14 /*DWMWA_CLOAKED*/, out int c, 4) == 0 && c != 0; }
            catch { return false; }
        }

        public class WindowInfo
        {
            public IntPtr Handle; public string Title; public string Process;
            public override string ToString() => Title;
        }

        /// <summary>Lists normal, visible top level application windows.</summary>
        public static List<WindowInfo> GetOpenWindows()
        {
            var list = new List<WindowInfo>();
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || IsCloaked(h)) return true;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return true;
                string t = GetTitle(h);
                if (string.IsNullOrWhiteSpace(t)) return true;
                string p = GetProcessName(h, out uint pid);
                if (pid == myPid) return true;
                if (t == "Program Manager") return true;
                list.Add(new WindowInfo { Handle = h, Title = t, Process = p });
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
