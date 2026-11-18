using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using StickyNotesPro.Security;

namespace StickyNotesPro
{
    internal static class Program
    {
        private const string MutexName = "StickyNotesPro_SingleInstance_v1";
        private const string EventName = "StickyNotesPro_ShowBrowser_v1";
        public const string Version = "1.3.0";

        private const string ImportEventName = "StickyNotesPro_Import_v1";
        private static string PendingFile => Path.Combine(Storage.Folder, "pending-import.txt");

        [STAThread]
        private static void Main(string[] args)
        {
            // Tamper protection: verify signature, hash and memory image before anything else runs.
            IntegrityGuard.Initialize();

            // a backup file passed on the command line (double-click on *.snpbackup)
            string importPath = null;
            foreach (var a in args ?? new string[0])
                if (TryGetBackupPath(a, out var p)) importPath = p;

            using (var mutex = new Mutex(true, MutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    if (importPath != null)
                    {
                        // already running → hand the file to the running instance
                        try
                        {
                            Directory.CreateDirectory(Storage.Folder);
                            File.WriteAllText(PendingFile, importPath);
                            EventWaitHandle.OpenExisting(ImportEventName).Set();
                        }
                        catch { }
                        return;
                    }
                    // already running → ask the running instance to open the Notes browser
                    try { EventWaitHandle.OpenExisting(EventName).Set(); } catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) UI.Scale = Math.Max(1f, g.DpiX / 96f); } catch { }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => { Log(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);

                var ctx = new AppController();

                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                var evImport = new EventWaitHandle(false, EventResetMode.AutoReset, ImportEventName);
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        int which = WaitHandle.WaitAny(new WaitHandle[] { ev, evImport });
                        if (which == 0) ctx.InvokeOnUi(() => ctx.ShowBrowser(false));
                        else
                        {
                            string path = null;
                            try
                            {
                                var fi = new FileInfo(PendingFile);
                                if (fi.Exists && fi.Length < 8192) path = File.ReadAllText(PendingFile).Trim();
                                File.Delete(PendingFile);
                            }
                            catch { }
                            if (!TryGetBackupPath(path, out path)) path = null;
                            if (!string.IsNullOrEmpty(path)) ctx.InvokeOnUi(() => ctx.ImportBackup(null, path));
                        }
                    }
                }) { IsBackground = true };
                t.Start();
                if (importPath != null) ctx.InvokeOnUi(() => ctx.ImportBackup(null, importPath));

                Application.Run(ctx);
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>Command-line / hand-over input: must be an existing local *.snpbackup file of sane size.</summary>
        private static bool TryGetBackupPath(string a, out string full)
        {
            full = null;
            if (!InputValidator.TryValidatePath(a, out var p, out _)) return false;
            if (!p.EndsWith(".snpbackup", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists || fi.Length > InputValidator.MaxDataFileBytes) return false;
            }
            catch { return false; }
            full = p;
            return true;
        }

        public static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Storage.Folder);
                File.AppendAllText(Path.Combine(Storage.Folder, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
            }
            catch { }
        }
    }
}
