using System.Windows.Controls;
using System.Windows.Threading;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class TaskDetailView : UserControl
{
    public TaskDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is TaskDetailViewModel vm && string.IsNullOrWhiteSpace(vm.Task.Title))
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TitleBox.Focus());
        };
    }
}
