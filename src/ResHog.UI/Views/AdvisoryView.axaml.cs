using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ResHog.UI.Views;

public partial class AdvisoryView : UserControl
{
    private bool _initialLoaded;

    public AdvisoryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>首次进入页面时自动加载当前建议(此后由视图切换/刷新按钮驱动)。</summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_initialLoaded) return;
        _initialLoaded = true;
        if (DataContext is ViewModels.AdvisoryViewModel vm)
        {
            vm.LoadCommand.Execute(null);
        }
    }
}
