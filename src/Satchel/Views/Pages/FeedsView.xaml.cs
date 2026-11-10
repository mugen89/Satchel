using System.Windows.Controls;
using System.Windows.Input;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class FeedsView : UserControl
{
    public FeedsView() => InitializeComponent();

    private void OnSeek(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FeedsViewModel vm && SeekTrack.ActualWidth > 0) vm.SeekFraction(e.GetPosition(SeekTrack).X / SeekTrack.ActualWidth);
    }
}
