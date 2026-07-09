using System.Diagnostics;
using System.Security.Principal;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ResHog.SetupUI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new InstallerViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            // Start installation immediately
            window.Opened += async (s, e) =>
            {
                await vm.RunInstallationAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Check admin privileges first
        if (!IsAdmin())
        {
            // Self-elevate with runas — single UAC prompt, no extra windows
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = true,
                Verb = "runas"
            };
            try
            {
                Process.Start(psi);
            }
            catch { }
            return;
        }

        // Already admin: launch Avalonia UI
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
