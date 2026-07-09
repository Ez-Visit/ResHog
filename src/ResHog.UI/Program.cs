using Avalonia;
using System;
using System.Threading;

namespace ResHog.UI;

internal class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 单实例：同一目录下只允许一个 ResHog.UI 运行
        var exeDir = AppContext.BaseDirectory;
        var mutexName = "ResHog_UI_Instance_" + exeDir.Replace('\\', '_').Replace(':', '_');
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
            return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
