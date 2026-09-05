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

    /// <summary>搜索框回车 = 点击"搜索"按钮(复用 SearchCommand;Loading 中忽略防重入)。</summary>
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ViewModels.ProcessManagerViewModel vm && !vm.IsLoading)
        {
            vm.SearchCommand.Execute(null);
        }
    }
}
