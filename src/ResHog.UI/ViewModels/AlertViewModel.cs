using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.Shared.Dtos;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

public partial class AlertViewModel : ViewModelBase
{
    private readonly MonitorApiClient _apiClient;

    [ObservableProperty]
    private string _selectedRange = "24h";

    [ObservableProperty]
    private string _selectedSeverity = "all";

    [ObservableProperty]
    private NamedOption _selectedRangeOption = RangeOptions[1];

    [ObservableProperty]
    private NamedOption _selectedSeverityOption = SeverityOptions[0];

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _totalCount;

    public ObservableCollection<AlertDto> Alerts { get; } = new();

    public static NamedOption[] RangeOptions { get; } =
    [
        new("1h", "最近 1 小时"),
        new("24h", "最近 24 小时"),
        new("7d", "最近 7 天")
    ];

    public static NamedOption[] SeverityOptions { get; } =
    [
        new("all", "全部"),
        new("critical", "严重"),
        new("warning", "警告"),
        new("info", "提示")
    ];

    public AlertViewModel(MonitorApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [RelayCommand]
    private async Task LoadAlertsAsync()
    {
        IsLoading = true;
        ClearError();

        try
        {
            var severity = SelectedSeverity == "all" ? null : SelectedSeverity;
            var data = await _apiClient.GetAlertsAsync(SelectedRange, severity);
            var t = _apiClient.LastTiming;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Alerts.Clear();
            if (data != null)
            {
                foreach (var alert in data)
                    Alerts.Add(alert);

                TotalCount = data.Count;
                CriticalCount = data.Count(a => a.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase));
                WarningCount = data.Count(a => a.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                TotalCount = 0;
                CriticalCount = 0;
                WarningCount = 0;
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
            SetError($"加载告警失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedRangeOptionChanged(NamedOption value)
    {
        if (SelectedRange != value.Value)
        {
            SelectedRange = value.Value;
            LoadAlertsCommand.Execute(null);
        }
    }

    partial void OnSelectedSeverityOptionChanged(NamedOption value)
    {
        if (SelectedSeverity != value.Value)
        {
            SelectedSeverity = value.Value;
            LoadAlertsCommand.Execute(null);
        }
    }
}
