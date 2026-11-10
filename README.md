# Satchel — portable personal organizer

Notes, tasks, calendar, projects, an **encrypted password vault**, contacts, money (subscriptions and bills),
bookmarks, corkboards, a sketchbook, a Markdown library, photos, a reading tracker, a game backlog, shows and movies,
custom collections, a YouTube companion, background music, feeds and podcasts, flashcards, timers and insights —
in one self-contained folder you can carry on a flash drive. No installation, no cloud storage, no accounts.

Built with C# / WPF on .NET 9, SQLite (EF Core), CommunityToolkit.Mvvm, Markdig and WebView2.

---

## Quick start

```powershell
# run from source
dotnet run --project src\Satchel

# build the portable release (self-contained, no .NET needed on the target PC)
.\publish.ps1              # ~165 MB, opens in about a second
.\publish.ps1 -Compress    # ~80 MB, but slower to start and uses about twice the memory
```

Then copy **`release\Satchel\`** to your flash drive and run `Satchel.exe`.

To publish a version that copies of Satchel can update to by themselves, `.\release.ps1` builds and packages it for a
GitHub release — step by step in **[GitHub and update instructions.md](GitHub%20and%20update%20instructions.md)**.
What changed in each version is in [CHANGELOG.md](CHANGELOG.md).
On first launch you create a master password and get a **recovery key** — store it somewhere safe,
away from the drive.

Open `Satchel.sln` in Visual Studio 2022 / Rider if you prefer an IDE.

## Where your data lives

Everything is stored next to the executable:

```
Satchel\
├─ Satchel.exe                 (+ a few native DLLs)
└─ Data\
   ├─ satchel.db               ← all your content (encrypted)
   ├─ Backups\                 ← daily automatic backups (last 10 kept) + a copy before every upgrade
   ├─ Logs\                    ← error log (never contains your content)
   ├─ Updates\                 ← the release notes from the last update check (a download sits here briefly)
   └─ WebView2\                ← player cache for videos, music and podcasts (only if you use them)
```

* Nothing is written to AppData or the registry.
* Running from source, the data folder is `src\Satchel\bin\<Config>\net9.0-windows\Data`.
* `Satchel.exe --data "D:\Somewhere\Else"` uses another data folder (handy for a second vault).
* Re-running `publish.ps1` replaces the app files but **never touches `Data\`**.
* Updating: **Settings → Updates** installs a new version from GitHub (it backs up the database first), or copy the
  new app files over the old ones by hand. Older databases are upgraded automatically on the next start (a backup is
  made first).
* Close Satchel before unplugging the drive.

## Features

**Dashboard** — widgets: greeting and clock, up next, focus tasks, pinned notes, quick capture, vault favorites with
live 2FA codes, password health, calendar, stats, birthdays, active projects, continue reading, new videos, weather,
countdowns, a daily quote and, if you add them, world clocks, *rediscover* (a random old note) and the music player.
* **Customize** (`Esc` when done): drag a widget to move it — it lands in the spot closest to where you hold it — and
  drag its corner to resize: the width snaps to columns, the height to 10 px (a widget given a fixed height shows more
  rows and scrolls; double-click the corner to fit the content again). Hide widgets or add them back from the bar.
* **Profiles** — different setups of widgets (say *Work* and *Evening*), each with its own widgets, order and sizes.
  Pick an icon for a profile and it gets a one-click shortcut in the top bar; `Alt+1 … 9` switches between all of them.

**Today** (`Ctrl+Shift+D`) — one page for a day: weather, what's due, events, a mood, and an **auto-diary** that
writes itself from what you did (tasks done, notes, reading, play, videos, episodes…) plus *on this day* memories
from earlier years. Step back through past days.

**Notes** — rich text (formatting, colours, pasted or dropped images, and checklist lines you tick right in the text —
`Ctrl+Shift+C`), checklists, lists, code snippets and journal entries. Notebooks, tags, pinning, colours, archive,
trash with undo, Markdown export.

**Tasks** — My day, Upcoming, lists, natural quick-add (`Call Ana tomorrow 5pm !high #work`), list and kanban views,
subtasks, reminders and **repeating tasks** (daily, weekdays, weekly, every other week, monthly, yearly or every
*n* days/weeks/months — also from quick-add: `Water plants every monday`). Completing one creates the next.

