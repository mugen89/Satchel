using System.Windows.Controls;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class InsightsView : UserControl
{
    public InsightsView()
    {
        InitializeComponent();
        ActivityChart.DayClicked += index => (DataContext as InsightsViewModel)?.OpenDay(index);
    }
}
