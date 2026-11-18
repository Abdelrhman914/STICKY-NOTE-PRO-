StickyNotes Pro 1.3 - Quick start guide
=======================================

StickyNotes Pro runs in the system tray (the area next to the clock).
Right-click the yellow note icon for all options.

GLOBAL HOTKEYS (work in any program)
  Ctrl+Alt+N   New sticky note
  Ctrl+Alt+C   New checklist note
  Ctrl+Alt+T   New toggle list note
  Ctrl+Alt+W   New note stuck to the window you are using (e.g. Google Chrome)
  Ctrl+Alt+B   Notes browser
  Ctrl+Alt+D   Deadlines & alerts list
  Ctrl+Alt+H   Hide / show all notes

CHECKLISTS WITH DEADLINES
  - Press Enter to add a new item, tick the box when it's done.
  - Click the calendar button next to an item to set its deadline.
  - Choose "Alert me" and when the first alert should come:
    1, 2, 3, 4, 5 days or 1 week before the deadline, and/or at the deadline.
  - The item shows the deadline and how many days are left
    (orange = soon, red = overdue).
  - Reminder popup: Go to note, Mark as done, Reschedule, Snooze, Dismiss.

  - Sections: click "+ Add section" (or Ctrl+Shift+H on an item) to add a
    title that groups the items below it. Click its arrow to collapse/expand;
    it shows how many of its items are done (e.g. 2/5).

CHECKBOXES ON THE SAME LINE (in text notes and toggle lists)
  - Type  ( ) lec1   ( ) study   ( ) solving   - every "( )" becomes a box:
      [box] lec1   [box] study   [box] solving
  - Or select text and click the checkbox button in the toolbar (Ctrl+Shift+C).
    Parts separated by commas, tabs or 2+ spaces each get their own box.
  - Click a box (or Ctrl+Space next to it) to tick it (the item is crossed out). Right-click a note for
    Tick all / Untick all. The toolbar shows how many are done (e.g. 2/3 done).

TOGGLE LISTS (titles that expand and collapse)
  - Tray -> "New toggle list note" (Ctrl+Alt+T), or note menu -> Note type.
  - Each title has its own content (bold, colors, bullets, pictures...).
  - Click the arrow or press Ctrl+Enter to expand / collapse.
  - Enter in a title = go to its content, Ctrl+Shift+Enter = new toggle,
    Alt+Up/Down = move, "Expand all / Collapse all" in the note menu.

NOTES FOR A SPECIFIC WINDOW
  Note menu (the 3-lines button) -> "Stick to window..." (Ctrl+W), pick a window.
  The note appears on top of that window whenever it is active and hides
  when you switch away. Use * as wildcard, e.g. title  *YouTube*

MORE (in the note menu)
  Colors, skins, transparency, font, tags, due date for the whole note,
  always on top, lock (read-only), encrypt with master password,
  linked files & folders, send to clipboard / file / e-mail / printer.

INSIDE A NOTE
  Ctrl+B/I/U/T  bold / italic / underline / strikethrough
  Ctrl+Shift+L  bullets       F5  insert date/time
  Ctrl+F / F3   find          Ctrl+Shift+V  paste as plain text
  Ctrl+R due date   Ctrl+W stick to window   Ctrl+D delete   Alt+Enter properties

YOUR DATA
  Saved automatically in  %APPDATA%\StickyNotesPro\notes.xml  (with a .bak backup).
  Installing a newer version over an old one keeps all your notes.

BACKUP & TRANSFER (move notes to another PC or version)
  Tray -> Backup & transfer -> "Export all notes..."  creates one file
  (*.snpbackup) with all notes, checklists, toggle lists, deadlines and settings.
  On the other computer: install StickyNotes Pro, then double-click the file
  or use "Import from backup..." and choose Merge (add to existing notes)
  or Replace. Encrypted notes stay encrypted (master password needed).
  A safety copy is made before every import, plus one automatic backup per
  day in  %APPDATA%\StickyNotesPro\backups  (last 10 kept).

SECURITY & INTEGRITY (v1.3)
  - The exe, this installer and the uninstaller are digitally signed with the
    release code-signing certificate and timestamped.
  - Before any window opens, the app verifies its own digital signature and
    SHA-256 digest against the pinned release certificate; on ANY change
    (modified byte, appended data, removed or foreign signature) it refuses
    to start and writes a line to  %APPDATA%\StickyNotesPro\security.log
  - This Setup also records the exe's SHA-256 in the registry
    (administrator-only); the installed copy must match it exactly.
  - While running, the app re-verifies itself every 15 seconds and whenever
    its file changes, scans loaded modules for injected code, and ends
    immediately if anything is off. Settings -> Program integrity shows the
    live status, with a "Verify now" button.
  - To check a copy yourself: run Verify-StickyNotesPro.ps1 next to the exe,
    or compare Get-FileHash -Algorithm SHA256 with the SHA-256 in the
    certificate-of-integrity PDF of release 1.3.0.
