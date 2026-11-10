using System.Windows.Controls;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class MoneyView : UserControl
{
    public MoneyView() => InitializeComponent();

    private void OnCurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MoneyViewModel vm && sender is ComboBox { SelectedValue: string currency } && currency != vm.MainCurrency)
            vm.SetMainCurrencyCommand.Execute(currency);
    }
}
