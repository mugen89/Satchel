using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>A freehand drawing (ink strokes in ISF format) with an optional reference image underneath.</summary>
public partial class Sketch : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private byte[]? _strokes;
    [ObservableProperty][property: Encrypted] private byte[]? _thumbnail;
    [ObservableProperty][property: Encrypted] private byte[]? _reference;
    /// <summary>plain, grid, dots, lines or dark.</summary>
    [ObservableProperty] private string _paper = "plain";
    [ObservableProperty] private double _canvasWidth = 1600;
    [ObservableProperty] private double _canvasHeight = 1000;
    [ObservableProperty] private double _referenceOpacity = 0.45;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private bool _isFavorite;
}
