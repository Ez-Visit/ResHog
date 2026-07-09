using Avalonia.Controls;
using Avalonia.Input;
using ResHog.Shared.Dtos;

namespace ResHog.UI.Views;

public partial class ProcessManagerView : UserControl
{
    public ProcessManagerView()
    {
        InitializeComponent();
    }

    private void OnKillClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ProcessInfoDto process)
        {
            if (DataContext is ViewModels.ProcessManagerViewModel vm)
            {
                vm.OnKillRequested(process);
            }
        }
    }
}
