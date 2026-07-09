using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MonitorApiClient _apiClient;
    private readonly DashboardViewModel _dashboard;
    private readonly ProcessManagerViewModel _processManager;
    private readonly TopNViewModel _topN;
    private readonly TrendViewModel _trend;
    private readonly AlertViewModel _alerts;
    private CancellationTokenSource? _healthCts;

    [ObservableProperty]
    private bool _isServiceOnline;

    [ObservableProperty]
    private string _statusText = "正在连接服务...";

    [ObservableProperty]
    private string _responseTimeText = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isVisible = true;

    public DashboardViewModel Dashboard => _dashboard;
    public ProcessManagerViewModel ProcessManager => _processManager;
    public TopNViewModel TopN => _topN;
    public TrendViewModel Trend => _trend;
    public AlertViewModel Alerts => _alerts;

    public MainViewModel(
        MonitorApiClient apiClient,
        DashboardViewModel dashboard,
        ProcessManagerViewModel processManager,
        TopNViewModel topN,
        TrendViewModel trend,
        AlertViewModel alerts)
    {
        _apiClient = apiClient;
        _dashboard = dashboard;
        _processManager = processManager;
        _topN = topN;
        _trend = trend;
        _alerts = alerts;
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        await StartHealthPolling();
    }

    private async Task StartHealthPolling()
    {
        _healthCts?.Cancel();
        _healthCts = new CancellationTokenSource();
        var token = _healthCts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                IsChecking = true;
                var health = await _apiClient.GetHealthAsync();
                if (health != null)
                {
                    IsServiceOnline = true;
                    ResponseTimeText = $"响应 {_apiClient.LastResponseTimeMs}ms";
                    var uptime = TimeSpan.FromSeconds(health.UptimeSeconds);
                    StatusText = $"服务运行中 | 采样 {health.SampleCount:N0} | 监控 {health.MonitoredProcesses} 进程 | 运行 {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";

                    // Auto-start dashboard polling on first successful connection
                    if (!_dashboard.IsPolling)
                        _dashboard.StartPolling();
                }
                else
                {
                    IsServiceOnline = false;
                    ResponseTimeText = string.Empty;
                    StatusText = "⚠ Service offline — please confirm ResHog service is running";
                    _dashboard.StopPolling();
                }
            }
            catch
            {
                IsServiceOnline = false;
                ResponseTimeText = string.Empty;
                StatusText = "⚠ Cannot connect to service (127.0.0.1:5180)";
                _dashboard.StopPolling();
            }
            finally
            {
                IsChecking = false;
            }

            try
            {
                await Task.Delay(5000, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (!IsVisible) return; // ignore tab switch while hidden/minimized

        // Stop process manager polling when leaving its tab (index 1)
        if (value != 1)
            _processManager.AutoRefresh = false;

        // Lazy-load data when switching tabs
        switch (value)
        {
            case 1: // Process Manager — initial load, no auto-refresh
                if (_processManager.SearchResults.Count == 0)
                    _processManager.SearchCommand.Execute(null);
                break;
            case 2: // TopN
                if (_topN.Results.Count == 0)
                    _topN.LoadDataCommand.Execute(null);
                break;
            case 3: // Trend
                if (_trend.ProcessNames.Count == 0)
                    _trend.LoadProcessNamesCommand.Execute(null);
                break;
            case 4: // Alerts
                if (_alerts.Alerts.Count == 0)
                    _alerts.LoadAlertsCommand.Execute(null);
                break;
        }
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (value)
        {
            // Window restored/shown — restart health polling (auto-restarts dashboard)
            _ = StartHealthPolling();
        }
        else
        {
            // Window hidden/minimized — stop all request loops
            StopAllPolling();
        }
    }

    public void StopAllPolling()
    {
        _healthCts?.Cancel();
        _dashboard.StopPolling();
        _processManager.AutoRefresh = false;
    }
}
