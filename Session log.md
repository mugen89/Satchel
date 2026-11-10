# Satchel — session log

A record of the build sessions: what was asked, what was built, how it was checked and what is still open.
Newest first. Ideas for later are in [Possible feature addition.md](Possible%20feature%20addition.md).
What changed in each version, in short, is in [CHANGELOG.md](CHANGELOG.md).

---

## 2026-11-10 — version 1.3.0

### Asked

After testing 1.2, in order:

> "moving elements on the Dashboard is buggy … on the dash board we want profiles … the element on the dashboard
> should be resizable / the button Private Hidden should be inside settings / in settings i need a list of all the keybinds"

> "the board is my favourite feature so far so i would like to upgrade it … an pull a note from the notes and convert
> it as a board note … the tabs are expanding a bit so make the grouping colapsable … make a Gamble tab (for now just a
> place holder)" — and: "notes on the board can be become private individually or the entire board can be private"

> "a new tab in settings wit option to map my main tabs keybinds. also lets add a configurable keybind to toggle
> visibility of the private items"

> "Library should have a drag and trop feature. it should read .md and .txt. a cool extra can be a .doc and .docx
> convert to .md and .xls and .xlsx also to convert. we might need a new tab for spreadsheet work"

> "Library need some optimization. also when a bigger file is active on Library when clicking the tab it requires a
> long time to load. the library pane on the laft should have option to create folders … i want to upload the project
> on github and then to push an update. in settings we will need a tab with version, a button check for updates, update
> button and a section with feeds where we should post the changes … I have never done this procedure so i will need a
> github and update instructions.md … once we set it up we will make a minot update and push it so i can test the update"

Also answered: how to mark items private, and what can be private.

### Result

Everything asked was built. Satchel is **version 1.3.0** with **27 tabs**. It can update itself from GitHub
releases, and **[GitHub and update instructions.md](GitHub%20and%20update%20instructions.md)** walks through the
setup. The test update to 1.3.1 waits for that setup (*Open items*).

### Built

| Area | What changed |
|---|---|
| Dashboard | Customize mode no longer shakes. Widgets land where you drop them and resize from a corner. **Profiles**, each with an optional icon shortcut in the top bar (`Alt+1 … 9`). |
| Settings | *Show private items* moved here (Privacy). **Keyboard shortcuts** lists every shortcut with a search box. **Key bindings**: your own keys for every tab and for private items. **Updates** is new (below). |
| Board | Notes edit like the Notes tab (checklists, lists, headings, a large editor). **Insert from Satchel** brings in notes, photos, videos, games, books, shows, bookmarks, projects and task lists. Private notes take their connections with them, and whole boards can be private. |
| Sidebar | Sections fold away. A **Gamble** tab placeholder. |
| Library import | Drop files anywhere on the page. `.md` / `.txt` in any encoding; `.docx`, `.doc` and `.rtf` become Markdown with pictures; `.xlsx`, `.xls` and `.csv` become tables. |
| Sheets (new tab) | Formulas with 100 functions, fill, sort, insert / delete, undo, several sheets, a frozen row; `.xlsx` / `.xls` / `.csv` in, `.xlsx` / `.csv` out, *Copy to the Library*. |
| Library speed | A 645 KB book: opening it **~38 s → 1–2 s**, coming back to the tab **~37 s → instant**. Search: up to 1.6 s → under 0.1 s. The live preview waits longer in big documents. |
| Library folders | Nested folders in the left pane; drag documents and folders onto them, or ⋯ → *Move to folder*. Private folders, *Undo* on delete, and old collections became folders. |
| Updates | Settings → Updates: version, *Check for updates*, *Install*, the repository, a daily check, and *What's new* (each release's notes). `release.ps1` packages a release; `CHANGELOG.md` holds the notes. |

### How it works — notes for the next session

- **Pages that keep their view:** `Controls/AnimatedPanels.cs` → `PageHost` replaced `TransitionContentControl`.
  - A page view model marked `IKeepAlivePage` (Library, Sheets) keeps its view, which is only hidden while you're elsewhere.
  - When the view model is disposed (showing private items recreates the pages), its view goes too.
  - A hidden page gives up the keyboard focus.
