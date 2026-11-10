using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Satchel.Controls;
using Satchel.Services.Sheets;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class SheetsView : UserControl
{
    private SheetsViewModel? _vm;
    private SheetDocument? _doc;

    public SheetsView()
    {
        InitializeComponent();
        _ = new FileDropTarget(this, DropZone, SpreadsheetIO.IsSpreadsheet, files => _vm?.ImportFilesAsync(files) ?? Task.CompletedTask);
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.FocusGridRequested -= FocusGrid;
                _vm.PropertyChanged -= OnVmChanged;
            }
            _vm = e.NewValue as SheetsViewModel;
            if (_vm != null)
            {
                _vm.FocusGridRequested += FocusGrid;
                _vm.PropertyChanged += OnVmChanged;
            }
            HookDocument();
        };
        // The formula bar shows what you type into a cell, as you type it.
        Grid.EditTextChanged += text =>
        {
            if (_vm == null) return;
            _vm.FormulaBarEditing = false;
            _vm.FormulaText = text;
        };
        Loaded += (_, _) => FocusGrid();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SheetsViewModel.Document)) HookDocument();
    }

    private void HookDocument()
    {
        if (_doc != null) _doc.SelectionChanged -= UpdateNameBox;
        _doc = _vm?.Document;
        if (_doc != null) _doc.SelectionChanged += UpdateNameBox;
        UpdateNameBox();
    }

    private void UpdateNameBox()
    {
        if (!NameBox.IsKeyboardFocusWithin) NameBox.Text = _doc?.ActiveAddress ?? "";
    }

    private void FocusGrid() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (!Grid.IsVisible || FormulaBox.IsKeyboardFocusWithin) return;
        Grid.Focus();
        Keyboard.Focus(Grid);
    });

    // ----- name box (go to a cell) -----

    private void OnNameBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, NameBox.SelectAll);

    private void OnNameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (!_vm.GoTo(NameBox.Text)) { NameBox.SelectAll(); return; }
            UpdateNameBox();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NameBox.Text = _doc?.ActiveAddress ?? "";
            FocusGrid();
        }
    }

    // ----- formula bar -----

    private void OnFormulaFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm != null) _vm.FormulaBarEditing = true;
    }

    private void OnFormulaKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            _vm.CommitFormula(move: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CancelFormula();
        }
    }

    private void OnFormulaLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is not { FormulaBarEditing: true }) return;
        if (_vm.FormulaText != (_doc?.ActiveRaw ?? "")) _vm.CommitFormula(move: false);
        else _vm.FormulaBarEditing = false;
    }

    // ----- number formats -----

    private void OnFormatMenu(object sender, RoutedEventArgs e) => FormatPopup.IsOpen = true;

    private void OnFormatChosen(object sender, RoutedEventArgs e)
    {
        FormatPopup.IsOpen = false;
        if ((sender as FrameworkElement)?.DataContext is FormatChoice choice) _vm?.SetFormatCommand.Execute(choice);
    }

    private void OnTitleClick(object sender, MouseButtonEventArgs e) => _vm?.RenameCommand.Execute(null);

    // ----- sheet tabs -----

    private void OnTabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SheetTab tab && _vm != null)
        {
            e.Handled = true;
            _vm.RenameTabCommand.Execute(tab);
        }
    }
}
