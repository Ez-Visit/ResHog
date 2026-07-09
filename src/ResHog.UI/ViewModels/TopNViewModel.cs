using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.Shared.Dtos;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

public partial class TopNViewModel : ViewModelBase
{
    private readonly MonitorApiClient _apiClient;

    [ObservableProperty]
    private string _selectedMetric = "cpu";

    [ObservableProperty]
    private string _selectedRange = "1h";

    [ObservableProperty]
    private NamedOption _selectedMetricOption = MetricOptions[0];

    [ObservableProperty]
    private NamedOption _selectedRangeOption = RangeOptions[0];

    [ObservableProperty]
    private int _limit = 20;

    public ObservableCollection<TopNResultDto> Results { get; } = new();

    public static NamedOption[] MetricOptions { get; } =
    [
        new("cpu", "CPU"),
        new("memory", "内存"),
        new("io_read", "磁盘读取"),
        new("io_write", "磁盘写入")
    ];

    public static NamedOption[] RangeOptions { get; } =
    [
        new("1h", "最近 1 小时"),
        new("24h", "最近 24 小时"),
        new("7d", "最近 7 天")
    ];

    public TopNViewModel(MonitorApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        IsLoading = true;
        ClearError();

        try
        {
            var data = await _apiClient.GetTopNAsync(SelectedMetric, Limit, SelectedRange);
            Results.Clear();
            if (data != null)
            {
                foreach (var item in data)
                    Results.Add(item);
            }
        }
        catch (Exception ex)
        {
            SetError($"加载失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedMetricOptionChanged(NamedOption value)
    {
        if (SelectedMetric != value.Value)
        {
            SelectedMetric = value.Value;
            LoadDataCommand.Execute(null);
        }
    }

    partial void OnSelectedRangeOptionChanged(NamedOption value)
    {
        if (SelectedRange != value.Value)
        {
            SelectedRange = value.Value;
            LoadDataCommand.Execute(null);
        }
    }
}