- **Why the Library was slow:** the hidden editor `TextBox` held the whole book, with spell checking on. Each visit
  re-laid out that text for ~35 s. Now the editor gets the text only when you start editing, and spell checking is off
  above 150,000 characters.
- **Folders:**
  - `LibraryFolder` (Name, ParentId) and `LibraryDoc.FolderId`, added by `SchemaUpgrader`.
  - The query filter hides documents whose folder is hidden. `PrivacyService` marks subfolders along with their folder.
  - `LibraryViewModel.MoveCollectionsIntoFolders` turns the old `Collection` text into folders when the Library loads,
    also for Sheets' *Copy to the Library* (folder "Spreadsheets") and the Markdown guide ("Guides").
  - Folded folders are remembered in `AppSettings.LibraryCollapsedFolders`.
- **The updater (`Services/UpdateService.cs`, `ViewModels/UpdatesViewModel.cs`):**
  1. It reads `api.github.com/repos/<repo>/releases` with the User-Agent `Satchel/<version>`. Drafts, pre-releases
     and releases without a `.zip` and its checksum are skipped.
  2. It downloads into `Data\Updates` and checks the SHA-256 against the `.sha256` file or GitHub's own `digest`.
  3. It unpacks, makes a backup (`before-update`) and renames each changed file to `*.old` before copying in the new
     one; unchanged files are skipped.
  4. It starts the new exe with `--wait-for <pid>` and closes.
  5. The new version waits, then deletes **only** the files listed in `Data\Updates\installed.json`. That file also
     drives the "Updated to …" toast.
- **Where the version and repository come from:** the version is `<Version>` in the csproj. The repository is
  `<SatchelUpdateRepository>` in the csproj (as assembly metadata), or Settings → Updates.
- **Debug builds only:** `--debug-update-feed <url>` points the updater at a local stand-in for GitHub.
- **`BackupService.Restart`** (after restoring a backup) also passes `--wait-for` now, so the new copy can't lose the
  race for the single-instance lock.
- **Windows PowerShell 5.1** reads a `.ps1` without a BOM as ANSI. An em dash turns into a quote character and breaks
  the script. Keep `publish.ps1` and `release.ps1` plain ASCII.

### Problems hit and fixed

| Problem | Cause → fix |
|---|---|
| Library tab took ~37 s to show a big document | The hidden editor held the whole text with spell checking → text only while editing, and the page view is kept. |
| Quick switches between documents lost the last edits | The save on switching saved the *new* document → it saves the one you leave. |
| Search across documents up to 1.6 s | Culture-aware comparison → ordinal, ignoring case. |
| Folding a folder jumped the selection to All documents | The selected subfolder vanished → the folded folder is selected. |
| Clean-up after an update could have deleted a user's `*.old` files (e.g. Satchel run from a drive's root) | It scanned the folder → it deletes only the files it renamed itself. |
| `release.ps1` failed to parse | PowerShell 5.1 and UTF-8 without BOM → ASCII-only script. |
| Wrapped CHANGELOG lines showed as separate paragraphs in *What's new* | The notes parser → continuation lines join their item. |
| Sample data in the debug code used real-looking `@gmail.com` / `@outlook.com` addresses | Switched to `mail.example` before the code goes public. |

### Checked

All on the hidden desktop, with throwaway data folders.

- **Build:** Debug and Release, 0 errors, 0 warnings.
- **Library speed** (`--debug-library-perf`, a 645 KB book, a 500-row table, 60 code blocks):

| | Before | After |
|---|---|---|
| Open the long book | 38–39 s | 1.0–2.0 s |
| Open the table / the code | 6.1–7.9 s | 1.0–2.3 s |
| Back to the tab, long book | 35–41 s | ~0.06 s |
| Back to the tab, table / code | 6.9–7.7 s | 0.05–0.3 s |
| Search across documents | 0.35–1.6 s | 0.06–0.09 s |

