using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>A saved web link with its fetched title, description, icon and preview image.</summary>
public partial class Bookmark : Entity
{
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    /// <summary>Folder path, "/" separated ("Work/Tools"). Empty = no folder.</summary>
    [ObservableProperty][property: Encrypted] private string _folder = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private byte[]? _icon;
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isReadLater;
    [ObservableProperty] private int _visits;
    [ObservableProperty] private DateTime? _lastVisitedAt;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private string _color = "slate";

    [NotMapped] public string Domain => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : Url;
    [NotMapped] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Domain : Title;
    [NotMapped] public IReadOnlyList<string> TagList => Note.SplitTags(Tags);
    [NotMapped] public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    [NotMapped] public string Initial => DisplayTitle.Length > 0 ? char.ToUpperInvariant(DisplayTitle[0]).ToString() : "?";

    partial void OnUrlChanged(string value)
    {
        OnPropertyChanged(nameof(Domain));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Initial));
    }

    partial void OnTagsChanged(string value) => OnPropertyChanged(nameof(TagList));
    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(HasDescription));
}
