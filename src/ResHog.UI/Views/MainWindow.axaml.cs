using Avalonia;
using Avalonia.Controls;

namespace ResHog.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        this.PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty &&
            DataContext is ViewModels.MainViewModel vm)
        {
            var newState = (WindowState)(e.NewValue ?? WindowState.Normal);
            vm.IsVisible = newState != WindowState.Minimized;
        }
    }
}
