using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum FeedKind { Articles = 0, Podcast = 1 }

/// <summary>An RSS / Atom feed or a podcast, checked in the background.</summary>
public partial class Feed : Entity
{
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _siteUrl = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    [ObservableProperty][property: Encrypted] private string _lastError = "";
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty] private FeedKind _kind;
    [ObservableProperty] private DateTime? _lastCheckedAt;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private bool _notify = true;
    [ObservableProperty] private string _color = "orange";

    [ObservableProperty][property: NotMapped] private int _unreadCount;
    [ObservableProperty][property: NotMapped] private bool _isChecking;

    [NotMapped] public bool IsPodcast => Kind == FeedKind.Podcast;
    partial void OnKindChanged(FeedKind value) => OnPropertyChanged(nameof(IsPodcast));
}

public partial class FeedItem : Entity
{
    [ObservableProperty] private int _feedId;
    [ObservableProperty][property: Encrypted] private string _guid = "";
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _link = "";
    /// <summary>Plain-text summary for the list.</summary>
    [ObservableProperty][property: Encrypted] private string _summary = "";
    /// <summary>Full article body (HTML from the feed), if it has one.</summary>
    [ObservableProperty][property: Encrypted] private string _content = "";
    [ObservableProperty][property: Encrypted] private string _author = "";
    [ObservableProperty][property: Encrypted] private string _enclosureUrl = "";
    [ObservableProperty] private string _enclosureType = "";
    [ObservableProperty] private DateTime? _publishedAt;
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isStarred;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _position;
    [ObservableProperty] private bool _isPlayed;

    [ObservableProperty][property: NotMapped] private string _feedTitle = "";
    [ObservableProperty][property: NotMapped] private string _feedColor = "orange";

    [NotMapped] public bool IsAudio => EnclosureUrl.Length > 0 && (EnclosureType.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ||
                                                                   EnclosureUrl.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                                                   EnclosureUrl.Contains(".m4a", StringComparison.OrdinalIgnoreCase));
    [NotMapped] public bool IsUnread => !IsRead;
    [NotMapped] public string DurationText => Duration > 0 ? Core.YouTube.FormatTime(Duration) : "";
    [NotMapped] public double ListenProgress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;

    partial void OnIsReadChanged(bool value) => OnPropertyChanged(nameof(IsUnread));
    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ListenProgress));
    }
    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(ListenProgress));
    partial void OnEnclosureUrlChanged(string value) => OnPropertyChanged(nameof(IsAudio));
}