"Back to the tab" is measured with 400 ms spent on another tab; that's subtracted here.

- **Folders** (`--debug-library-folders`), all as expected:
  - Collections became folders ("Recipes" and "recipes" merged) and none were left in the database.
  - Nesting, moving and the refused move into a folder's own subfolder.
  - Folding, and search across folders.
  - A private folder and its subfolder hidden in the list and in the database, then shown again.
  - Deleting a folder moved its subfolder up and kept the document.
- **Sheets regression** (`--debug-sheets` with the fixture files): passed, alongside the two Library tests.
- **The updater, end to end** (`upd-test.ps1` in the session scratchpad):
  1. A published 1.3.0 checked a local stand-in for GitHub and found 1.3.1, with 3 versions in the feed. A draft and
     a pre-release were ignored.
  2. A download with a wrong checksum was refused, and no file changed.
  3. The real one downloaded with a progress bar, a backup was made, only `Satchel.exe` was replaced, and the app restarted.
  4. 1.3.1 started, removed `Satchel.exe.old` and the download, and showed *Updated to Satchel 1.3.1*. The note
     written before the update was still there.
  5. The requests carried `User-Agent: Satchel/1.3.0`.
- **Parser checks** (console harness): CHANGELOG notes, repository names (pasted URLs, `.git`, invalid names), tags,
  checksum files, GitHub's JSON.

### Not checked

- **Dragging documents and folders onto folders, and the ⋯ → Move to folder menu.** Real drag-and-drop needs the
  mouse; the moves behind them were tested.
- **The updater against the real GitHub.** That happens with your first release (*Open items*).
- From before: Sheets copy/paste (tests never touch the clipboard), real mouse use in Sheets, and file drops from
  Explorer.

### Open items

1. **Your GitHub setup.** Follow [GitHub and update instructions.md](GitHub%20and%20update%20instructions.md):
   account, repository, `<SatchelUpdateRepository>`, first push, release **1.3.0**, and install it on the drive by hand
   once.
2. **Then the test update:** I make a small visible change as **1.3.1**, you release it, and the 1.3.0 copy updates
   itself.
3. **.NET 10.** .NET 9 support ends today. Once the .NET 10 SDK is installed, retargeting is a good next version,
   and it can go out through the updater.
4. **Move `Satchel recovery key.txt` out of the project folder.** `.gitignore` already keeps it out of git.

---

## 2026-11-09, 17:39–21:30 — version 1.2.0

### Asked

> "ok try to implement as many items from the list that we have + maybe we can add a music player from ytune lincks
> on background if possible some kind of add blocker for youtube clips"

"The list" was the backlog pinned at the end of the previous session: 8 of your own ideas plus 11 picks from the
30-idea list.

### Result

Everything on the list was built, plus background music from YouTube / YouTube Music links and ad blocking in the
YouTube player. Satchel is now **version 1.2.0** with **24 tabs** in sidebar sections, and the README describes
all of it. About 120 source files were added or changed.

### Built

**New pages**

