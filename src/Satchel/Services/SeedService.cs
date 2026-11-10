using System.Windows;
using System.Windows.Documents;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>Creates a friendly starter set of content on first run.</summary>
public sealed class SeedService
{
    private readonly DataStore _store;

    public SeedService(DataStore store) => _store = store;

    public async Task SeedAsync()
    {
        // Rich text has to be built on the UI thread.
        var (welcome, welcomePlain) = BuildWelcome();
        var today = DateTime.Today;

        await _store.RunAsync(db =>
        {
            if (db.Notes.Any() || db.Tasks.Any()) return;

            var personal = new Notebook { Name = "Personal", Color = "teal", Icon = Glyphs.Book, SortOrder = 0 };
            var work = new Notebook { Name = "Work", Color = "sky", Icon = Glyphs.Work, SortOrder = 1 };
            db.Notebooks.AddRange(personal, work);
            db.SaveChanges();

            var checklist = new List<ChecklistItem>
            {
                new() { Text = "Create your vault", IsChecked = true },
                new() { Text = "Add your first account in the Vault" },
                new() { Text = "Drag cards around to rearrange them" },
                new() { Text = "Pick a theme in Settings → Appearance" },
                new() { Text = "Press Ctrl+K to search everything" },
                new() { Text = "Copy the Satchel folder to your flash drive" },
            };

            db.Notes.AddRange(
                new Note { Type = NoteType.Text, Title = "Welcome to Satchel 👋", Content = welcome, PlainText = welcomePlain, Color = "mint", IsPinned = true, NotebookId = personal.Id, SortOrder = 0, Tags = "getting-started" },
                new Note { Type = NoteType.Checklist, Title = "Getting started", Content = Note.SerializeItems(checklist), PlainText = string.Join("\n", checklist.Select(c => c.Text)), Color = "sand", IsPinned = true, NotebookId = personal.Id, SortOrder = 1 },
                new Note { Type = NoteType.List, Title = "Groceries", Content = Note.SerializeItems([new() { Text = "Coffee beans" }, new() { Text = "Oat milk" }, new() { Text = "Lemons" }, new() { Text = "Sourdough" }]), PlainText = "Coffee beans\nOat milk\nLemons\nSourdough", Color = "default", NotebookId = personal.Id, SortOrder = 2 },
                new Note { Type = NoteType.Code, Title = "Useful snippet", Language = "PowerShell", Content = "# Copy Satchel to a USB drive (E:)\nrobocopy \".\\Satchel\" \"E:\\Satchel\" /MIR", PlainText = "robocopy", Color = "slate", NotebookId = work.Id, SortOrder = 3 },
                new Note { Type = NoteType.Journal, Title = "", JournalDate = today, Mood = 4, Content = "", PlainText = "", Color = "default", NotebookId = personal.Id, SortOrder = 4 }
            );

            var home = new TaskList { Name = "Personal", Color = "teal", Icon = Glyphs.Home, SortOrder = 0 };
            var job = new TaskList { Name = "Work", Color = "sky", Icon = Glyphs.Work, SortOrder = 1 };
            db.TaskLists.AddRange(home, job);
            db.SaveChanges();

            db.Tasks.AddRange(
                new TaskItem { ListId = home.Id, Title = "Back up the Satchel folder to a second drive", Priority = TaskPriority.High, DueDate = today.AddDays(2), SortOrder = 0 },
                new TaskItem { ListId = home.Id, Title = "Plan the weekend trip", State = TaskState.InProgress, Priority = TaskPriority.Medium, SortOrder = 1, Subtasks = TaskItem.SerializeSubtasks([new() { Text = "Book a place", IsDone = true }, new() { Text = "Check the weather" }, new() { Text = "Pack snacks" }]) },
                new TaskItem { ListId = home.Id, Title = "Renew passport", DueDate = today.AddDays(14), Priority = TaskPriority.Low, SortOrder = 2 },
                new TaskItem { ListId = job.Id, Title = "Prepare the quarterly report", DueDate = today.AddDays(5), Priority = TaskPriority.Urgent, SortOrder = 3 },
                new TaskItem { ListId = job.Id, Title = "Install Satchel", State = TaskState.Done, CompletedAt = DateTime.Now, SortOrder = 4 }
            );

            var cPersonal = new CalendarCategory { Name = "Personal", Color = "teal", SortOrder = 0 };
            var cWork = new CalendarCategory { Name = "Work", Color = "sky", SortOrder = 1 };
            var cFamily = new CalendarCategory { Name = "Family", Color = "amber", SortOrder = 2 };
            db.CalendarCategories.AddRange(cPersonal, cWork, cFamily);
            db.SaveChanges();

            var friday = today.AddDays(((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7);
            db.Events.AddRange(
                new CalendarEvent { CategoryId = cPersonal.Id, Title = "Weekly review", Start = friday.AddHours(17), End = friday.AddHours(17.5), Recurrence = RecurrenceFrequency.Weekly, RecurrenceDays = 1 << (int)DayOfWeek.Friday, ReminderMinutes = 10 },
                new CalendarEvent { CategoryId = cWork.Id, Title = "Team sync", Location = "Room 2 / online", Start = today.AddDays(1).AddHours(10), End = today.AddDays(1).AddHours(10.5), ReminderMinutes = 10 },
                new CalendarEvent { CategoryId = cPersonal.Id, Title = "Dentist", Start = today.AddDays(5).AddHours(15), End = today.AddDays(5).AddHours(16), ReminderMinutes = 60 },
                new CalendarEvent { CategoryId = cFamily.Id, Title = "Family dinner", Start = today.AddDays(3).AddHours(19), End = today.AddDays(3).AddHours(21.5) },
                new CalendarEvent { CategoryId = cWork.Id, Title = "Conference", Start = today.AddDays(9), End = today.AddDays(10), IsAllDay = true }
            );

            var board = new Board { Name = "Ideas", SortOrder = 0 };
            db.Boards.Add(board);
            db.SaveChanges();
            var s1 = new StickyNote { BoardId = board.Id, Text = "Drag me around ✋", Color = "yellow", X = 60, Y = 50, Rotation = -2, ZIndex = 1 };
            var s2 = new StickyNote { BoardId = board.Id, Text = "Double-click empty space to add a note", Color = "teal", X = 320, Y = 90, Rotation = 1.5, ZIndex = 2 };
            var s3 = new StickyNote { BoardId = board.Id, Text = "Resize from the corner ↘", Color = "pink", X = 580, Y = 40, Rotation = -1, ZIndex = 3 };
            var s4 = new StickyNote { BoardId = board.Id, Text = "“Tidy up” lines everything up neatly", Color = "green", X = 200, Y = 330, Rotation = 2, ZIndex = 4 };
            var s5 = new StickyNote { BoardId = board.Id, Text = "Drag the dot on a note's edge onto another note to connect them.\nTry “Corkboard” for pins and swinging strings!",
                Color = "orange", X = 520, Y = 320, Width = 250, Height = 210, Rotation = -1.5, ZIndex = 5 };
            db.Stickies.AddRange(s1, s2, s3, s4, s5);
            db.SaveChanges();
            db.BoardLinks.AddRange(
                new BoardLink { BoardId = board.Id, FromId = s1.Id, ToId = s2.Id, Arrow = true },
                new BoardLink { BoardId = board.Id, FromId = s4.Id, ToId = s5.Id });
            db.LibraryDocs.Add(new LibraryDoc { Title = "Welcome to your Library", Markdown = ViewModels.SampleDocs.Guide, Collection = "Guides", FileName = "markdown-guide.md" });
            db.SaveChanges();
        });
    }

    private static (string, string) BuildWelcome()
    {
        var doc = new FlowDocument();
        void P(string text, double size = 0, bool bold = false)
        {
            var p = new Paragraph(new Run(text));
            if (size > 0) p.FontSize = size;
            if (bold) p.FontWeight = FontWeights.SemiBold;
            doc.Blocks.Add(p);
        }
        P("Everything you keep here — notes, tasks, events, passwords — lives in one encrypted file next to the app, so the whole folder can travel on a flash drive.");
        P("A few tips", 19, true);
        var list = new List { MarkerStyle = TextMarkerStyle.Disc };
        foreach (var tip in new[]
        {
            "Ctrl+K opens the command palette: jump anywhere, search everything.",
            "Drag cards on the Dashboard, Notes and Tasks boards to reorder them.",
            "The Vault keeps profiles (services) and their accounts, with one-time codes and a password generator.",
            "Lock any time with Ctrl+L. It also locks itself after a few idle minutes.",
            "Settings → Appearance has themes, accent colors, fonts and more."
        })
            list.ListItems.Add(new ListItem(new Paragraph(new Run(tip))));
        doc.Blocks.Add(list);
        P("Your data never leaves this drive. Keep your recovery key somewhere safe.");
        return RichTextEditor.SerializeDocument(doc);
    }
}
