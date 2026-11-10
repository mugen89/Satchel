# GitHub and update instructions

How to put Satchel on GitHub, publish a new version, and let the app update itself from it.
Written for a first time: every step says where to click or what to type.

> [!CAUTION]
> **Your recovery key must never be uploaded.** The file `Satchel recovery key.txt` in the project folder unlocks your
> vault. The project's `.gitignore` keeps it (and your `Data` folder) out of git, but the safest place for it is not
> this folder at all: print it or put it in a password manager, then delete the file. Step 5 below double-checks that
> it isn't about to be uploaded.

---

## How it fits together

```
your PC                                   GitHub (public)                       any copy of Satchel
──────────────                            ─────────────────                     ────────────────────
project folder ── git push ───────────▶   the code
release.ps1 ── upload on the website ──▶  Releases: v1.3.1                      Settings → Updates
                                            Satchel-1.3.1-win-x64.zip     ◀──── "Check for updates"
                                            Satchel-1.3.1-win-x64.zip.sha256 ── download, verify, install
                                            notes ("What's new")          ────▶ shown in the app
```

* **The code** goes to GitHub with `git` (steps 4–5). Nothing in the app needs it there, but it's your backup and
  history.
* **A release** is one version: a tag such as `v1.3.1`, a description (the "What's new" text) and the files the
  app downloads. `release.ps1` makes those files (step 7).
* **The app** asks GitHub for the list of releases. If one is newer than itself, Settings → Updates offers to
  install it.

---

## One-time setup

### 1. Create a GitHub account

1. Go to <https://github.com/signup> and sign up. Your **username** becomes part of every address, e.g.
   `https://github.com/yourname/Satchel`.
2. Recommended: turn on two-factor authentication (Settings → Password and authentication). The updater trusts what
   is published in your releases, so your account is worth protecting.
3. Keep your email private: Settings → Emails → tick **Keep my email addresses private**. GitHub then shows you an
   address like `12345678+yourname@users.noreply.github.com`. Copy it for the next step.

### 2. Tell git who you are

Git is already installed on this PC (version 2.54). Open **PowerShell** and run, with your own name and the
`noreply` address from step 1.3:

```powershell
git config --global user.name "Your Name"
git config --global user.email "12345678+yourname@users.noreply.github.com"
```

Everything you upload carries this name and email publicly, so the `noreply` address keeps your real one private.

### 3. Create the repository on GitHub

