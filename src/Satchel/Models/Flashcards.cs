using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public partial class Deck : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private int _newPerDay = 20;

    [ObservableProperty][property: NotMapped] private int _total;
    [ObservableProperty][property: NotMapped] private int _due;
    [ObservableProperty][property: NotMapped] private int _new;
}

public enum CardState { New = 0, Learning = 1, Review = 2, Relearning = 3 }

/// <summary>A flashcard scheduled with spaced repetition (SM-2 style ease and intervals, Anki-like grades).</summary>
public partial class Flashcard : Entity
{
    [ObservableProperty] private int _deckId;
    [ObservableProperty][property: Encrypted] private string _front = "";
    [ObservableProperty][property: Encrypted] private string _back = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty] private CardState _state;
    [ObservableProperty] private double _ease = 2.5;
    [ObservableProperty] private double _intervalDays;
    [ObservableProperty] private int _reps;
    [ObservableProperty] private int _lapses;
    /// <summary>Learning step index (0 = first step).</summary>
    [ObservableProperty] private int _step;
    [ObservableProperty] private DateTime? _dueAt;
    [ObservableProperty] private DateTime? _lastReviewedAt;
    [ObservableProperty] private bool _isSuspended;
    [ObservableProperty] private double _sortOrder;

    [NotMapped] public bool IsNewCard => State == CardState.New;
    [NotMapped] public bool IsDue => !IsSuspended && State != CardState.New && (DueAt ?? DateTime.MinValue) <= DateTime.Now;

    [NotMapped] public string DueText
    {
        get
        {
            if (IsSuspended) return "Suspended";
            if (State == CardState.New) return "New";
            if (DueAt is not { } due) return "Due";
            if (due <= DateTime.Now) return "Due now";
            var days = (due.Date - DateTime.Today).Days;
            return days switch { 0 => $"Today {Core.Format.Time(due)}", 1 => "Tomorrow", < 30 => $"In {days} days", < 365 => $"In {days / 30} mo", _ => $"In {days / 365.0:0.#} y" };
        }
    }

    partial void OnStateChanged(CardState value) => RaiseDue();
    partial void OnDueAtChanged(DateTime? value) => RaiseDue();
    partial void OnIsSuspendedChanged(bool value) => RaiseDue();

    private void RaiseDue()
    {
        OnPropertyChanged(nameof(IsNewCard));
        OnPropertyChanged(nameof(IsDue));
        OnPropertyChanged(nameof(DueText));
    }
}

/// <summary>One answer during a study session (for stats and the auto-diary).</summary>
public partial class ReviewLog : Entity
{
    [ObservableProperty] private int _cardId;
    [ObservableProperty] private int _deckId;
    [ObservableProperty] private DateTime _reviewedAt = DateTime.Now;
    /// <summary>1 = again, 2 = hard, 3 = good, 4 = easy.</summary>
    [ObservableProperty] private int _grade;
    [ObservableProperty] private double _intervalDays;
    /// <summary>The card was new before this answer (counts toward the daily new-card limit).</summary>
    [ObservableProperty] private bool _wasNew;
}
