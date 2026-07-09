using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ResHog.SetupUI;

public class InstallStep : INotifyPropertyChanged
{
    public string Title { get; set; } = "";
    
    private string _status = "Pending";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(StatusColor)); }
    }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged(); } }

    public string StatusIcon => Status switch
    {
        "Running" => "\u25B6",   // ▶
        "Success" => "\u2714",   // ✔
        "Fail" => "\u2718",      // ✘
        _ => "\u25CB"            // ○
    };

    public string StatusColor => Status switch
    {
        "Success" => "#1D9E75",
        "Fail" => "#E24B4A",
        "Running" => "#378ADD",
        _ => "#CCCCCC"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class InstallerViewModel : INotifyPropertyChanged
{
    public ObservableCollection<InstallStep> Steps { get; } = new();

    private string _statusText = "";
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

    private bool _isComplete;
    public bool IsComplete { get => _isComplete; set { _isComplete = value; OnPropertyChanged(); } }

    private bool _hasFailed;
    public bool HasFailed { get => _hasFailed; set { _hasFailed = value; OnPropertyChanged(); } }

    public InstallerViewModel()
    {
        Steps.Add(new InstallStep { Title = "1. 准备安装文件..." });
        Steps.Add(new InstallStep { Title = "2. 关闭运行中的客户端程序..." });
        Steps.Add(new InstallStep { Title = "3. 卸载旧版本（保留数据）..." });
        Steps.Add(new InstallStep { Title = "4. 安装新版本..." });
        Steps.Add(new InstallStep { Title = "5. 验证服务状态..." });
    }

    public async Task RunInstallationAsync()
    {
        string? tempDir = null;

        try
        {
            // Step 1: Extract
            Steps[0].Status = "Running";
            Steps[0].IsRunning = true;
            StatusText = "正在准备安装文件...";

            tempDir = Path.Combine(Path.GetTempPath(), "ResHogSetup_" + Guid.NewGuid().ToString("N")[..8]);
            await Task.Run(() => ExtractPayload(tempDir));
            Steps[0].Status = "Success";
            Steps[0].IsRunning = false;

            var installPs1 = Path.Combine(tempDir, "install.ps1");
            var uninstallPs1 = Path.Combine(tempDir, "uninstall.ps1");

            // Step 2: Kill running ResHog processes (UI client, console-mode service)
            Steps[1].Status = "Running";
            Steps[1].IsRunning = true;
            StatusText = "正在关闭 ResHog.UI.exe...";

            await Task.Run(() => KillRelatedProcesses());
            Steps[1].Status = "Success";
            Steps[1].IsRunning = false;

            // Step 3: Uninstall
            Steps[2].Status = "Running";
            Steps[2].IsRunning = true;
            StatusText = "正在卸载旧版本...";

            int code = await Task.Run(() => RunPowerShell(uninstallPs1, "-KeepData"));
            Steps[2].Status = "Success";
            Steps[2].IsRunning = false;

            // Step 4: Install
            Steps[3].Status = "Running";
            Steps[3].IsRunning = true;
            StatusText = "正在安装新版本...";

            code = await Task.Run(() => RunPowerShell(installPs1, ""));
            if (code != 0)
            {
                Steps[3].Status = "Fail";
                Steps[3].IsRunning = false;
                HasFailed = true;
                StatusText = $"安装失败，退出代码: {code}";
                IsComplete = true;
                return;
            }
            Steps[3].Status = "Success";
            Steps[3].IsRunning = false;

            // Step 5: Verify
            Steps[4].Status = "Running";
            Steps[4].IsRunning = true;
            StatusText = "正在验证服务状态 (等待 PDH 预热)...";

            await Task.Delay(12000);
            var ok = await VerifyServiceAsync();
            Steps[4].Status = ok ? "Success" : "Fail";
            Steps[4].IsRunning = false;

            HasFailed = !ok;
            StatusText = ok ? "安装成功！" : "安装完成但服务验证失败，请稍后检查。";
            IsComplete = true;
        }
        catch (Exception ex)
        {
            StatusText = "安装出错: " + ex.Message;
            HasFailed = true;
            IsComplete = true;
        }
        finally
        {
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void ExtractPayload(string targetDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Payload.zip"));
        if (resName == null) throw new Exception("Payload.zip 未嵌入 setup.exe");

        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new Exception("无法读取 Payload.zip 资源");

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        zip.ExtractToDirectory(targetDir);
    }

    private static int RunPowerShell(string scriptPath, string args)
    {
        var cmd = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" {args}";
        var psi = new ProcessStartInfo("powershell", cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)
        };
        var proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc?.ExitCode ?? -1;
    }

    /// <summary>
    /// Kill any running ResHog processes that could lock installation files.
    /// </summary>
    private static void KillRelatedProcesses()
    {
        // Kill all ResHog.UI.exe instances (desktop client)
        foreach (var proc in Process.GetProcessesByName("ResHog.UI"))
        {
            try
            {
                proc.Kill();
                if (!proc.WaitForExit(5000))
                {
                    proc.Kill(); // force
                    proc.WaitForExit(2000);
                }
            }
            catch
            {
                // Process may have exited already or access denied
            }
        }

        // Kill any ResHog.Service.exe instances running in console mode
        // (the installed Windows service is handled by uninstall.ps1 via sc.exe stop)
        foreach (var proc in Process.GetProcessesByName("ResHog.Service"))
        {
            try
            {
                // Skip the Windows service (PID < 10000 heuristic; service usually
                // runs with a specific session). We only kill console-mode instances.
                if (proc.SessionId != System.Diagnostics.Process.GetCurrentProcess().SessionId)
                    continue;

                proc.Kill();
                if (!proc.WaitForExit(5000))
                {
                    proc.Kill();
                    proc.WaitForExit(2000);
                }
            }
            catch { }
        }
    }

    private static async Task<bool> VerifyServiceAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync("http://localhost:5180/api/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