1. On GitHub, click **+** (top right) → **New repository**.
2. **Repository name:** `Satchel`.
3. **Public.** The app checks for updates without signing in to GitHub, and that only works for public
   repositories. Your code becomes visible to anyone; your notes, vault and other data never leave your drive.
   *(If you'd rather keep the code private, see "Private code" at the end.)*
4. Leave **Add a README**, **.gitignore** and **license** off — the project already has them.
5. Click **Create repository**. Keep the page open: it shows the address you need next,
   `https://github.com/yourname/Satchel.git`.

### 4. Tell Satchel where its updates come from

Open `src\Satchel\Satchel.csproj` in a text editor and fill in your repository:

```xml
<SatchelUpdateRepository>yourname/Satchel</SatchelUpdateRepository>
```

Every build made from now on looks there by default. (You can also type it in the app, in Settings → Updates →
GitHub repository; that overrides the built-in one.)

### 5. Upload the code (the first "push")

In PowerShell:

```powershell
cd "D:\01_Organized\Personal Organizer"
git init
git add .
git status
```

`git status` lists everything that's about to be uploaded. **Check it.** You should see `src/`, `README.md`,
`CHANGELOG.md`, `publish.ps1`, `release.ps1` and the other documents. You must **not** see:

* `Satchel recovery key.txt`
* any `Data` folder, or a `.db` file
* `bin`, `obj` or `release` folders

To be sure about the key, this must print nothing:

```powershell
git status --short | Select-String -Pattern "recovery"
```

If something is listed that shouldn't be, stop and ask me before going further. Otherwise:

```powershell
git commit -m "Satchel 1.3.0"
git branch -M main
git remote add origin https://github.com/yourname/Satchel.git
git push -u origin main
```

The first push opens a browser window to sign in to GitHub. That's the Git Credential Manager, which comes with
Git, and it remembers you afterwards. Reload the repository page: your code is there.

> `Session log.md` and `Possible feature addition.md` are uploaded too. They're notes about building Satchel
> (no personal data). If you'd rather keep them on your PC only, add their names to `.gitignore` before
> `git add .`.

### 6. Allow PowerShell scripts (once)

`publish.ps1` and `release.ps1` are PowerShell scripts, and Windows blocks scripts by default. Allow your own
scripts for your user account:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

Answer **Y**. To run a script without changing this, use
`powershell -ExecutionPolicy Bypass -File .\release.ps1` instead.

---

## Publishing a version

Do this for every new version, starting with 1.3.0 right after the setup.

### 7. Build the release files

1. **Raise the version** in `src\Satchel\Satchel.csproj`, e.g. `<Version>1.3.1</Version>`. The version is how the
   app knows a release is newer than itself, so never publish two releases with the same number.
2. **Write what changed** at the top of `CHANGELOG.md`, under a heading with the same number:

   ```markdown
   ## 1.3.1 — 2026-11-12

   ### Improved

   - Something that got better.

   ### Fixed

   - Something that was broken.
   ```

   This text is what people see as "What's new" in Settings → Updates.
3. **Build and package:**

   ```powershell
   cd "D:\01_Organized\Personal Organizer"
   .\release.ps1
   ```

   It takes a minute or two and ends by printing exactly what to upload. In `release\` you now have:

   | File | What it is |
   |---|---|
   | `Satchel-1.3.1-win-x64.zip` | the app (never your `Data` folder) |
   | `Satchel-1.3.1-win-x64.zip.sha256` | its checksum; the app refuses a download that doesn't match it |
   | `release-notes-1.3.1.md` | the `CHANGELOG.md` section for this version |

   `release\Satchel\` is also the ready-to-run app if you want to copy it to a drive by hand.

### 8. Upload the code changes

```powershell
git add .
git commit -m "Satchel 1.3.1"
git push
```

### 9. Create the release on GitHub

1. Open your repository on GitHub → **Releases** (right-hand side) → **Draft a new release**
   (the first time: **Create a new release**).
2. **Choose a tag** → type `v1.3.1` → click **Create new tag: v1.3.1 on publish**.
3. **Release title:** `Satchel 1.3.1`.
4. **Description:** open `release\release-notes-1.3.1.md`, copy everything and paste it here.
5. Drag **both** `Satchel-1.3.1-win-x64.zip` and `Satchel-1.3.1-win-x64.zip.sha256` into the
   **Attach binaries** box. Wait until both have finished uploading.
6. Leave **Set as a pre-release** unticked (the app ignores pre-releases), then click **Publish release**.

That's it: every copy of Satchel that checks for updates now finds 1.3.1.

> Optional shortcut: with the GitHub CLI (`winget install GitHub.cli`, then `gh auth login` once),
> `.\release.ps1 -Publish` does step 9 for you.

---

## Getting the updater onto your drive (once)

The copy of Satchel on your flash drive is older than 1.3.0, so it has no updater yet. Install 1.3.0 by hand once:

1. Close Satchel on the drive.
2. Copy everything from `release\Satchel\` (after step 7) onto the drive's `Satchel` folder, replacing the old
   files. **Keep the drive's `Data` folder**: your data is in it, and the new version upgrades it on its first
   start, after making a backup.
3. Start it, unlock, open **Settings → Updates** and click **Check for updates**. It should say you're up to date.

From then on, updates install from inside the app.

## Testing the first update

The plan we agreed: publish 1.3.0, then a small 1.3.1, and update to it from inside the app.

1. Publish **1.3.0** (steps 7–9, with the version already at 1.3.0), and install it on the drive as above.
   *To be careful, test on a copy first: copy the whole `Satchel` folder, including `Data`, to your desktop and run
   that one.*
2. Tell me when that's done. I'll make a small, visible change, raise the version to **1.3.1** and write its
   CHANGELOG entry.
3. You run steps 7–9 for 1.3.1.
4. In the 1.3.0 copy: **Settings → Updates → Check for updates** → **Install 1.3.1** → confirm.
   Satchel downloads the update, checks it, backs up your data, closes and starts again.
5. Unlock. A message says *Updated to Satchel 1.3.1*. Under Updates, 1.3.1 is marked **Installed**, with its notes.

---

## How the updater works

* **Checking:** it reads `https://api.github.com/repos/<you>/Satchel/releases`. Drafts and pre-releases are
  skipped, and so is a release without a `.zip` plus its checksum. Only with **Online features** on
  (Settings → Sidebar & online). Automatic checks happen at most once a day, after you unlock; they only tell you,
  and never install anything on their own. GitHub allows about 60 checks an hour from one network without signing
  in, which is plenty.
* **Downloading:** into `Data\Updates`. The SHA-256 of the download has to match the `.sha256` file (or the
  checksum GitHub reports itself), otherwise it's deleted and nothing changes.
* **Installing:**
  1. Your database is backed up to `Data\Backups` (`…-before-update.db`).
  2. A running program can't be overwritten on Windows, but it can be renamed. Each file that changed is renamed to
     `*.old` and the new one is copied in its place. Files that didn't change are left alone.
  3. Satchel starts the new version and closes. The new version waits for the old one to finish, then deletes the
     `.old` files and the download.
  4. If any file can't be replaced, everything is put back as it was and you get a message.

  Your `Data` folder is never touched.
* **Development builds** (run from Visual Studio or `dotnet run`) can check, but won't install. Only the published
  app (`publish.ps1` / `release.ps1`) updates itself.
* **What the checksum does and doesn't do:** it guarantees the download arrived intact and is exactly the file in
  your release. It can't protect against someone who gets into your GitHub account and publishes a release
  themselves. That's why two-factor authentication matters (step 1.2).

## When something goes wrong

| Message or problem | What to do |
|---|---|
| "GitHub has no public repository …" | Check the name in Settings → Updates (`yourname/Satchel`), and that the repository is public. |
| "No versions have been published in this repository yet" | The release is still a draft, or wasn't published. |
| Says "up to date" although you published | The `<Version>` wasn't raised before `release.ps1`, the tag is lower than the installed version, the release is marked pre-release, or the `.zip` / `.sha256` isn't attached. |
| "This release has no download with a checksum" | Attach the `.sha256` file to the release (edit the release on GitHub). |
| "The download doesn't match its checksum" | Upload the zip again, together with the `.sha256` made with it in the same `release.ps1` run. |
| "A file couldn't be replaced … Nothing was changed" | Another program has a file open, often an antivirus scan or an Explorer window showing the folder. Try again in a minute. |
| "Satchel can't write to its own folder" | The drive is write-protected, or Satchel is in Program Files. |
| "Online features are off" | Click *Turn on online features* in Settings → Updates. |
| `release.ps1` says the exe and project versions differ | Run it without `-SkipBuild`, so it builds again. |
| Windows SmartScreen warns about `Satchel.exe` after downloading the zip in a browser | Right-click the zip → Properties → **Unblock** before unzipping, or choose *More info → Run anyway*. Updates installed by the app aren't affected. |
| `git push` asks for a password | Use the browser sign-in window; GitHub doesn't accept your account password in git. |
| Scripts "cannot be loaded because running scripts is disabled" | Step 6. |

**If the recovery key was ever uploaded:** treat it as public. Removing the file in a later commit doesn't remove it
from GitHub's history.
1. In Satchel, make a new recovery key and change the master password (Settings → Security).
2. Make the repository private, or delete it and start over.
3. Ask me to help remove the file from the history.

## Quick reference: releasing a version

```powershell
# 1. raise <Version> in src\Satchel\Satchel.csproj, add "## x.y.z — date" to CHANGELOG.md
cd "D:\01_Organized\Personal Organizer"
.\release.ps1
git add .
git commit -m "Satchel x.y.z"
git push
# 2. GitHub → Releases → Draft a new release → tag vx.y.z → paste release\release-notes-x.y.z.md
#    → attach the .zip and .zip.sha256 → Publish release
```

## Private code (optional)

The updater needs **public releases**, not public code. To keep the code private, use two repositories:

* `Satchel` (private): your code. Steps 3–5 and 8, with **Private** chosen in step 3.
* `Satchel-releases` (public, empty): only releases. Create it like step 3 (tick *Add a README* so it isn't
  empty), do step 9 there, and put `yourname/Satchel-releases` in `<SatchelUpdateRepository>` (step 4).
