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
    private DateTime? _lastUpdateWithRender;

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

        // Subscribe to timing events — update status bar immediately after
        // ANY successful API call from any page (no polling).
        _apiClient.TimingUpdated += OnTimingUpdated;
    }

    /// <summary>
    /// Event handler: fires after every successful API call (or render-timing push)
    /// from any ViewModel. Updates the status-bar display instantly with zero polling.
    ///
    /// Health-check timings (RenderMs=0) do NOT overwrite the current display when a
    /// richer timing (Dashboard/TopN/Alert etc.) is already showing, so e.g.
    /// "API 42ms | DB 35ms | 渲染 8ms" doesn't get replaced by "API 3ms" after
    /// a cache-hit health check that has no DB or render time.
    /// </summary>
    private void OnTimingUpdated(ApiCallTiming timing)
    {
        if (!IsServiceOnline) return;

        // Always update if the new timing has render info (comes from user-action pages).
        if (timing.RenderMs > 0)
        {
            ResponseTimeText = timing.Summary;
            _lastUpdateWithRender = DateTime.Now;
        }
        else if (_lastUpdateWithRender == null ||
                 DateTime.Now - _lastUpdateWithRender.Value > TimeSpan.FromSeconds(30))
        {
            // No rich timing available yet, or last one is too stale — show whatever we have.
            ResponseTimeText = timing.Summary;
        }
        // Otherwise: a rich timing (with render info) is still "sticky" on the display —
        // don't overwrite with a bare health-check timing.
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
                    // OnTimingUpdated will fire via the TimingUpdated event
                    // and update ResponseTimeText automatically.
                    var uptime = TimeSpan.FromSeconds(health.UptimeSeconds);
                    StatusText = $"服务运行中 | 采样 {health.SampleCount:N0} | 监控 {health.MonitoredProcesses} 进程 | 运行 {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";

                    // Auto-start dashboard polling only when the Dashboard tab is active
                    if (SelectedTabIndex == 0 && !_dashboard.IsPolling)
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
                await Task.Delay(60000, token);
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

        // Dashboard: only poll when tab 0 is active
        if (value == 0)
        {
            if (IsServiceOnline && !_dashboard.IsPolling)
                _dashboard.StartPolling();
        }
        else
        {
            if (_dashboard.IsPolling)
                _dashboard.StopPolling();
        }

        // Stop process manager polling when leaving its tab (index 1)
        if (value != 1)
            _processManager.AutoRefresh = false;

        // Lazy-load data when switching tabs
        switch (value)
        {
            case 0: // Dashboard — already polling, no extra load needed
                break;
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
