using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Models;

/// <summary>A workbook in the Sheets tab. The sheets, cells and formulas are JSON in <see cref="Data"/> (encrypted).</summary>
public partial class Spreadsheet : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _data = "";
    /// <summary>The file it came from, if it was imported.</summary>
    [ObservableProperty][property: Encrypted] private string _fileName = "";
    [ObservableProperty] private int _sheetCount = 1;
    [ObservableProperty] private int _cellCount;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private DateTime? _lastOpenedAt;

    [NotMapped]
    public string Summary
    {
        get
        {
            var sheets = SheetCount == 1 ? "1 sheet" : $"{SheetCount} sheets";
            var cells = CellCount == 1 ? "1 cell" : $"{CellCount:N0} cells";
            return $"{sheets} · {cells} · edited {Format.Relative(UpdatedAt)}";
        }
    }

    partial void OnSheetCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnCellCountChanged(int value) => OnPropertyChanged(nameof(Summary));

    /// <summary>After a save: "edited just now".</summary>
    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));
}
