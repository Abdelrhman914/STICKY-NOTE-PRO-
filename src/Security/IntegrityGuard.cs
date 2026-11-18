using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StickyNotesPro.Security
{
    /// <summary>
    /// Runtime tamper protection.
    ///
    /// At start-up (before any UI or user data is touched) and then continuously while the app runs:
    ///  • Host check        – the code must run as the entry assembly of its own, on-disk StickyNotesPro.exe
    ///                        (not loaded from memory / reflection into a foreign process).
    ///  • Signature check   – Authenticode signature + SHA-256 digest verified against the PINNED release
    ///                        certificate compiled into the binary (see SigningPin.g.cs).
    ///  • Installed hash    – for installed copies, the SHA-256 of the whole file must equal the value the signed
    ///                        installer stored in HKLM (writable only by administrators).
    ///  • Memory check      – the IL code + metadata mapped in memory must be byte-identical to the verified file.
    ///  • Injection check   – every loaded native module must come from a protected system location or carry
    ///                        a valid Authenticode signature; DLL search paths and extension points are locked down.
    ///  • Debugger check    – a managed or native debugger attached to the process is treated as tampering.
    /// Any failure is logged and the process is terminated immediately.
    /// </summary>
    internal static class IntegrityGuard
    {
        public static bool Enforced => SigningPin.Enforce && SigningPin.CertSha256.Length == 64;
        public static string Status { get; private set; } = "Not checked";
        public static DateTime LastCheck { get; private set; }
        public static string FileSha256 { get; private set; } = "";
        public static string SignerSubject { get; private set; } = "";

        private const int RegularIntervalMs = 15000;
        private const int DebuggerIntervalMs = 1500;

        private static string _exePath;
        private static byte[] _memExpectedHash;
        private static uint _memRva, _memLen;
        private static List<KeyValuePair<uint, uint>> _memMask;   // (rva, length) ranges the loader may patch
        private static readonly Dictionary<string, bool> _moduleCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static Thread _monitor;
        private static FileSystemWatcher _watcher;
        private static readonly object _lock = new object();
        private static int _failing;

        // ------------------------------------------------------------------ start-up

        /// <summary>Called first thing in Main. Returns only when the binary is genuine.</summary>
        public static void Initialize()
        {
            HardenProcess();
            if (!Enforced) { Status = "Development build – integrity enforcement disabled"; return; }

            string reason = RunAllChecks(true);
            if (reason != null) Fail(reason, true);

            _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "IntegrityMonitor", Priority = ThreadPriority.BelowNormal };
            _monitor.Start();
            try
            {
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(_exePath), Path.GetFileName(_exePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.Security,
                    IncludeSubdirectories = false,
                };
                FileSystemEventHandler h = (s, e) => CheckNowAsync();
                _watcher.Changed += h; _watcher.Deleted += h; _watcher.Created += h;
                _watcher.Renamed += (s, e) => CheckNowAsync();
                _watcher.EnableRaisingEvents = true;
            }
            catch { /* watcher is a convenience; the periodic check still runs */ }
        }

        private static void CheckNowAsync() => ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); string r = RunAllChecks(false); if (r != null) Fail(r, false); });

        private static void MonitorLoop()
        {
            int elapsed = 0;
            while (true)
            {
                Thread.Sleep(DebuggerIntervalMs);
                elapsed += DebuggerIntervalMs;
                string r = CheckDebugger();
                if (r == null && elapsed >= RegularIntervalMs) { elapsed = 0; r = RunAllChecks(false); }
                if (r != null) Fail(r, false);
            }
        }

        /// <summary>On-demand verification (Settings → Security). Terminates on failure like the monitor does.</summary>
        public static string VerifyNow()
        {
            if (!Enforced) return null;
            string r = RunAllChecks(false);
            if (r != null) Fail(r, false);
            return r;
        }

        /// <summary>Runs every check. Returns null when everything is fine, otherwise the reason.</summary>
        public static string RunAllChecks(bool startup)
        {
            lock (_lock)
            {
                try
                {
                    string r = CheckHost() ?? CheckDebugger() ?? CheckFile(startup) ?? CheckMemory(startup) ?? CheckModules();
                    LastCheck = DateTime.Now;
                    Status = r == null ? "Verified" : "FAILED: " + r;
                    return r;
                }
                catch (Exception ex)
                {
                    return "Integrity check error: " + ex.GetType().Name;
                }
            }
        }

        // ------------------------------------------------------------------ individual checks

        private static string CheckHost()
        {
            var asm = typeof(IntegrityGuard).Assembly;
            if (Assembly.GetEntryAssembly() != asm) return "Code was loaded into a foreign host process.";
            string loc = asm.Location;
            if (string.IsNullOrEmpty(loc)) return "Code was loaded from memory instead of from the signed executable.";
            var sb = new StringBuilder(32768);
            if (Native.GetModuleFileNameW(IntPtr.Zero, sb, sb.Capacity) == 0) return "Unable to identify the process image.";
            string img = sb.ToString();
            if (img.StartsWith(@"\\?\")) img = img.Substring(4);
            if (!string.Equals(Path.GetFullPath(img), Path.GetFullPath(loc), StringComparison.OrdinalIgnoreCase))
                return "The process image is not the StickyNotes Pro executable.";
            _exePath = Path.GetFullPath(loc);
            return null;
        }

        private static string CheckDebugger()
        {
            if (!Enforced) return null;
            if (Debugger.IsAttached) return "A managed debugger is attached.";
            try
            {
                if (Native.IsDebuggerPresent()) return "A native debugger is attached.";
                bool remote = false;
                if (Native.CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remote) && remote) return "A remote debugger is attached.";
            }
            catch { }
            return null;
        }

        private static byte[] _fileBytes;

        private static string CheckFile(bool startup)
        {
            byte[] f;
            try
            {
                using (var s = new FileStream(_exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (s.Length > 64L * 1024 * 1024) return "Executable has an unexpected size.";
                    f = new byte[s.Length];
                    int off = 0;
                    while (off < f.Length) { int n = s.Read(f, off, f.Length - off); if (n <= 0) break; off += n; }
                    if (off != f.Length) return "Executable could not be read completely.";
                }
            }
            catch (FileNotFoundException) { return "The executable was deleted or replaced while running."; }
            catch (Exception ex) { return "The executable could not be read (" + ex.GetType().Name + ")."; }

            var res = AuthenticodeVerifier.Verify(f, SigningPin.CertSha256);
            if (!res.Ok) return res.Error;
            string fileHash = AuthenticodeVerifier.Hex(res.FileSha256);

            // installed copy → whole-file SHA-256 must match the value recorded by the signed installer
            string expected = InstalledHash(out bool isInstalledCopy);
            if (isInstalledCopy)
            {
                if (string.IsNullOrEmpty(expected)) return "The recorded installation hash is missing.";
                if (!AuthenticodeVerifier.FixedEquals(Encoding.ASCII.GetBytes(expected.ToUpperInvariant()), Encoding.ASCII.GetBytes(fileHash)))
                    return "SHA-256 of the executable does not match the value recorded at installation.";
            }

            if (startup)
            {
                _fileBytes = f;
                FileSha256 = fileHash;
                SignerSubject = res.SignerSubject;
            }
            else if (!string.Equals(fileHash, FileSha256, StringComparison.Ordinal))
                return "The executable on disk changed while the application was running.";
            return null;
        }

        /// <summary>Reads HKLM\Software\StickyNotesPro (32-bit view, as written by the NSIS installer).</summary>
        private static string InstalledHash(out bool isInstalledCopy)
        {
            isInstalledCopy = false;
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"Software\StickyNotesPro"))
                    {
                        string dir = k?.GetValue("InstallDir") as string;
                        if (string.IsNullOrEmpty(dir)) continue;
                        string installedExe = Path.GetFullPath(Path.Combine(dir, "StickyNotesPro.exe"));
                        if (!string.Equals(installedExe, _exePath, StringComparison.OrdinalIgnoreCase)) continue;
                        isInstalledCopy = true;
                        return k.GetValue("ExeSha256") as string ?? "";
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Compares the IL + metadata mapped in memory with the verified file bytes.</summary>
        private static string CheckMemory(bool startup)
        {
            IntPtr baseAddr = Native.GetModuleHandleW(null);
            if (baseAddr == IntPtr.Zero) return "Process image base not found.";
            if (startup)
            {
                var f = _fileBytes;
                if (!PeInfo.TryParse(f, out var pe, out _)) return "Executable headers are invalid.";
                pe.DataDir(f, 14, out uint clrRva, out uint clrSize);
                if (clrRva == 0 || clrSize < 72) return "Executable is not a .NET image.";
                long clrOff = pe.RvaToOffset(clrRva);
                if (clrOff < 0) return "CLR header not found.";
                uint cb = BitConverter.ToUInt32(f, (int)clrOff);
                uint mdRva = BitConverter.ToUInt32(f, (int)clrOff + 8);
                uint mdSize = BitConverter.ToUInt32(f, (int)clrOff + 12);
                uint flags = BitConverter.ToUInt32(f, (int)clrOff + 16);
                if ((flags & 1) == 0) return "Executable is not IL-only.";
                _memRva = clrRva + cb;
                uint end = mdRva + mdSize;
                if (end <= _memRva) return "Unexpected .NET image layout.";
                _memLen = end - _memRva;
                long startOff = pe.RvaToOffset(_memRva), endOff = pe.RvaToOffset(end - 1);
                if (startOff < 0 || endOff < 0 || endOff - startOff + 1 != _memLen) return "Unexpected .NET image layout.";

                // mask: base relocations + import address table (the loader is allowed to write those)
                _memMask = new List<KeyValuePair<uint, uint>>();
                pe.DataDir(f, 12, out uint iatRva, out uint iatSize);
                if (iatSize > 0) _memMask.Add(new KeyValuePair<uint, uint>(iatRva, iatSize));
                pe.DataDir(f, 5, out uint relRva, out uint relSize);
                long relOff = relSize > 0 ? pe.RvaToOffset(relRva) : -1;
                if (relOff >= 0)
                {
                    long p = relOff, relEnd = relOff + relSize;
                    while (p + 8 <= relEnd)
                    {
                        uint page = BitConverter.ToUInt32(f, (int)p), blk = BitConverter.ToUInt32(f, (int)p + 4);
                        if (blk < 8) break;
                        for (long q = p + 8; q + 2 <= p + blk; q += 2)
                        {
                            ushort e = BitConverter.ToUInt16(f, (int)q);
                            if ((e >> 12) != 0) _memMask.Add(new KeyValuePair<uint, uint>(page + (uint)(e & 0xFFF), 8));
                        }
                        p += blk;
                    }
                }
                var expected = new byte[_memLen];
                Buffer.BlockCopy(f, (int)startOff, expected, 0, (int)_memLen);
                ApplyMask(expected);
                using (var sha = SHA256.Create()) _memExpectedHash = sha.ComputeHash(expected);
                _fileBytes = null;
            }
            var live = new byte[_memLen];
            Marshal.Copy(IntPtr.Add(baseAddr, (int)_memRva), live, 0, (int)_memLen);
            ApplyMask(live);
            byte[] h;
            using (var sha = SHA256.Create()) h = sha.ComputeHash(live);
            if (!AuthenticodeVerifier.FixedEquals(h, _memExpectedHash)) return "The program code in memory was modified (code injection / patching detected).";
            return null;
        }

        private static void ApplyMask(byte[] buf)
        {
            foreach (var m in _memMask)
            {
                long s = (long)m.Key - _memRva, e = s + m.Value;
                for (long i = Math.Max(0, s); i < Math.Min(buf.Length, e); i++) buf[i] = 0;
            }
        }

        /// <summary>Every loaded native module must be in a protected system folder or be validly signed.</summary>
        private static string CheckModules()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var trustedRoots = new List<string>();
            foreach (var r in new[] { win, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") })
                if (!string.IsNullOrEmpty(r)) trustedRoots.Add(r.TrimEnd('\\') + "\\");

            using (var proc = Process.GetCurrentProcess())
            {
                foreach (ProcessModule m in proc.Modules)
                {
                    string fn = null;
                    try { fn = m.FileName; } catch { continue; }
                    if (string.IsNullOrEmpty(fn)) continue;
                    // Pseudo-modules without a backing file (in-memory shims reported by some
                    // runtimes, e.g. "[In Memory] <assembly>") cannot be signature-checked and
                    // carry no native code of their own – nothing can be injected through them.
                    if (fn.IndexOf("In Memory", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!File.Exists(fn)) return "Unverifiable module (file missing): " + fn;
                    string path;
                    try { path = Path.GetFullPath(fn); } catch { continue; }
                    if (string.Equals(path, _exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_moduleCache.TryGetValue(path, out bool ok)) { if (!ok) return "Unauthorized module injected: " + path; continue; }

                    bool trusted = false;
                    foreach (var root in trustedRoots)
                        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { trusted = true; break; }
                    if (!trusted) trusted = IsIlOnlyAssembly(path) || WinTrust.IsSigned(path);
                    _moduleCache[path] = trusted;
                    if (!trusted) return "Unauthorized (unsigned) module injected: " + path;
                }
            }
            return null;
        }

        /// <summary>Pure-IL managed assemblies (e.g. XmlSerializer helpers) cannot carry native injection code.</summary>
        private static bool IsIlOnlyAssembly(string path)
        {
            try
            {
                var f = File.ReadAllBytes(path);
                if (!PeInfo.TryParse(f, out var pe, out _)) return false;
                pe.DataDir(f, 14, out uint clrRva, out uint clrSize);
                long o = clrRva == 0 ? -1 : pe.RvaToOffset(clrRva);
                return o >= 0 && (BitConverter.ToUInt32(f, (int)o + 16) & 1) != 0;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ hardening

        private static void HardenProcess()
        {
            try { Native.HeapSetInformation(IntPtr.Zero, 1 /*HeapEnableTerminationOnCorruption*/, IntPtr.Zero, 0); } catch { }
            // Wine/CrossOver export these APIs but apply DIFFERENT loader semantics afterwards:
            // SetDefaultDllDirectories there breaks every later P/Invoke (even user32). The API
            // check alone is not enough - skip the DLL-search hardening when running under Wine.
            if (!IsWineRuntime())
            {
                try { Native.SetDllDirectoryW(""); } catch { }                               // no DLLs from the current directory
                try { Native.SetDefaultDllDirectories(0x00000800 /*LOAD_LIBRARY_SEARCH_SYSTEM32*/); } catch { }
            }
            try
            {
                // block AppInit_DLLs, legacy IMEs, global window-hook DLL injection, Winsock LSPs
                var ext = new int[] { 1 };
                Native.SetProcessMitigationPolicy(6 /*ProcessExtensionPointDisablePolicy*/, ext, 4);
                // no images from remote shares or low-integrity locations; prefer System32 copies
                var img = new int[] { 1 | 2 | 4 };
                Native.SetProcessMitigationPolicy(10 /*ProcessImageLoadPolicy*/, img, 4);
            }
            catch { }
        }

        /// <summary>Detects Wine/CrossOver, whose loader semantics differ from Windows for DLL search hardening.</summary>
        private static bool IsWineRuntime()
        {
            try
            {
                // Wine 8+ exposes wine_get_version through ntdll; older Wine also through kernel32.
                foreach (var m in new[] { "ntdll", "kernel32" })
                {
                    IntPtr k = Native.LoadLibraryW(m);
                    if (k != IntPtr.Zero && Native.GetProcAddress(k, "wine_get_version") != IntPtr.Zero) return true;
                }
                using (var r = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Wine"))
                    if (r != null) return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ failure

        public static string SecurityLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickyNotesPro", "security.log");

        private static void Fail(string reason, bool startup)
        {
            if (Interlocked.Exchange(ref _failing, 1) == 1) { Thread.Sleep(Timeout.Infinite); return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SecurityLog));
                File.AppendAllText(SecurityLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INTEGRITY VIOLATION ({(startup ? "start-up" : "runtime")}): {reason}  file={_exePath}\r\n");
            }
            catch { }
            if (startup)
            {
                try
                {
                    MessageBox.Show(
                        "StickyNotes Pro failed its integrity check and will not start.\n\n" + reason +
                        "\n\nThe program file may have been modified, damaged or tampered with. " +
                        "Please reinstall StickyNotes Pro from the official, digitally signed setup.\n\n" +
                        "Your notes have not been touched.",
                        "StickyNotes Pro – Integrity check failed", MessageBoxButtons.OK, MessageBoxIcon.Stop,
                        MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                }
                catch { }
            }
            Native.TerminateProcess(Native.GetCurrentProcess(), 0xC0000409);
            Environment.FailFast("Integrity violation: " + reason);
        }

        // ------------------------------------------------------------------ P/Invoke

        private static class Native
        {
            [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern uint GetModuleFileNameW(IntPtr h, StringBuilder sb, int n);
            [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
            [DllImport("kernel32")] public static extern bool IsDebuggerPresent();
            [DllImport("kernel32")] public static extern bool CheckRemoteDebuggerPresent(IntPtr h, ref bool present);
            [DllImport("kernel32")] public static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32")] public static extern bool TerminateProcess(IntPtr h, uint code);
            [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool SetDllDirectoryW(string path);
            [DllImport("kernel32")] public static extern bool SetDefaultDllDirectories(uint flags);
            [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibraryW(string name);
            [DllImport("kernel32", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr mod, string name);
            [DllImport("kernel32")] public static extern bool HeapSetInformation(IntPtr heap, int cls, IntPtr info, UIntPtr len);
            public static bool HeapSetInformation(IntPtr heap, int cls, IntPtr info, int len) => HeapSetInformation(heap, cls, info, (UIntPtr)len);
            [DllImport("kernel32")] public static extern bool SetProcessMitigationPolicy(int policy, int[] buffer, UIntPtr len);
            public static bool SetProcessMitigationPolicy(int policy, int[] buffer, int len) => SetProcessMitigationPolicy(policy, buffer, (UIntPtr)len);
        }
    }

    /// <summary>WinVerifyTrust wrapper (used for third-party modules loaded into the process).</summary>
    internal static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice;
            public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction;
            public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

        public static bool IsSigned(string path)
        {
            IntPtr pFile = IntPtr.Zero;
            try
            {
                var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
                pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
                Marshal.StructureToPtr(fi, pFile, false);
                var d = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,              // WTD_UI_NONE
                    fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                    dwUnionChoice = 1,           // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x00001000,    // WTD_CACHE_ONLY_URL_RETRIEVAL (never block on the network)
                };
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref d) == 0;
            }
            catch { return false; }
            finally { if (pFile != IntPtr.Zero) { Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO)); Marshal.FreeHGlobal(pFile); } }
        }
    }
}
