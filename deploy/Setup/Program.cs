using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("================================================");
Console.WriteLine("       ResHog Setup v0.2.0");
Console.WriteLine("       Windows Resource Monitor Installer");
Console.WriteLine("================================================");
Console.WriteLine();

// ---------- 检测管理员权限 ----------
if (!IsAdmin())
{
    Console.WriteLine("ResHog 安装需要管理员权限。");
    Console.WriteLine("正在请求管理员权限（请在 UAC 对话框中点击\"是\"）...");
    Console.WriteLine();

    var psi = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath,
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = AppContext.BaseDirectory
    };
    try
    {
        var proc = Process.Start(psi);
        proc?.WaitForExit();
        Environment.Exit(proc?.ExitCode ?? 0);
    }
    catch
    {
        Console.WriteLine("用户取消了管理员授权，安装已取消。");
        Console.WriteLine("提示：也可以右键点击 setup.exe，选择\"以管理员身份运行\"。");
        Pause();
        Environment.Exit(1);
    }
    return;
}

// ---------- 提取 Payload.zip 到临时目录 ----------
string tempDir = Path.Combine(Path.GetTempPath(), "ResHogSetup_" + Guid.NewGuid().ToString("N")[..8]);
Console.WriteLine("正在准备安装文件...");
Console.WriteLine("临时目录: " + tempDir);
Console.WriteLine();

ExtractPayload(tempDir);

string installPs1 = Path.Combine(tempDir, "install.ps1");
string uninstallPs1 = Path.Combine(tempDir, "uninstall.ps1");

if (!File.Exists(installPs1))
{
    Console.WriteLine("错误：安装包内部文件损坏，缺少 install.ps1。");
    Cleanup(tempDir);
    Pause();
    Environment.Exit(1);
}

// [1/2] 卸载旧版本
Console.WriteLine("[1/2] 正在卸载旧版本（保留历史数据）...");
int code = RunPowerShell(uninstallPs1, "-KeepData");
if (code == 0)
    Console.WriteLine("  [OK] 卸载完成");
else
    Console.WriteLine("  [!] 卸载过程有警告（首次安装或无旧版本可卸载，属于正常情况）");

Console.WriteLine();

// [2/2] 安装新版本
Console.WriteLine("[2/2] 正在安装新版本...");
code = RunPowerShell(installPs1, "");
if (code == 0)
{
    Console.WriteLine("  [OK] 安装完成");
}
else
{
    Console.WriteLine($"  [X] 安装失败，退出代码: {code}");
    Cleanup(tempDir);
    Pause();
    Environment.Exit(1);
}

// ---------- 验证服务 ----------
Console.WriteLine();
Console.WriteLine("服务状态:");
try
{
    var sc = Process.Start(new ProcessStartInfo("sc", "query ResHog")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    });
    if (sc != null)
    {
        var scOut = sc.StandardOutput.ReadToEnd();
        sc.WaitForExit(3000);
        foreach (var line in scOut.Split('\n'))
            if (line.Contains("STATE", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("  " + line.Trim());
    }
}
catch { }

Console.WriteLine();
Console.WriteLine("API 验证（等待 10 秒让服务初始化）...");
Thread.Sleep(10000);

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var resp = await http.GetAsync("http://localhost:5180/api/health");
    if (resp.IsSuccessStatusCode)
        Console.WriteLine("  [OK] ResHog 服务运行正常");
    else
        Console.WriteLine("  [!] 服务状态异常，HTTP " + resp.StatusCode);
}
catch
{
    Console.WriteLine("  [!] 服务已安装但尚未就绪（PDH 预热中），请稍后刷新");
}

// ---------- 清理临时文件 ----------
Cleanup(tempDir);

Console.WriteLine();
Console.WriteLine("================================================");
Console.WriteLine("  安装成功!");
Console.WriteLine("================================================");
Console.WriteLine();
Console.WriteLine("使用提示:");
Console.WriteLine("  - ResHog 服务已在后台自动运行");
Console.WriteLine("  - 启动 ResHog.UI.exe 查看监控数据");
Console.WriteLine("  - 如需彻底卸载，以管理员身份运行：uninstall.ps1");
Console.WriteLine();
Pause();

// ============================================================

static bool IsAdmin()
{
    try
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    catch { return false; }
}

static void ExtractPayload(string targetDir)
{
    var asm = Assembly.GetExecutingAssembly();
    var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Payload.zip"));
    if (resName == null)
    {
        Console.WriteLine("  错误：未找到 Payload.zip 嵌入资源");
        return;
    }

    using var stream = asm.GetManifestResourceStream(resName);
    if (stream == null)
    {
        Console.WriteLine("  错误：无法读取 Payload.zip");
        return;
    }

    using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
    zip.ExtractToDirectory(targetDir);
    Console.WriteLine($"  已提取 {zip.Entries.Count} 个文件 ({targetDir})");
}

static void Cleanup(string tempDir)
{
    try
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }
    catch { }
}

static int RunPowerShell(string scriptPath, string args)
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

static void Pause()
{
    Console.Write("按任意键继续...");
    Console.ReadKey(true);
    Console.WriteLine();
}
