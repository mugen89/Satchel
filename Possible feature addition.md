# Possible feature additions

Ideas for after Satchel 1.2.0, collected 2026-11-09. None of these are built yet.

**How to use it:** tick what you want (`- [x]`) and tell me *"build the ticked items in Possible feature addition.md"*.
Sizes are rough: **S** a small addition · **M** about one tab's worth of work · **L** bigger than that.

> **Before new features:** the move to .NET 10 (support for .NET 9 ends 2026-11-10) and a visual check of the 1.2
> pages. Both are in [Session log.md](Session%20log.md) under *Open items*.

## Library folders and updates — next steps (added 2026-11-10, with 1.3.0)

- [ ] **Drop files onto a folder** (S) — files dropped on a folder row are imported straight into it (today they go
  into the folder that's open).
- [ ] **Show a document's folder** (S) — in *All documents* and in search results, a small folder name under the title.
- [ ] **Huge documents in under a second** (M) — lay out the first screens first and the rest while you read.
- [ ] **Release from GitHub itself** (M) — a GitHub Action that builds the zip and checksum and publishes the
  release when you push a version tag, so `release.ps1` and the upload aren't needed.
- [ ] **Signed releases** (M) — the updater checks a signature made with your own key, not only the checksum, so even
  someone with access to your GitHub account couldn't publish an update the app would install.
- [ ] **Early versions** (S) — a setting to also get pre-releases, for trying a version before it goes to everyone.

## Sheets and Library — next steps (added 2026-11-10)

The Sheets tab and the Library's Word / Excel import are new. Natural follow-ups:

- [ ] **Charts in Sheets** (M) — a column, line or pie chart from a selected range, kept with the sheet.
- [ ] **Colours, borders and wrapped text in Sheets** (M) — cell fill and text colours, borders, taller rows that wrap.
- [ ] **Find and replace in Sheets** (S) — Ctrl+F across the sheet or the whole workbook.
- [ ] **Formula help while typing** (S–M) — function names to pick from as you type `=VL…`, with their arguments.
- [ ] **Merged cells and filters** (M) — merge across columns; a filter row to show only some rows.
- [ ] **Money ↔ Sheets** (S) — send the Money tab's payments to a spreadsheet for your own sums and charts.
- [ ] **PDF and EPUB in the Library** (M–L) — read them as documents (already in the older ideas below).
- [ ] **Pictures from old .doc files** (M) — they live in a separate drawing format inside the file.

## My picks

1. **Backup to a second place.** On a flash drive, losing or breaking the stick is the biggest real risk, and
   today's automatic backups sit on the same stick.
2. **Tray and quick capture.** 1.2 added bill, task and podcast reminders, but they only go off while Satchel is open.
3. **Save articles to the Library + full articles in Feeds.** One reader that finishes the read-later loop for
   Bookmarks and Feeds.
4. **Year in review.** Cheap now that Insights already gathers everything.

## 1. Finishing touches to 1.2

These were in the original descriptions of things you picked, but didn't make it into 1.2.

- [ ] **Bookmarks: save an article to the Library** (S–M) — keep a page's readable text and pictures as a Library
  document you can read offline.
- [ ] **Insights: year in review** (M) — a yearly recap: books and games finished, hours played, shows watched,
  busiest days, mood across the year.
- [ ] **Flashcards from what you've collected** (M) — make cards from a note's lines or from your book quotes in Reading.
- [ ] **Bills on the Calendar** (S) — Money's due dates as a read-only calendar layer.
- [ ] **More live cards** (S–M) — a book, game, show or video on a board that stays in sync (progress, next episode,
  hours played).

## 2. New ideas for the 1.2 pages

- [ ] **Podcasts: queue, sleep timer, downloads** (M) — an *up next* list, stop after a set time or at the end of an
  episode, and optional downloads into `Data\` for offline listening (they take space on the drive).
- [ ] **Music: songs from the drive** (M) — play MP3 / M4A / FLAC files kept on the flash drive in the same player
  and queue as the YouTube links.
- [ ] **Feeds: full article** (S–M) — fetch the whole post when a feed only sends a summary. Shares the reader with
  *save an article to the Library* above.
- [ ] **Watching: new-episode alerts** (S) — Satchel already stores episode air dates; get a notification when the
  next episode of a show you're watching comes out.
- [ ] **Clock: focus sessions** (S–M) — work / break cycles tied to a task, with focus time in Insights and the auto-diary.
- [ ] **Photos: rotate, crop, find duplicates** (S–M) — fix a sideways photo, trim a screenshot, clean up pictures
  imported twice.
- [ ] **Collections: CSV import and export** (S) — bring in a spreadsheet you already keep, or take a tracker out.
- [ ] **Money: one total in your currency** (S) — totals are per currency today; add exchange rates (typed in, or
  fetched when online).
- [ ] **Imports from other apps** (M) — Goodreads → Reading, Letterboxd / IMDb → Watching, Anki → Flashcards,
  Google Keep → Notes, Todoist → Tasks.

## 3. Other new ideas across the app

- [ ] **Export everything** (S–M) — one button that writes all your content to plain files (Markdown, CSV, iCalendar,
  vCard, images) in a folder you choose, so nothing is ever locked inside Satchel. Unencrypted, with a warning.
- [ ] **Keep two copies in sync** (L) — for example the stick and a home PC, through a shared folder, with a clear
  choice when the same item changed in both places.
- [ ] **Interface in another language** (L) — dates already follow the language you pick; this would translate the
  rest of the app.

## 4. From the 2026-11-09 ideas list, not picked that round

The original descriptions, still possible.

**New tabs**

- [ ] **Habits** (M) — daily or weekly habits, with streaks and a heatmap of the whole year. They would also show on Today.
- [ ] **Worlds** (L) — a wiki for game ideas: characters, places, items and lore pages that link to each other, a
  board showing how they relate, and a game design document template.
- [ ] **Pixel studio** (M) — a pixel-art editor for sprites, with palettes, layers, frame-by-frame animation and
  sprite-sheet export.
- [ ] **Documents** (M) — an encrypted file cabinet for ID scans, contracts, warranties and receipts, with expiry
  reminders and PDF preview.
- [ ] **Automations** (S–M) — an n8n tab and a webhook link to your n8n.

**Upgrades to existing tabs**

- [ ] **Board: frames, shapes and drawing** (M) — labelled frames that group notes, shapes for diagrams, drawing
  directly on the board, checklist notes and selecting several notes at once.
- [ ] **Board: presentation mode** (M) — move from note to note along the strings with smooth zooming, plus a
  mini-map and PNG / PDF export.
- [ ] **Library: e-books and PDFs** (L) — EPUB books in the same reading themes, PDFs in a built-in viewer, and
  highlights with margin notes.
- [ ] **Calendar subscriptions** (S) — add an online calendar link (public holidays, a shared Google or Outlook
  calendar) as a read-only layer.
- [ ] **Vault: breach check and QR codes** (S–M) — check your passwords against known data breaches without sending
  the passwords themselves, and show Wi-Fi passwords as a QR code your phone can scan.
- [ ] **Videos: mini-player** (M) — keep a video playing in a small floating window while you use other tabs, with
  playback speed and chapters.
- [ ] **Sketch: layers and shapes** (M) — layers, lines and shapes, a ruler and mirrored drawing.

**Across the app**

- [ ] **Tray and quick capture** (M) — Satchel keeps running in the system tray, so reminders and checks continue;
  a keyboard shortcut opens a quick note or task box from any app.
- [ ] **Links between everything** (L) — type `[[` to link notes, projects, books and boards, see what links back to
  each item, and a graph view that moves like the corkboard strings.
- [ ] **Templates** (S) — ready-made weekly review, game idea, trip plan and meeting notes for notes, projects and
  boards, plus your own.
- [ ] **Theme studio** (M) — design your own themes (colours, fonts, corner roundness, page backgrounds, subtle
  animation) and share them as files.
- [ ] **Backup to a second place** (M) — automatic encrypted backups to another drive or a cloud folder, plus a
  safe-eject button.
- [ ] **Achievements** (S–M) — points and badges for streaks, finished books and cleared backlogs, with an unlock animation.
- [ ] **Ask Satchel** (M–L) — optional AI help with your own API key: summarize a document, turn a note into tasks,
  or build a board from a brain dump.
