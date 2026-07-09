using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ResHog.UI.Services;
using ResHog.UI.ViewModels;
using ResHog.UI.Views;

namespace ResHog.UI;

public partial class App : Application
{
    private IServiceProvider? _services;
    private bool _isExiting;

    public override void Initialize()
    {
#pragma warning disable IL2026
        AvaloniaXamlLoader.Load(this);
#pragma warning restore IL2026
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            desktop.MainWindow = mainWindow;

            // Start the health polling loop. This is fire-and-forget because
            // the loop runs for the lifetime of the application.
            _ = mainViewModel.InitializeAsync();

            // 窗口关闭时最小化到托盘而非退出
            mainWindow.Closing += (s, e) =>
            {
                if (!_isExiting)
                {
                    e.Cancel = true;
                    mainViewModel.IsVisible = false; // pause polling
                    mainWindow.Hide();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServices()
    {
        var collection = new ServiceCollection();

        // Services
        collection.AddSingleton<MonitorApiClient>();

        // ViewModels
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<DashboardViewModel>();
        collection.AddSingleton<TopNViewModel>();
        collection.AddSingleton<TrendViewModel>();
        collection.AddSingleton<AlertViewModel>();
        collection.AddSingleton<ProcessManagerViewModel>();

        return collection.BuildServiceProvider();
    }

    // --- Tray icon event handlers ---

    private void OnTrayClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnTrayOpen(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        _isExiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } window &&
            desktop.MainWindow.DataContext is ViewModels.MainViewModel vm)
        {
            vm.IsVisible = true; // resume polling
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }
}
