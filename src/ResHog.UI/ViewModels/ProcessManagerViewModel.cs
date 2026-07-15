using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.Shared.Dtos;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

public partial class ProcessManagerViewModel : ViewModelBase
{
    private readonly MonitorApiClient _apiClient;
    private CancellationTokenSource? _pollCts;
    private bool _isPolling;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private NamedOption _selectedSearchMode = SearchModes[0];

    [ObservableProperty]
    private bool _autoRefresh;

    public static NamedOption[] SearchModes { get; } =
    [
        new("name", "按进程名"),
        new("port", "按端口号")
    ];

    [ObservableProperty]
    private string? _killMessage;

    [ObservableProperty]
    private bool _hasResultMessage;

    [ObservableProperty]
    private bool _showConfirmOverlay;

    [ObservableProperty]
    private string _confirmMessage = string.Empty;

    private ProcessInfoDto? _pendingKillProcess;

    public ObservableCollection<ProcessInfoDto> SearchResults { get; } = new();

    public ProcessManagerViewModel(MonitorApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    /// <summary>
    /// Called from code-behind when the kill button is clicked.
    /// Shows the confirmation overlay instead of killing immediately.
    /// </summary>
    public void OnKillRequested(ProcessInfoDto process)
    {
        _pendingKillProcess = process;
        ConfirmMessage = $"确定要终止进程 {process.ProcessName} (PID: {process.Pid}) ？";
        ShowConfirmOverlay = true;
    }

    [RelayCommand]
    private void ConfirmKill(bool confirmed)
    {
        ShowConfirmOverlay = false;

        if (confirmed && _pendingKillProcess != null)
        {
            _ = ExecuteKillAsync(_pendingKillProcess);
        }

        _pendingKillProcess = null;
    }

    private async Task ExecuteKillAsync(ProcessInfoDto process)
    {
        KillMessage = null;
        IsLoading = true;

        try
        {
            var result = await _apiClient.KillProcessAsync(process.Pid);
            if (result != null)
            {
                KillMessage = result.Message;

                if (result.Success)
                {
                    SearchResults.Remove(process);
                    HasResultMessage = true;
                }
            }
            else
            {
                KillMessage = "终止失败：服务未响应。";
            }
        }
        catch (Exception ex)
        {
            KillMessage = $"终止失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
            StartPolling();
        else
            StopPolling();
    }

    private void StartPolling()
    {
        if (_isPolling) return;
        _isPolling = true;
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollLoop(_pollCts.Token);
    }

    private void StopPolling()
    {
        _isPolling = false;
        _pollCts?.Cancel();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        await SearchAsync();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch (TaskCanceledException) { break; }
            if (ct.IsCancellationRequested) break;
            await SearchAsync();
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsLoading = true;
        ClearError();
        KillMessage = null;
        HasResultMessage = false;

        try
        {
            var results = await _apiClient.SearchProcessesAsync(SearchQuery);
            var t = _apiClient.LastTiming;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (results != null)
            {
                // Replace old results (avoid flicker from Clear+Add loop)
                SearchResults.Clear();
                foreach (var r in results)
                    SearchResults.Add(r);

                HasResultMessage = true;
                KillMessage = string.IsNullOrWhiteSpace(SearchQuery)
                    ? $"当前共 {results.Count} 个进程，每 3 秒刷新。"
                    : results.Count == 0
                        ? $"未找到匹配 \"{SearchQuery}\" 的进程。"
                        : $"找到 {results.Count} 个进程。";
            }
            else
            {
                SetError("搜索失败，服务未响应。");
            }
            sw.Stop();

            ApiMs = t.NetworkMs;
            ServerMs = t.ServerMs;
            DbMs = t.DbMs;
            RenderMs = sw.ElapsedMilliseconds;
            _apiClient.SetTiming(t.NetworkMs, t.ServerMs, t.DbMs, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            SetError($"搜索失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
