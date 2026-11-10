using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Satchel.Core;
using Satchel.Models;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class VaultView : UserControl
{
    private VaultViewModel? _vm;

    public VaultView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SearchFocusRequested -= FocusSearch;
            }
            _vm = e.NewValue as VaultViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            _vm.SearchFocusRequested += FocusSearch;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultViewModel.SelectedProfile) && _vm?.SelectedProfile != null)
        {
            DetailScroll.ScrollToTop();
            Anim.EnterUp(DetailContent, 12, 300);
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnAccountHeaderClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return;
        if ((sender as FrameworkElement)?.DataContext is AccountItemViewModel item) item.IsExpanded = !item.IsExpanded;
    }

    private async void OnMoveAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: VaultProfile target } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: AccountItemViewModel account })
            await _vm.MoveAccountAsync(account, target);
    }

    private void OnScoreClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var first = _vm.HealthFilters.FirstOrDefault(f => f.Count > 0);
        _vm.SelectFilterCommand.Execute(first ?? _vm.AllFilter);
    }

    private void OnAddAccountHint(object sender, MouseButtonEventArgs e) => _vm?.NewAccountCommand.Execute(null);
}