**Calendar** — month/week/agenda, drag to reschedule or resize, recurring events, coloured calendars, tasks and
birthdays on the calendar, `.ics` import/export.

**Projects** — a goal, status, dates, colour/icon/cover, milestones (drag to reorder), a task board
(To do / In progress / Done, drag between columns), notes, resources (links with fetched titles), a log of
updates, wins, blockers and decisions — and its own corkboard. A project's task list, notebook and board also
appear in Tasks, Notes and Board.

**Vault** — profiles with any number of accounts, 13 templates, custom fields, live TOTP codes, password
generator, strength meter, history, expiry, password health, CSV import/export.

**Contacts** — phones, emails, address, birthday, photo; vCard import/export.

**Money** — subscriptions and bills (weekly to yearly, every *n* days, or once), what's due next, *mark paid* with a
payment history, monthly and yearly totals per currency, spending by category, a link to the matching vault profile,
and a reminder a few days before each payment.

**Gamble** — the future home for trading and bets; a placeholder for now (hide it in Settings if you don't need it yet).

**Sheets** — spreadsheets with formulas:
* Open **Excel** (`.xlsx` with formulas, number formats, bold / italic, alignment, column widths and frozen rows; old
  `.xls` with its values) and **CSV**, or drop them on the page. Export to `.xlsx` (opens in Excel, LibreOffice and
  Google Sheets), to CSV for Excel on this PC, or copy into the Library as tables.
* 100 functions: SUM, AVERAGE, COUNT(A), MIN / MAX, ROUND, IF / IFS / IFERROR, AND / OR, SUMIF(S), COUNTIF(S),
  AVERAGEIF(S), VLOOKUP / HLOOKUP / XLOOKUP, INDEX / MATCH, TEXT, TEXTJOIN, LEFT / MID / RIGHT, SUBSTITUTE, DATE, TODAY,
  DATEDIF, EOMONTH, NETWORKDAYS and more. References to other sheets work (`Budget!B6`); a formula that uses its own
  result shows `#CYCLE!`.
* Type numbers the way your Windows region writes them (`1 234,50`), percentages (`12%`), amounts (`12,50 лв`,
  `€12.50`), dates and times.
* Click cells into a formula while typing it; copy, cut and paste — also with Excel — with formulas adjusting; fill
  down / right; AutoSum; sort (a header row stays on top); insert and delete rows and columns (formulas follow);
  number formats; undo / redo; several sheets per file; the sum, average and count of the selection at the bottom.

**Bookmarks** — cards or a list, folders, tags, favorites and *read later*; titles, descriptions and icons are fetched
for you. Import from Chrome / Edge / Brave (read only) or any browser's bookmarks HTML; export HTML. Drop links in.

**Board** — free canvases of notes, **photos** and **link cards**:
* Notes edit like the Notes tab: a floating toolbar with bold, italic, underline, strike, headings, **checklists**
  (click a box to tick it), bulleted and numbered lists, colours, highlight, code, sizes and images. Double-click a
  note's top bar (or ⤢) to open it large with the full toolbar.
* **Insert from Satchel**: put notes (copied as editable board notes — checklists keep their boxes), photos from the
  gallery, videos, games, books, shows and movies, bookmarks, projects and task lists on the board — several at
  once, from different tabs. Games, books, shows, projects and task lists become live cards; new cards find free space.
* Paste or drop images (photo notes with captions, double-click to enlarge) and links (title and preview image
  are fetched; YouTube links show the thumbnail and open in the Videos player).
* **Connect notes**: drag the dot on a note's edge onto another note. Connections can have a colour, a label and an arrow.
* **Corkboard style**: cork texture, wooden frame, push-pins (pick their colour) and **strings of yarn that sag
  and swing** when you move notes. Drag from a pin to tie a new string.
* **Live cards** that stay up to date: a task list, a note, a project, a countdown, a world clock, the weather,
  what's playing, a game, a book and a show or movie.
* **Private notes**: mark a single note private from its ⋯ menu, or the whole board from the board menu. A private
  note takes its connections with it while private items are hidden.
* **Video study**: dock a YouTube video on the board and press `Enter` to pin a note at the current moment; notes
  with a time chip jump the video there.
* Zoom, tidy up, multiple boards.

**Sketch** — a sketchbook for doodles, diagrams and game-level ideas: pen (pressure-sensitive with a tablet),
marker, eraser, lasso select (move/resize/delete), 12 colours, sizes, undo/redo, paper styles (plain, grid,
dots, lined, dark), a reference image to trace over, PNG export, copy, and *To board* (adds it as a photo note).

**Library** — drop files anywhere on the page (or use *Import*), or write your own:
* **Markdown** (`.md`, with the pictures it points to) and **text** (`.txt` in UTF-8, UTF-16 or an old Windows code
  page such as Cyrillic 1251; line breaks are kept).
* **Word** — `.docx` and old `.doc` become Markdown with headings, bold / italic / strike / code, bulleted and
  numbered lists, links, tables and footnotes (pictures too, for `.docx`). **RTF** works the same way.
* **Excel and CSV** (`.xlsx`, `.xls`, `.csv`, `.tsv`) become tables, one per sheet; the toast offers *Open in Sheets*
  for the real spreadsheet.

The Library itself:
**Folders** in the left pane, as deep as you like: drag documents (and folders) onto a folder, or use ⋯ → *Move to
folder*; a search looks in every folder. A private folder hides everything in it, its folders included. Deleting a
folder keeps what's inside (it moves up a level), with *Undo*. Documents from before folders keep their old
"collection" as a folder. Big documents stay quick: the page is kept while you visit other tabs, and the editor only
loads the text once you start editing.
Reader with themes (*Match app*, *Paper*, *Sepia*, *Night*), fonts, size, line spacing and width, justify +
hyphenation, table of contents, find (Ctrl+F), remembered reading position, edit mode with live preview,
syntax-coloured code, tables, task lists, footnotes. **Page view** lays a document out like a PDF (A4, Letter,
book or fit-to-window, margins, one or two pages side by side) with page turning from the keyboard.
Export `.md` or print / save as PDF.

**Photos** — a gallery for photos and screenshots: add files or whole folders, drop or paste them; grouped by
month, albums, favorites, captions, a viewer with zoom, slideshow, copy / save, *pin to a board*. Pictures are
stored encrypted inside Satchel (up to 2048 px, EXIF date and rotation kept); your originals are never changed.

**Reading** — shelves (reading, want to read, finished, stopped), progress by page with +10/+25 buttons,
reading sessions and a 14-day chart, streak, yearly goal, ratings, notes, quotes (with a quotes wall), and an
online search (Open Library) that fills in the details and the cover.

**Games** — a status board (Playing, Backlog, Wishlist, Completed, Dropped — drag between columns) and a cover
library, hours and completion, ratings, notes, a **session timer**, play log, and Steam search for cover art and genres.
**Steam import**: read the games on this PC (no account needed) or your whole Steam library (a Web API key kept in the
Vault), pick which ones to add, and sync play time.

**Watching** — shows and movies: search fills in posters and details (TVmaze, iTunes), episode tracking by season,
*watched next*, statuses (watching, planned, on hold, completed) as a board or a library.

**Collections** — your own trackers for anything (wine, plants, LEGO, gear…): pick the fields (text, number, date,
rating, choice, yes/no, link…) or start from a template, then browse as cards or a table.

**Videos** — follow **YouTube channels** (paste a link or @handle — no account, no API key; uses the public
RSS feeds) and get their new uploads with a badge and a notification. Save any video, *watch later*, history,
hide Shorts. Watch **inside Satchel** with **timestamped notes** (click a time to jump there), resume where you
stopped, and open a **notes board** for a video (a corkboard with the video and your notes, strung together).
The player **blocks ads** (best effort — YouTube changes often; switch it off in Settings if a video misbehaves).

**Music** — play **YouTube and YouTube Music links** in the background: paste songs or whole playlists, shuffle,
repeat, a queue, volume, media keys, a *now playing* pill in the title bar and controls on the lock screen. A few
tracks don't allow playback outside YouTube; those are skipped.

**Feeds** — follow blogs, news sites and **podcasts** (paste the site; Satchel finds the RSS / Atom feed), unread
and starred, a clean reader, OPML import / export, notifications. Episodes play in the background with speed
control and resume where you stopped.

**Flashcards** — decks with spaced repetition (again / hard / good / easy, learning steps, a daily limit for new
cards), import from text, export, keyboard-only study.

**Clock** (`Ctrl+T`) — several named timers (`Ctrl+Shift+T`: “25 Focus”), a stopwatch with laps, world clocks and
countdowns to dates (optionally every year and on the dashboard).

**Insights** — charts of what you do: an activity heatmap (click a day to open it), tasks done, pages read, hours
played, things watched, mood and flashcard reviews.

**Private items** — mark anything private (a note, task, event, account, photo, bookmark…; a private notebook,
list, calendar, profile, album, deck or board hides everything in it). Show or hide them in **Settings → Privacy** or
with `Ctrl+Shift+H` (pick other keys in Settings → Key bindings); by default they're hidden again every time you unlock,
and showing them can ask for your password.
Hidden items are left out of lists, search, the dashboard, Today and Insights.

**Everywhere** — `Ctrl+K` palette searches everything (every module, from notes to songs and flashcards) and runs
commands; back / forward between pages (`Alt+←/→` or the mouse side buttons); every keyboard shortcut in a cheat sheet
(`F1`) and, searchable, in Settings → Keyboard shortcuts; page transitions, animated reordering, toasts with *Undo*.

## Customization

Settings → Appearance: 9 themes (none purple), 14 accent colours or any hex, font, interface size,
corner roundness, animations, ambient glow, card shadows, collapsed sidebar.
Settings → General: name, start page, date language/format, clock, first day of week.
Settings → Key bindings: your own keys for any tab (Ctrl or Alt with a key, or F2–F12 on its own) and for showing
private items. Click a box and press the keys; keys Satchel already uses are refused with what they do, keys another tab
had move over, and `Ctrl+1 … 9` can be given to any tab (the rest keep following the sidebar). Sidebar tooltips, the
search palette and the cheat sheet show your keys.
Settings → Keyboard shortcuts: every shortcut, with a search box.
In the sidebar, click a section name (Organize, Keep, Create…) to fold its tabs away; the page you're on stays visible.
Settings → Sidebar & online: choose which tabs are shown and their order (hidden tabs keep their data), section
headers, online features on/off, video and feed checking, notifications, ad blocking in the player.
Settings → Privacy: show private items, hide them again at every unlock, ask for the password to show them.
Settings → Updates: the version, *Check for updates* and *Install*, the GitHub repository updates come from, a daily
automatic check (it only tells you), and *What's new* — the notes of every published version.

## Online features (optional)

Some features use the internet when available: YouTube channel updates, the video and music players, feeds and
podcasts, link and bookmark previews, weather (Open-Meteo), book (Open Library), game (Steam), show (TVmaze) and
movie (iTunes) lookups, the optional Steam library import, and update checks (GitHub's public releases list). Nothing
is uploaded — they only fetch public pages (the Steam import sends your Steam ID and your own API key to Steam). Turn them all off in
**Settings → Sidebar & online**; everything else works fully offline.

The video, music and podcast players use **Microsoft Edge WebView2** (built into Windows 11, installed on most
Windows 10 PCs). If it's missing, videos open in your browser instead. Its cache and cookies live in `Data\WebView2` (not
encrypted); *Clear player data* removes them.

## Security

* Content is encrypted with **AES-256-GCM** per field (all modules, including images, sketches and documents).
* A random 256-bit data key encrypts the content; it is stored only *wrapped* by your master password
  (**PBKDF2-SHA256, 600,000 iterations**) and by your recovery key.
* The key exists only in memory while unlocked. Auto-lock on inactivity, Windows lock and (optionally) minimize.
* Copied secrets are excluded from clipboard history/cloud sync and wiped after a delay.
* Stored unencrypted: UI settings, the optional password hint, structural data (dates, colours, order,
  positions), and the video player cache.
* *Private* is about keeping things out of sight (screen sharing, someone looking over your shoulder) — the
  encryption is the same for every item.
* **If you lose both the master password and the recovery key, the data cannot be recovered.** Keep backups.

## Keyboard shortcuts

`F1` (or `Ctrl+/`) shows them all inside the app, and Settings → Keyboard shortcuts lists them with a search box.
Settings → Key bindings changes the keys for tabs and for private items; the defaults are below.

| Keys | Action | Keys | Action |
|---|---|---|---|
| `Ctrl+K` / `Ctrl+P` | Command palette / search | `Ctrl+1 … 9` | Switch page (sidebar order, unless you gave the keys to a tab) |
| `Alt+1 … 9` | Dashboard profile 1 … 9 | `Esc` on the dashboard | Finish customizing |
| `Ctrl+N` | New item on current page | `Ctrl+Tab` | Next page (`+Shift`: previous) |
| `Alt+← / →` | Back / forward | `Ctrl+,` | Settings |
| `Ctrl+F` | Search / find on page | `Ctrl+L` | Lock |
| `Ctrl+Shift+D` | Today | `Ctrl+T` | Clock |
| `Ctrl+Shift+T` | Start a timer | `Ctrl+Shift+H` | Show / hide private items |
| `Ctrl+Shift+Space` | Music play / pause | `Ctrl+Shift+← / →` | Previous / next song |
| `Ctrl+\` | Collapse the sidebar | `Ctrl+= / − / 0` | Interface size |
| `Esc` | Close dialog / palette | `Ctrl+B/I/U` | Format text |
| `Ctrl+V` on a board | Paste image, link or text as a note | `Delete` | Remove selected connection |
| `P` `M` `E` `S` in Sketch | Pen, marker, eraser, select | `Ctrl+Z / Ctrl+Y` | Undo / redo (Sketch) |
| `F2` / typing in Sheets | Edit a cell | `Ctrl+D` / `Ctrl+R` in Sheets | Fill down / right |
| `Alt+=` in Sheets | AutoSum | `Ctrl+;` in Sheets | Today's date |
| `Space`, `1–4` in Flashcards | Show answer, grade | `← / →` in Photos and page view | Previous / next |

## Project structure

```
src/Satchel/
├─ Core/          paths, formatting, colours/palettes, icon glyphs, YouTube link parsing, animation helpers
├─ Security/      AES-GCM crypto, key management, password generator & strength, TOTP, clipboard
├─ Models/        EF entities ([Encrypted] marks encrypted columns)
├─ Data/          DbContext, SchemaUpgrader (adds new tables/columns automatically), DataStore
├─ Services/      settings, themes, session/lock, dialogs, toasts, backups, reminders, search, web access,
│                 Markdown renderer, video feeds, RSS / podcasts, music, timers, weather, Steam, privacy,
│                 spaced repetition, images, CSV / iCalendar / vCard, updates (GitHub releases),
│                 Import/ (Word, RTF, text, Excel → Markdown), Sheets/ (formula engine, xlsx / xls / csv)
├─ ViewModels/    one per page + dialogs + dashboard widgets
├─ Views/         XAML pages and dialog templates (DataTemplates.xaml maps VMs → views)
├─ Controls/      board connectors (rope physics), cork texture, rich text editor, animated panels, charts,
│                 paged reader, YouTube and audio players, weather icons, date/time pickers, avatar, …
├─ Behaviors/     drag & drop engine, attached UI helpers, motion
└─ Themes/        control styles (all colours come from ThemeService at runtime)
```

### Extending

* **Rename the app** — `Core/AppInfo.cs` (`Name`), plus `AssemblyName` in the `.csproj`.
* **Add a theme** — one line in `ThemeService.Themes`.
* **Schema changes** — add properties or entities (and a `DbSet`); `SchemaUpgrader` creates missing tables and
  columns on the next start. New columns get neutral defaults (0, empty, null). New encrypted columns just
  need `[property: Encrypted]`.
* **Add a page** — a `PageViewModel`, a view, a `DataTemplate` in `Views/DataTemplates.xaml` and a `NavItem`
  in `ShellViewModel` (with its sidebar section).
* **Retarget to .NET 10** — change `net9.0-windows` → `net10.0-windows` in the `.csproj` and the publish
  profile, and bump the EF Core / DI packages to 10.x.

### Debug automation (Debug builds only)

Used to verify the UI without clicking through it; compiled out of Release builds.

```powershell
Satchel.exe --data .\testdata --debug-password "test-pass-123" `
  --debug-shots "Dashboard=dash.png;Board:2=board.png;ProjectDetail:Tasks=tasks.png" --debug-exit
```

Seeds: `--debug-seed-vault`, `--debug-seed-contacts`, `--debug-seed-boards`, `--debug-seed-library`,
`--debug-seed-modules` (projects, books, games, a sketch, a followed YouTube channel), `--debug-seed-new`
(bookmarks, subscriptions, shows, a deck, a collection, photos, journal moods), `--debug-seed-feeds` (two real feeds).
Tests: `--debug-modules-test <file>` (drives the newer modules and checks the database), `--debug-dash-test <dir>`
(customize-mode frame rate, drag placement, resizing, profiles, the Settings shortcut list; add `--debug-dash-variants`
to compare kinds of motion and `--debug-software` for WPF's software renderer), `--debug-board-upgrade <dir>` (checklists in
board notes, the large editor, Insert from Satchel, private notes and their connections, folding sidebar sections),
`--debug-keybindings <dir>` (Settings → Key bindings: parsing, refused and moved keys, the listening box, running the keys,
saving and resetting), `--debug-sheets <dir>` with `--debug-fixtures <folder>` (imports every file in the folder into the
Library, then types a spreadsheet through the grid: formulas, fill, sort, insert, undo, tabs, saving, import and export),
`--debug-library-perf <dir>` (timings for big documents: opening, coming back to the tab, live preview, search),
`--debug-library-folders <dir>` (old collections becoming folders, nesting, moving, refused moves, private folders,
deleting), `--debug-update-test <dir>` with `--debug-update-feed <url>` (a published build checks a local stand-in for
GitHub, refuses a damaged download, installs the update and restarts; the new version runs `--debug-after-update <dir>`),
`--debug-privacy-test <file>`,
`--debug-music <dir>`, `--debug-board-new <dir>`, `--debug-steam <file>`, `--debug-drag-test <dir>`,
`--debug-lock-cycle <dir>`, `--debug-richtext-test <dir>`.
Other flags: `--debug-theme <key>`, `--debug-bindings <log>`, `--debug-schema-log <file>`, `--debug-tree` (writes the
visual tree next to each screenshot).

Screenshots use `RenderTargetBitmap`, which draws nothing while Windows is locked — run them on an unlocked session.
Special shot targets: `BoardFocus`, `BoardSwing`, `BoardLink`, `LibrarySepia/Night/Edit/Find`, `ProjectDetail:<tab>`,
`ReadingDetail`, `ReadingQuotes`, `GamesDetail`, `GamesLibrary`, `SketchEdit`, `VideoPlay`.
