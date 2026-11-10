using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum BookStatus { WantToRead = 0, Reading = 1, Finished = 2, Abandoned = 3 }
public enum BookFormat { Paper = 0, Ebook = 1, Audiobook = 2, Comic = 3, Article = 4 }

public partial class Book : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _author = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private string _isbn = "";
    [ObservableProperty][property: Encrypted] private string _publisher = "";
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private byte[]? _cover;
    [ObservableProperty] private BookStatus _status;
    [ObservableProperty] private BookFormat _format;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _rating;
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private DateTime? _finishedAt;
    [ObservableProperty] private int _year;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _color = "teal";

    [NotMapped] public double Progress => Status == BookStatus.Finished ? 1 : TotalPages > 0 ? Math.Clamp((double)CurrentPage / TotalPages, 0, 1) : 0;
    [NotMapped] public string ProgressText => Status == BookStatus.Finished ? "Finished" :
        TotalPages > 0 ? $"{CurrentPage} / {TotalPages} · {Progress:P0}" : CurrentPage > 0 ? $"Page {CurrentPage}" : "Not started";
    [NotMapped] public int PagesLeft => Math.Max(0, TotalPages - CurrentPage);
    [NotMapped] public string UnitName => Format == BookFormat.Audiobook ? "min" : "pages";

    partial void OnCurrentPageChanged(int value) => RaiseProgress();
    partial void OnTotalPagesChanged(int value) => RaiseProgress();
    partial void OnStatusChanged(BookStatus value) => RaiseProgress();
    partial void OnFormatChanged(BookFormat value) => OnPropertyChanged(nameof(UnitName));

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(PagesLeft));
    }
}

public partial class ReadingSession : Entity
{
    [ObservableProperty] private int _bookId;
    [ObservableProperty] private DateTime _date = DateTime.Now;
    [ObservableProperty] private int _pages;
    [ObservableProperty] private int _minutes;
    [ObservableProperty] private int _toPage;
}

public partial class BookQuote : Entity
{
    [ObservableProperty] private int _bookId;
    [ObservableProperty][property: Encrypted] private string _text = "";
    [ObservableProperty][property: Encrypted] private string _note = "";
    [ObservableProperty] private int _page;
    [ObservableProperty] private bool _isFavorite;
}
