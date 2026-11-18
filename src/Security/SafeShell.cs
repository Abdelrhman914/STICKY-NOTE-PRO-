using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace StickyNotesPro.Security
{
    /// <summary>
    /// The only place where the app asks Windows to open something.
    /// Never builds command lines from user data; uses ShellExecute with a validated file / URL only.
    /// </summary>
    internal static class SafeShell
    {
        /// <summary>Hyperlink clicked inside note text. Web / mail links open; anything else is refused or confirmed.</summary>
        public static void OpenHyperlink(IWin32Window owner, string link)
        {
            link = (link ?? "").Trim();
            if (InputValidator.IsSafeWebUrl(link, out var uri))
            {
                Start(uri.AbsoluteUri);
                return;
            }
            // file:// links → treat as a file link, but never run programs from note text
            if (Uri.TryCreate(link, UriKind.Absolute, out var u) && u.IsFile && InputValidator.TryValidatePath(u.LocalPath, out var full, out _))
            {
                if (InputValidator.IsExecutableType(full))
                {
                    MessageBox.Show(owner, "For your safety, links in note text cannot start programs or scripts:\n\n" + full,
                        "Link blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (MessageBox.Show(owner, "This link opens a file on your computer or network:\n\n" + full + "\n\nOpen it?",
                        "Open file link", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    OpenValidated(full);
                return;
            }
            MessageBox.Show(owner, "This link was blocked because it is not a web (http/https) or e-mail address:\n\n" +
                InputValidator.CleanLine(link, 300), "Link blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>A file or folder the user linked to a note.</summary>
        public static void OpenLinkedPath(IWin32Window owner, string path)
        {
            if (!InputValidator.TryValidatePath(path, out var full, out var err))
            {
                MessageBox.Show(owner, "This link is not valid and was not opened.\n\n" + err, "Linked file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full))
            {
                MessageBox.Show(owner, "The linked file or folder no longer exists:\n\n" + full, "Linked file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!isDir && InputValidator.IsExecutableType(full) &&
                MessageBox.Show(owner, "This linked item is a program or script:\n\n" + full +
                    "\n\nRunning programs can harm your computer. Run it only if you trust it.\n\nRun it now?",
                    "Security warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (isDir) OpenFolder(full); else OpenValidated(full);
        }

        /// <summary>Opens a folder in Explorer.</summary>
        public static void OpenFolder(string folder)
        {
            if (!InputValidator.TryValidatePath(folder, out var full, out _) || !Directory.Exists(full)) return;
            Start(full);
        }

        /// <summary>Shows a file selected in Explorer (path is validated and quoted, never interpreted).</summary>
        public static void RevealInExplorer(string file)
        {
            if (!InputValidator.TryValidatePath(file, out var full, out _) || !File.Exists(full)) return;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var psi = new ProcessStartInfo(Path.Combine(win, "explorer.exe"), "/select,\"" + full + "\"") { UseShellExecute = false };
            try { Process.Start(psi)?.Dispose(); } catch { }
        }

        private static void OpenValidated(string full) => Start(full);

        private static void Start(string target)
        {
            try
            {
                var psi = new ProcessStartInfo(target) { UseShellExecute = true, ErrorDialog = false };
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "StickyNotes Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
