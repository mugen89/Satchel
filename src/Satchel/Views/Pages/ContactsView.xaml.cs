using System.ComponentModel;
using System.Windows.Controls;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class ContactsView : UserControl
{
    private ContactsViewModel? _vm;

    public ContactsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SearchFocusRequested -= FocusSearch;
            }
            _vm = e.NewValue as ContactsViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            _vm.SearchFocusRequested += FocusSearch;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContactsViewModel.Selected) && _vm?.Selected != null) Anim.EnterUp(Detail, 12, 300);
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
