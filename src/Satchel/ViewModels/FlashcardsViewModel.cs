using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Flashcards: decks of question/answer cards studied with spaced repetition.</summary>
public sealed partial class FlashcardsViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Flashcard> _dirty = [];
    private List<Flashcard> _cards = [];
    private readonly List<Flashcard> _queue = [];
    private DateTime _sessionStart;
    private int _sessionAnswers, _sessionCorrect;
    private int _newStudiedToday;

    public FlashcardsViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Flashcards";
    public ObservableCollection<Deck> Decks { get; } = [];
    public ObservableCollection<Flashcard> Cards { get; } = [];

    [ObservableProperty] private Deck? _selectedDeck;
    [ObservableProperty] private Flashcard? _editing;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newFront = "";
    [ObservableProperty] private string _newBack = "";

    // study session
    [ObservableProperty] private bool _isStudying;
    [ObservableProperty] private Flashcard? _current;
    [ObservableProperty] private bool _isFlipped;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private int _remaining;
    [ObservableProperty] private int _sessionTotal;
    [ObservableProperty] private string _againText = "";
    [ObservableProperty] private string _hardText = "";
    [ObservableProperty] private string _goodText = "";
    [ObservableProperty] private string _easyText = "";
    [ObservableProperty] private string _summary = "";

    public bool HasDeck => SelectedDeck != null;
    public bool HasDecks => Decks.Count > 0;
    public bool HasCards => Cards.Count > 0;
    public bool IsEditing => Editing != null;
    public int DueToday => Decks.Sum(d => d.Due);
    public double SessionProgress => SessionTotal == 0 ? 0 : 1 - (double)Remaining / Math.Max(1, SessionTotal);
    public string StudyButtonText => SelectedDeck is { } d ? (d.Due + Math.Min(d.New, NewAllowance(d)) is var n && n > 0 ? $"Study {n} card{(n == 1 ? "" : "s")}" : "All done for now") : "";
    public bool CanStudy => SelectedDeck is { } d && d.Due + Math.Min(d.New, NewAllowance(d)) > 0;

    partial void OnSelectedDeckChanged(Deck? value)
    {
        Editing = null;
        ApplyCards();
        OnPropertyChanged(nameof(HasDeck));
        OnPropertyChanged(nameof(StudyButtonText));
        OnPropertyChanged(nameof(CanStudy));
    }

    partial void OnSearchChanged(string value) => ApplyCards();
    partial void OnEditingChanged(Flashcard? oldValue, Flashcard? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnCardChanged;
        if (newValue != null) newValue.PropertyChanged += OnCardChanged;
        OnPropertyChanged(nameof(IsEditing));
    }

    partial void OnRemainingChanged(int value) => OnPropertyChanged(nameof(SessionProgress));

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is Flashcard c && e.PropertyName is nameof(Flashcard.Front) or nameof(Flashcard.Back) or nameof(Flashcard.Tags) or nameof(Flashcard.IsSuspended))
        {
            _dirty.Add(c);
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var c in batch) c.UpdatedAt = DateTime.Now;
        await _store.UpdateRangeAsync(batch);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadAsync();
        if (parameter is "study" && SelectedDeck != null) StartStudy();
        else if (parameter is ItemRef r && Decks.FirstOrDefault(d => d.Id == r.ParentId) is { } deck)
        {
            SelectedDeck = deck;
            if (r.Id != 0 && _cards.FirstOrDefault(c => c.Id == r.Id) is { } card) Editing = card;
        }
    }

    public override void OnNewShortcut() => FocusNewCard?.Invoke();
    public event Action? FocusNewCard;

    private async Task LoadAsync()
    {
        var keep = SelectedDeck?.Id;
        var today = DateTime.Today;
        var (decks, cards, newToday) = await _store.RunAsync(db => (db.Decks.OrderBy(d => d.SortOrder).ToList(), db.Flashcards.ToList(),
            db.ReviewLogs.Where(r => r.ReviewedAt >= today && r.WasNew).Select(r => r.CardId).Distinct().Count()));
        _cards = cards;
        _newStudiedToday = newToday;
        Decks.Clear();
        foreach (var d in decks) Decks.Add(d);
        RefreshCounts();
        SelectedDeck = Decks.FirstOrDefault(d => d.Id == keep) ?? Decks.FirstOrDefault();
        OnPropertyChanged(nameof(HasDecks));
    }

    private int NewAllowance(Deck d) => Math.Max(0, d.NewPerDay - _newStudiedToday);

    private void RefreshCounts()
    {
        var now = DateTime.Now;
        foreach (var d in Decks)
        {
            var cards = _cards.Where(c => c.DeckId == d.Id && !c.IsSuspended).ToList();
            d.Total = cards.Count;
            d.New = cards.Count(c => c.State == CardState.New);
            d.Due = cards.Count(c => c.State != CardState.New && (c.DueAt ?? DateTime.MinValue) <= now);
        }
        OnPropertyChanged(nameof(DueToday));
        OnPropertyChanged(nameof(StudyButtonText));
        OnPropertyChanged(nameof(CanStudy));
    }

    private void ApplyCards()
    {
        Cards.Clear();
        if (SelectedDeck == null) { OnPropertyChanged(nameof(HasCards)); return; }
        var q = Search.Trim();
        foreach (var c in _cards.Where(c => c.DeckId == SelectedDeck.Id && (q.Length == 0 || c.Front.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Back.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                     .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt))
            Cards.Add(c);
        OnPropertyChanged(nameof(HasCards));
    }

    // ----- decks -----

    [RelayCommand] private void SelectDeck(Deck? d) { if (d != null) SelectedDeck = d; }

    [RelayCommand]
    private async Task NewDeck()
    {
        var name = await _dialogs.PromptAsync("New deck", "A deck is a set of cards on one topic — a language, a course, an exam, things you want to remember.", "", "e.g. Spanish verbs", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var deck = new Deck { Name = name.Trim(), SortOrder = Decks.Count, Color = Palettes.Tags[Decks.Count % 10].Key };
        await _store.AddAsync(deck);
        Decks.Add(deck);
        SelectedDeck = deck;
        OnPropertyChanged(nameof(HasDecks));
        FocusNewCard?.Invoke();
    }

    [RelayCommand]
    private async Task RenameDeck()
    {
        if (SelectedDeck is not { } d) return;
        var name = await _dialogs.PromptAsync("Rename deck", null, d.Name, "Name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        d.Name = name.Trim();
        await _store.UpdateAsync(d);
    }

    [RelayCommand]
    private async Task SetNewPerDay()
    {
        if (SelectedDeck is not { } d) return;
        var text = await _dialogs.PromptAsync("New cards per day", "How many new cards to introduce each day (all decks share today's count).", d.NewPerDay.ToString(), "20", "Save");
        if (!int.TryParse(text, out var n) || n < 0) return;
        d.NewPerDay = Math.Min(500, n);
        await _store.UpdateAsync(d);
        RefreshCounts();
    }

    [RelayCommand]
    private async Task DeleteDeck()
    {
        if (SelectedDeck is not { } d) return;
        if (!await _dialogs.ConfirmAsync("Delete deck?", $"“{d.Name}” and its {d.Total} card(s) will be deleted, with their study history.", "Delete", danger: true)) return;
        await _store.DeleteAsync(d);
        Decks.Remove(d);
        _cards.RemoveAll(c => c.DeckId == d.Id);
        SelectedDeck = Decks.FirstOrDefault();
        OnPropertyChanged(nameof(HasDecks));
    }

    // ----- cards -----

    [RelayCommand]
    private async Task AddCard()
    {
        if (SelectedDeck is not { } d) { _toasts.Info("Create a deck first"); return; }
        var front = NewFront.Trim();
        var back = NewBack.Trim();
        if (front.Length == 0 || back.Length == 0) { _toasts.Info("Fill in both sides"); return; }
        var card = new Flashcard { DeckId = d.Id, Front = front, Back = back, SortOrder = _cards.Count(c => c.DeckId == d.Id) };
        await _store.AddAsync(card);
        _cards.Add(card);
        Cards.Add(card);
        NewFront = "";
        NewBack = "";
        RefreshCounts();
        OnPropertyChanged(nameof(HasCards));
        FocusNewCard?.Invoke();
    }

    [RelayCommand] private void EditCard(Flashcard? c) => Editing = ReferenceEquals(Editing, c) ? null : c;
    [RelayCommand] private void CloseEditor() => Editing = null;

    [RelayCommand]
    private async Task DeleteCard(Flashcard? c)
    {
        c ??= Editing;
        if (c == null) return;
        _dirty.Remove(c);
        await _store.DeleteAsync(c);
        _cards.Remove(c);
        Cards.Remove(c);
        if (ReferenceEquals(Editing, c)) Editing = null;
        RefreshCounts();
        OnPropertyChanged(nameof(HasCards));
    }

    [RelayCommand]
    private async Task ResetCard(Flashcard? c)
    {
        c ??= Editing;
        if (c == null) return;
        c.State = CardState.New;
        c.IntervalDays = 0;
        c.Ease = 2.5;
        c.Step = 0;
        c.DueAt = null;
        await _store.UpdateAsync(c);
        RefreshCounts();
    }

    [RelayCommand]
    private async Task ToggleSuspend(Flashcard? c)
    {
        c ??= Editing;
        if (c == null) return;
        c.IsSuspended = !c.IsSuspended;
        await _store.UpdateAsync(c);
        RefreshCounts();
    }

    // ----- import / export -----

    /// <summary>Paste lines "front ⇥ back" (tab, semicolon, " - " or " :: ") — Anki and spreadsheet exports work as they are.</summary>
    [RelayCommand]
    private async Task ImportText()
    {
        if (SelectedDeck == null) { _toasts.Info("Create a deck first"); return; }
        var text = await _dialogs.ShowAsync(new Dialogs.TextAreaDialogViewModel("Import cards",
            "One card per line: front and back separated by a tab, “;”, “ :: ” or “ - ”. Copy two columns from a spreadsheet, or paste an Anki text export.",
            "hola :: hello\ngracias :: thank you", "Import"));
        if (text is string s) await ImportLinesAsync(s.Split('\n'));
    }

    [RelayCommand]
    private async Task ImportFile()
    {
        if (SelectedDeck == null) { _toasts.Info("Create a deck first"); return; }
        var dialog = new OpenFileDialog { Title = "Import cards", Filter = "Text or CSV (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        await ImportLinesAsync(await File.ReadAllLinesAsync(dialog.FileName));
    }

    private async Task ImportLinesAsync(IEnumerable<string> lines)
    {
        if (SelectedDeck is not { } d) return;
        var cards = new List<Flashcard>();
        int order = _cards.Count(c => c.DeckId == d.Id);
        foreach (var raw in lines)
        {
            var line = raw.Trim('\r', ' ');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[]? parts = null;
            foreach (var sep in new[] { "\t", " :: ", ";", " - ", "," })
            {
                var p = line.Split(sep, 2);
                if (p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0) { parts = p; break; }
            }
            if (parts == null) continue;
            cards.Add(new Flashcard { DeckId = d.Id, Front = Unquote(parts[0]), Back = Unquote(parts[1]), SortOrder = order++ });
        }
        if (cards.Count == 0) { _toasts.Info("No cards found — use one card per line, front and back separated by a tab or “;”"); return; }
        await _store.AddRangeAsync(cards);
        _cards.AddRange(cards);
        ApplyCards();
        RefreshCounts();
        _toasts.Success($"Imported {cards.Count} cards into {d.Name}");
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Replace("\"\"", "\"");
        return s.Replace("<br>", "\n").Replace("<br/>", "\n");
    }

    [RelayCommand]
    private async Task Export()
    {
        if (SelectedDeck is not { } d) return;
        var dialog = new SaveFileDialog { Title = "Export cards", Filter = "Tab-separated text (*.txt)|*.txt", FileName = d.Name + ".txt" };
        if (dialog.ShowDialog() != true) return;
        var sb = new StringBuilder();
        foreach (var c in _cards.Where(c => c.DeckId == d.Id))
            sb.Append(c.Front.Replace("\t", " ").Replace("\n", "<br>")).Append('\t').AppendLine(c.Back.Replace("\t", " ").Replace("\n", "<br>"));
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), Encoding.UTF8);
        _toasts.Success("Exported (not encrypted) — Anki can import this file");
    }

    // ----- studying -----

    [RelayCommand]
    private void StartStudy()
    {
        if (SelectedDeck is not { } d) return;
        var now = DateTime.Now;
        var deckCards = _cards.Where(c => c.DeckId == d.Id && !c.IsSuspended).ToList();
        _queue.Clear();
        _queue.AddRange(deckCards.Where(c => c.State != CardState.New && (c.DueAt ?? DateTime.MinValue) <= now).OrderBy(c => c.DueAt));
        var fresh = deckCards.Where(c => c.State == CardState.New).OrderBy(c => c.SortOrder).Take(NewAllowance(d)).ToList();
        // Mix new cards in among the reviews.
        for (int i = 0; i < fresh.Count; i++) _queue.Insert(Math.Min(_queue.Count, i * 3 + 1), fresh[i]);
        if (_queue.Count == 0) { _toasts.Info("Nothing due — come back later, or add new cards"); return; }
        SessionTotal = _queue.Count;
        _sessionStart = DateTime.Now;
        _sessionAnswers = _sessionCorrect = 0;
        IsFinished = false;
        IsStudying = true;
        NextCard();
    }

    private void NextCard()
    {
        var now = DateTime.Now;
        // Learning cards come back in the same session once their few minutes are up (or when nothing else is left).
        var card = _queue.FirstOrDefault(c => c.State is CardState.New or CardState.Review || (c.DueAt ?? DateTime.MinValue) <= now)
                   ?? _queue.OrderBy(c => c.DueAt).FirstOrDefault();
        Remaining = _queue.Count;
        if (card == null)
        {
            Finish();
            return;
        }
        Current = card;
        IsFlipped = false;
        AgainText = SpacedRepetition.Preview(card, 1);
        HardText = SpacedRepetition.Preview(card, 2);
        GoodText = SpacedRepetition.Preview(card, 3);
        EasyText = SpacedRepetition.Preview(card, 4);
    }

    [RelayCommand]
    private void Flip()
    {
        if (Current != null && !IsFinished) IsFlipped = !IsFlipped;
    }

    [RelayCommand]
    private async Task Answer(string? gradeText)
    {
        if (Current is not { } card || !IsFlipped || !int.TryParse(gradeText, out var grade)) return;
        bool wasNew = card.State == CardState.New;
        var due = SpacedRepetition.Apply(card, grade, DateTime.Now);
        _sessionAnswers++;
        if (grade >= 3) _sessionCorrect++;
        if (wasNew) _newStudiedToday++;
        _queue.Remove(card);
        // Still learning (due within the next 20 minutes): keep it in this session.
        if (due <= DateTime.Now.AddMinutes(20)) _queue.Add(card);
        await _store.RunAsync(db =>
        {
            db.Update(card);
            db.Add(new ReviewLog { CardId = card.Id, DeckId = card.DeckId, ReviewedAt = DateTime.Now, Grade = grade, IntervalDays = card.IntervalDays, WasNew = wasNew });
            db.SaveChanges();
        });
        NextCard();
    }

    private void Finish()
    {
        IsFinished = true;
        Current = null;
        var minutes = Math.Max(1, (int)Math.Round((DateTime.Now - _sessionStart).TotalMinutes));
        Summary = _sessionAnswers == 0 ? "Nothing to review right now."
            : $"{_sessionAnswers} answers in {minutes} min · {Math.Round(100.0 * _sessionCorrect / _sessionAnswers)}% right the first time round.";
        RefreshCounts();
    }

    [RelayCommand]
    private void EndStudy()
    {
        IsStudying = false;
        IsFinished = false;
        Current = null;
        _queue.Clear();
        RefreshCounts();
        ApplyCards();
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        if (Editing != null) Editing.PropertyChanged -= OnCardChanged;
    }
}