| Page | What it does |
|---|---|
| Today | Your day on one page: weather, what's due, events, a mood, an automatic diary of what you did, and *on this day* from earlier years. |
| Clock | Named timers, a stopwatch with laps, world clocks and countdowns. |
| Money | Subscriptions and bills, what's due next, *mark paid* with a payment history, totals per currency, a link to the vault profile, a reminder before each payment. |
| Bookmarks | Cards or a list, folders, tags, favorites, *read later*; import from Chrome, Edge, Brave or a bookmarks HTML file. |
| Photos | A gallery with albums, favorites, captions, a zoomable viewer and a slideshow; pictures are stored encrypted. |
| Watching | Shows and movies looked up on TVmaze / iTunes, episode tracking, statuses as a board or a library. |
| Collections | Your own trackers with the fields you choose (or a template), as cards or a table. |
| Music | YouTube and YouTube Music links in the background: playlists, shuffle, repeat, a queue, media keys, a *now playing* pill. |
| Feeds | Blogs, news and podcasts (the feed is found from the site's address), a reader, OPML, notifications; episodes play in the background and resume. |
| Flashcards | Decks with spaced repetition, import from text, keyboard-only study. |
| Insights | An activity heatmap and charts: tasks, pages read, hours played, things watched, mood, flashcard reviews. |

**Upgrades to existing pages**

- **Tasks** — repeating tasks, also from quick-add (`Water plants every monday`); completing one creates the next.
- **Board** — live cards (task list, note, project, countdown, clock, weather, music) and video study: dock a YouTube
  video, press `Enter` to pin a note at the current moment, and a note's time chip jumps the video back there.
- **Library** — a page view laid out like a PDF (A4, Letter, book or fit to window; one or two pages side by side).
- **Games** — Steam import, from this PC's Steam folder or with a Web API key kept in the Vault; pick the games, sync play time.
- **Dashboard** — weather, world clocks, countdowns, a daily quote, *rediscover* (a random old note) and the music player.
- **Videos** — ad blocking in the player (best effort; it can be switched off in Settings).

**Across the app**

- **Private flag on every item.** A private notebook, list, calendar, profile, album, deck or board hides everything
  in it. `Ctrl+Shift+H` shows or hides them; settings to hide them again at every unlock and to ask for the password.
- **Choose which tabs appear** and in what order; hidden tabs keep their data.
- **Shortcuts:** back / forward (`Alt+←/→`), an `F1` cheat sheet, `Ctrl+Shift+D` Today, `Ctrl+T` Clock,
  `Ctrl+Shift+T` start a timer, music keys.
- **`Ctrl+K` searches every module** and opens the exact item; palette commands for all the new pages.
- Fixed the known 1.1 text bug: Videos now points to *Settings → Sidebar & online*.

### How it works — notes for the next session

- **Private items** are EF Core global query filters on `AppDbContext.ShowPrivate`; child rows follow their parent.
  Every page uses one shared menu command, `PrivacyCommands.Toggle`.
- **The app-wide players live in `MainWindow`**, so they keep playing on every page: `MusicService` + `YouTubePlayer`,
  and `PodcastService` + `AudioHost` (a WebView2 `<audio>` page). Both stay one visible pixel, because Chromium
  won't start media in a hidden page. Ad blocking is in `WebViewEnv`.
- **Payment reminders** remember what they have announced in settings (`RemindedPayments`), not on the subscription
  row, because the Money page saves whole rows.
- **Search results** carry `ItemRef(ParentId, Id)`, so a hit opens the item inside its deck, feed, playlist or collection.
- **`DataStore` uses `ConfigureAwait(false)`** and settings save asynchronously; a synchronous save on the UI thread deadlocked.
- **Schema:** additions only. On the first start of 1.2, `SchemaUpgrader` makes a backup, then creates the new tables and columns.

Where to look (under `src/Satchel/`):

| Area | Files |
|---|---|
| Private items | `Services/PrivacyService.cs`, `Core/PrivacyState.cs`, `Data/AppDbContext.cs` |
| Players and ad blocking | `Services/MusicService.cs`, `Controls/YouTubePlayer.cs`, `Services/PodcastService.cs`, `Controls/AudioHost.cs`, `Services/WebViewEnv.cs` |
| Reminders, repeating tasks | `Services/ReminderService.cs`, `Services/TaskRepeat.cs` |
| Today's diary | `Services/Diary.cs` |
| Flashcard scheduling | `Services/SpacedRepetition.cs` |
| Steam | `Services/SteamService.cs`, `ViewModels/GamesViewModel.Steam.cs` |
| Board live cards | `ViewModels/BoardViewModel.Live.cs`, `ViewModels/BoardLive.cs` |
| Search | `Services/SearchService.cs` |
| Charts, page view | `Controls/Charts.cs`, `Controls/PagedReader.cs` |
| Test automation (Debug only) | `DebugAutomation.cs` |

### Problems hit and fixed

| Problem | Cause → fix |
|---|---|
| The private-items test hung | A synchronous settings save deadlocked with the database gate → asynchronous saves and `ConfigureAwait(false)`. |
| Music stuck on "buffering" | Chromium defers media in hidden pages → the player stays one visible pixel. |
| Podcasts wouldn't play: "Only site-of-origin pack URIs are supported for media" | WPF's media player can't follow podcast redirect links → podcasts play through a WebView2 audio page. |
| Opening Clock sometimes showed Today | Navigation was dropped while Today waited for the weather → the weather loads in the background and navigation is queued. |
| Searches on encrypted fields found nothing | Encrypted columns can't be compared in SQL → filtered in memory. |
| The "already reminded" mark on a bill would have been overwritten | The Money page saves whole rows → the mark moved to settings. |
| Screenshots came back blank | Windows was locked, and WPF screenshots render nothing then. Not an app bug. |
| Compiler warnings | Fixed: 0 warnings in Debug and Release. |

### Checked

All tests ran on a hidden desktop (never your mouse or keyboard), against a real database file.

- **Build:** Debug and Release, 0 errors, 0 warnings.
- **Screenshots, before the PC locked:** Clock, Today, Dashboard, Settings, board live cards and video study, Library page view.
- **Earlier automated tests:** private items hidden and shown again across notes, vault, boards and search; music keeps
  playing across pages and pauses; a note's time chip jumps the video to its moment; the Steam file reader works on sample files.
- **Module test** (`--debug-modules-test`):

| Area | Result |
|---|---|
| Search | 8 of 8 test queries found the right kind of item |
| Command palette | the new commands are there |
| Money | marking Netflix paid moved it from 12 Nov to 12 Dec and logged one payment |
| Watching | marking an episode watched: 3 → 4 watched, next is S01E05 |
| Flashcards | 12 answers saved (review log 20 → 32), card states updated |
| Photos | 9 → 11 photos imported, favorite saved, the viewer loads the full-size image |
| Collections | a tracker with 7 fields and 2 items saved |
| Bookmarks | the browser-bookmark readers parse sample files |
| Feeds / podcasts | an episode streamed (muted) and pausing saved its position |
| Steam | the import window opens |
| Reminders | the overdue test bill was announced exactly once |
| Insights, Today | the numbers match the test data |

- **Logs:** no binding errors, no exceptions in `Data\Logs`.

### Not checked

- **Screenshots of Money, Watching, Flashcards, Photos, Collections, Bookmarks, Feeds, Insights and the Steam window.**
  The PC was locked for the rest of the session, so they still need a look on an unlocked PC.
- Steam import on a PC with Steam installed (only sample files were available).
- Ad blocking against today's YouTube. It is best effort by nature.
- Known and harmless: a brief binding warning from an analog clock's hands when it first appears. The hands draw
  correctly, so it was left as is.

### Open items

1. **.NET 10.** Support for .NET 9 ends **2026-11-10**. Install the .NET 10 SDK, then retarget (README → *Extending*).
2. **Visual check** of the pages listed under *Not checked*, in a light and a dark theme.
3. **Publish.** `.\publish.ps1` updates `release\Satchel\` and leaves your `Data\` folder alone. Not run yet.
4. **Test 1.2 yourself.** The test log (https://claude.ai/artifact/SjgXa7XZfzmK5ktusdBkEH) covers 1.1 only; a 1.2
   checklist can be made the same way.

---

## 2026-11-09, until 17:30 — versions 1.0 and 1.1

- **1.0:** notes, tasks, calendar, vault, contacts and board. Portable: everything lives in `Data\` next to the exe,
  with per-field AES-256-GCM encryption. Themes without purple or indigo.
- **1.1:** board upgrade (rich text, images, link cards, connections, a corkboard with swinging strings), Projects,
  Library (Markdown), Reading, Games, Sketch and Videos (YouTube channels and a player with timestamped notes).
- Then: the 1.1 test checklist (https://claude.ai/artifact/SjgXa7XZfzmK5ktusdBkEH), an answer on whether n8n could
  go inside Satchel (nothing built), and a list of 30 ideas, from which the 1.2 backlog was pinned.
