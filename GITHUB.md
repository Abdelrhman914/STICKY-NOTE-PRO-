# Publishing StickyNotes Pro on GitHub

This file is a checklist for the repository owner. It is safe (and useful)
to commit it, but you can delete it after the first publish if you prefer.

## 1. What goes INTO the repository

| Commit (tracked in git)                                   | Why |
|-----------------------------------------------------------|-----|
| `README.md`, `LICENSE`, `.gitignore`, `GITHUB.md`         | Front page + license + rules |
| `src/` — all `*.cs`, `StickyNotesPro.csproj`, `app.manifest`, `app.ico` | The source code |
| `installer/` — `setup.nsi`, `LICENSE.rtf` (EULA), `README.txt`, `*.ico`, `*.bmp` | Installer source |
| `docs/screenshots/` — app screenshots used by the README | Docs |
| `build/` — `release.sh`, `sign-file.sh`, `integrity_record.py`, `Verify-StickyNotesPro.ps1`, `tools/IntegrityVerifier/**` | Signed-release pipeline |

> `src/Security/SigningPin.g.cs` is safe to publish: it only contains the
> SHA-256 of the (public) release certificate — not any private key.

## 2. What must NEVER go into the repository

| Do NOT commit                                             | Why |
|-----------------------------------------------------------|-----|
| `signing/` (PFX private key, `pfx-password.txt`, `.pem`, `.cnf`) | **If the private key leaks, anyone can sign files the app accepts as genuine.** The `.gitignore` already blocks it — verify before the first commit. |
| `*.exe`, `*.dll`, `StickyNotesPro.exe.config`, `release/`, `*.cer`, PDF certificate, manifest JSON, `SHA256SUMS.txt` | Binaries and release artifacts belong in a **GitHub Release**, not in git history (they bloat clones forever). |
| `uploads/` (the Notezilla screenshots used as references) | Copyrighted material owned by a third party — must not be in your repo. |
| `bin/`, `obj/`, `out/`, `.vs/`, `.cache/`                | Build output. |

## 3. First publish (commands)

```bash
cd StickyNotesPro
git init -b main
git add .                              # .gitignore does the filtering
git status                             # CHECK: no signing/, no *.exe, no release/
git commit -m "StickyNotes Pro 1.3.0"
git remote add origin https://github.com/<you>/StickyNotesPro.git
git push -u origin main
```

**Safety check before `git push`:**
```bash
git status --porcelain | grep -iE "signing|\.exe|release/" && echo "STOP - ignored files staged"
```
(no output = safe)

## 4. Attaching release 1.3.0

Create a GitHub Release (tag `v1.3.0`) and upload these files from
`release/1.3.0/`:

- `StickyNotesPro_Setup_1.3.0.exe` — the installer
- `StickyNotesPro.exe` + `StickyNotesPro.exe.config` — portable exe
- `StickyNotesPro_1.3.0_Certificate_of_Integrity.pdf` — signed release record
- `StickyNotesPro-Release-Signing.cer` — public release certificate
- `Verify-StickyNotesPro.ps1` — one-command authenticity check
- `SHA256SUMS.txt` and `StickyNotesPro_1.3.0_release-manifest.json`

Notice text for every release (paste into the release description):

> Download `StickyNotesPro_...pdf` — its exe SHA-256 is authoritative.
> Any file whose SHA-256 differs is NOT an authentic release; do not run it.

## 5. When releasing a new version

1. Bump `<Version>` in `src/StickyNotesPro.csproj` (+ `Program.cs` text if shown).
2. Run `bash build/release.sh` (it re-pins the certificate and re-signs everything).
3. Commit only the source/scripts/docs changes (never artifacts).
4. Tag: `git tag vX.Y.Z -m "..."; git push --tags`
5. Attach everything from `release/X.Y.Z/` to a new GitHub Release.
