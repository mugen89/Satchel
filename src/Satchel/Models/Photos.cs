using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public partial class Album : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty] private string _color = "sky";
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private int? _coverPhotoId;

    [ObservableProperty][property: NotMapped] private int _photoCount;
    [ObservableProperty][property: NotMapped] private byte[]? _coverThumb;
}

/// <summary>A photo or screenshot. <see cref="Data"/> is the full image (loaded only when opened), <see cref="Thumb"/> a small preview.</summary>
public partial class Photo : Entity
{
    [ObservableProperty] private int? _albumId;
    [ObservableProperty][property: Encrypted] private string _caption = "";
    [ObservableProperty][property: Encrypted] private string _fileName = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private byte[]? _data;
    [ObservableProperty][property: Encrypted] private byte[]? _thumb;
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _takenAt = DateTime.Now;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isScreenshot;
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private bool _isChecked;

    [NotMapped] public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 1.5;
    [NotMapped] public string SizeText => $"{Width} × {Height} · {Core.Format.Bytes(Size)}";
    [NotMapped] public string DisplayName => string.IsNullOrWhiteSpace(Caption) ? (string.IsNullOrWhiteSpace(FileName) ? "Photo" : FileName) : Caption;

    partial void OnCaptionChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
