using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>A followed YouTube channel (checked through its public RSS feed — no account or API key).</summary>
public partial class VideoChannel : Entity
{
    [ObservableProperty][property: Encrypted] private string _youtubeId = "";
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _handle = "";
    [ObservableProperty][property: Encrypted] private byte[]? _avatar;
    [ObservableProperty][property: Encrypted] private string _lastError = "";
    [ObservableProperty] private DateTime? _lastCheckedAt;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private bool _notify = true;
    [ObservableProperty] private string _color = "red";

    [ObservableProperty][property: NotMapped] private int _newCount;
    [ObservableProperty][property: NotMapped] private bool _isChecking;
}

public enum VideoState { New = 0, Seen = 1, Watched = 2 }

public partial class Video : Entity
{
    [ObservableProperty][property: Encrypted] private string _youtubeId = "";
    [ObservableProperty] private int? _channelId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _author = "";
    [ObservableProperty][property: Encrypted] private byte[]? _thumbnail;
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty] private DateTime? _publishedAt;
    [ObservableProperty] private VideoState _state;
    [ObservableProperty] private bool _isSaved;
    [ObservableProperty] private bool _isWatchLater;
    [ObservableProperty] private bool _isShort;
    [ObservableProperty] private int? _boardId;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private long _views;
    [ObservableProperty] private DateTime? _watchedAt;

    [NotMapped] public bool IsUnseen => State == VideoState.New;
    [NotMapped] public bool IsWatched => State == VideoState.Watched;
    [NotMapped] public double WatchProgress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    [NotMapped] public string Url => Core.YouTube.WatchUrl(YoutubeId);

    partial void OnStateChanged(VideoState value)
    {
        OnPropertyChanged(nameof(IsUnseen));
        OnPropertyChanged(nameof(IsWatched));
    }

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(WatchProgress));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(WatchProgress));
}

/// <summary>A timestamped note on a video.</summary>
public partial class VideoNote : Entity
{
    [ObservableProperty] private int _videoId;
    [ObservableProperty] private double _seconds;
    [ObservableProperty][property: Encrypted] private string _text = "";

    [NotMapped] public string TimeText => Core.YouTube.FormatTime(Seconds);
    partial void OnSecondsChanged(double value) => OnPropertyChanged(nameof(TimeText));
}
