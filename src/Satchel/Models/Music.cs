using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>A list of YouTube / YouTube Music tracks played in the background music player.</summary>
public partial class MusicPlaylist : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    /// <summary>The YouTube playlist link it was imported from (empty for hand-made lists).</summary>
    [ObservableProperty][property: Encrypted] private string _sourceUrl = "";
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private int _trackCount;
}

public partial class MusicTrack : Entity
{
    [ObservableProperty] private int _playlistId;
    [ObservableProperty][property: Encrypted] private string _youtubeId = "";
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _artist = "";
    [ObservableProperty][property: Encrypted] private byte[]? _thumbnail;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private int _playCount;
    [ObservableProperty] private DateTime? _lastPlayedAt;
    /// <summary>The video's owner doesn't allow playback outside YouTube; the player skips it.</summary>
    [ObservableProperty] private bool _cannotEmbed;

    [ObservableProperty][property: NotMapped] private bool _isCurrent;

    [NotMapped] public string DurationText => Duration > 0 ? Core.YouTube.FormatTime(Duration) : "";
    [NotMapped] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "YouTube track" : Title;

    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
}
