using System.Collections.Concurrent;
using System.Diagnostics;
using ResHog.Collectors;

namespace ResHog.Services;

/// <summary>
/// 进程友好显示名解析(对齐任务管理器语义,DISP-2,2026-09-04)。
///
/// 解析优先级:
///   1. svchost.exe 且 SCM 有该 PID 服务 → "服务主机: &lt;服务显示名&gt;"
///      (服务显示名为 SCM lpDisplayName,本地化;依赖 ServiceMapper 提供)
///   2. exe 版本资源 FileDescription(FileVersionInfo,标准版本 API
///      自动做 MUI 合并——中文系统上系统二进制自动得到"控制台窗口主机"等中文名)
///   3. 回退 exe 名(调用方以 ProcessName 兜底,本服务返回 null 表示无可解析项)
///
/// 缓存策略:exe 路径 → FileDescription 按**服务生命周期**永久缓存
/// (版本资源不可变,唯一 exe 路径集合有限,~数百条,内存可忽略);
/// 解析失败(权限/PPL/文件消失)也缓存 null,避免重复异常开销。
/// 仅在 ProcessManager 后台刷新线程调用解析(不在 HTTP 请求路径读文件)。
///
/// 可伪造性说明:FileDescription 是 exe 自声明元数据,与任务管理器有同样弱点;
/// UI 侧保留 exe 名作为 ToolTip 对冲。
/// </summary>
public class ProcessDisplayNameService
{
    private readonly ConcurrentDictionary<string, string?> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServiceMapper _serviceMapper;

    /// <summary>任务管理器同款"服务主机"前缀(ResHog UI 为中文,写死;本地化属未来项)。</summary>
    public const string ServiceHostPrefix = "服务主机: ";

    public ProcessDisplayNameService(ServiceMapper serviceMapper)
    {
        _serviceMapper = serviceMapper;
    }

    /// <summary>
    /// 解析单个进程的显示名;返回 null 表示回退到 exe 名(调用方兜底)。
    /// 仅在后台刷新线程调用;缓存命中 O(1),未缓存路径首次为一次版本资源读。
    /// </summary>
    public string? Resolve(int pid, string processName, string? exePath)
    {
        // 1. svchost:任务管理器显示"服务主机: <服务显示名>"
        if (processName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
        {
            _serviceMapper.RefreshIfNeeded();
            var svcDisplay = _serviceMapper.GetServiceDisplayName(pid);
            if (!string.IsNullOrEmpty(svcDisplay))
                return ServiceHostPrefix + svcDisplay;
            // 无服务信息 → 落到 FileDescription("Host Process for Windows Services")
        }

        // 2. FileDescription(MUI 本地化,标准版本 API)
        if (!string.IsNullOrEmpty(exePath))
        {
            var desc = _pathCache.GetOrAdd(exePath, ReadFileDescription);
            if (!string.IsNullOrWhiteSpace(desc))
                return desc;
        }

        return null; // 调用方回退 exe 名
    }

    /// <summary>
    /// 按 exe 名富化(仪表盘路径:数据行只有 process_name,无 PID 语义)。
    /// svchost 多实例共享同一 exe 名,逐 PID 服务名不可区分,统一显示通用标签;
    /// 其余 exe 名不做文件解析(避免在请求路径读文件),返回 null 由调用方兜底 exe 名。
    /// </summary>
    public string? ResolveByExeName(string processName)
    {
        return processName.Equals("svchost", StringComparison.OrdinalIgnoreCase)
            ? "服务主机(系统服务)"
            : null;
    }

    private string? ReadFileDescription(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch
        {
            // 文件被删/权限受限/PPL 保护等 → 缓存 null
            return null;
        }
    }
}
