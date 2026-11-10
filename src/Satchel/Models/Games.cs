using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum GameStatus { Playing = 0, Backlog = 1, Wishlist = 2, Completed = 3, Dropped = 4 }

public partial class Game : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _platform = "";
    [ObservableProperty][property: Encrypted] private string _genre = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private string _storeUrl = "";
    [ObservableProperty][property: Encrypted] private byte[]? _cover;
    [ObservableProperty] private GameStatus _status = GameStatus.Backlog;
    [ObservableProperty] private double _hours;
    [ObservableProperty] private int _rating;
    [ObservableProperty] private int _completion;
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private DateTime? _finishedAt;
    [ObservableProperty] private DateTime? _lastPlayedAt;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private int _steamAppId;
    [ObservableProperty] private string _color = "sky";

    [NotMapped] public string HoursText => Hours <= 0 ? "Not played" : Hours < 1 ? $"{Math.Round(Hours * 60)} min" : $"{Hours:0.#} h";
    [NotMapped] public string StatusText => Status switch
    {
        GameStatus.Playing => "Playing",
        GameStatus.Backlog => "Backlog",
        GameStatus.Wishlist => "Wishlist",
        GameStatus.Completed => "Completed",
        _ => "Dropped"
    };

    partial void OnHoursChanged(double value) => OnPropertyChanged(nameof(HoursText));
    partial void OnStatusChanged(GameStatus value) => OnPropertyChanged(nameof(StatusText));
}

public partial class PlaySession : Entity
{
    [ObservableProperty] private int _gameId;
    [ObservableProperty] private DateTime _date = DateTime.Now;
    [ObservableProperty] private int _minutes;
    [ObservableProperty][property: Encrypted] private string _note = "";

    [NotMapped] public string DurationText => Minutes < 60 ? $"{Minutes} min" : $"{Minutes / 60} h {Minutes % 60:00} min";
}
