using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.Shared.Dtos;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

public partial class TrendViewModel : ViewModelBase
{
    private readonly MonitorApiClient _apiClient;

    [ObservableProperty]
    private string? _selectedProcess;

    [ObservableProperty]
    private string _selectedMetric = "cpu";

    [ObservableProperty]
    private string _selectedRange = "1h";

    [ObservableProperty]
    private NamedOption _selectedMetricOption = MetricOptions[0];

    [ObservableProperty]
    private NamedOption _selectedRangeOption = RangeOptions[0];

    [ObservableProperty]
    private double _avgValue;

    [ObservableProperty]
    private double _maxValue;

    [ObservableProperty]
    private string? _processDetailInfo;

    [ObservableProperty]
    private string? _selectedProcessServiceName;

    /// <summary>Unit string derived from the selected metric for display (e.g. "%", "MB", "MB/s").</summary>
    public string TrendUnit => SelectedMetric.ToLowerInvariant() switch
    {
        "cpu" => "%",
        "memory" => "MB",
        "io_read" or "io_write" => "MB/s",
        _ => ""
    };

    public ObservableCollection<string> ProcessNames { get; } = new();
    public ObservableCollection<TrendPointDto> TrendPoints { get; } = new();

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

    public TrendViewModel(MonitorApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [RelayCommand]
    private async Task LoadProcessNamesAsync()
    {
        try
        {
            var names = await _apiClient.GetProcessNamesAsync();
            var t = _apiClient.LastTiming;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (names != null)
            {
                ProcessNames.Clear();
                foreach (var n in names)
                    ProcessNames.Add(n);
            }
            sw.Stop();

            ApiMs = t.NetworkMs;
            ServerMs = t.ServerMs;
            DbMs = t.DbMs;
            RenderMs = sw.ElapsedMilliseconds;
            _apiClient.SetTiming(t.NetworkMs, t.ServerMs, t.DbMs, sw.ElapsedMilliseconds);
        }
        catch
        {
        }
    }

    [RelayCommand]
    public async Task LoadTrendAsync()
    {
        if (string.IsNullOrEmpty(SelectedProcess))
            return;

        IsLoading = true;
        ClearError();
        TrendPoints.Clear();

        try
        {
            // 缺陷 #12 修复：并行发起 trend + detail 两个请求，总延迟 = max(trend, detail) 而非 trend + detail
            // 注意：即使 trend 返回空也要让 detail 任务完成（避免异常未观察）；
            //       是否使用 detail 结果由下面 points.Count > 0 决定
            var trendTask = _apiClient.GetTrendAsync(SelectedProcess, SelectedMetric, SelectedRange);
            var detailTask = _apiClient.GetProcessDetailAsync(SelectedProcess, SelectedRange);
            await Task.WhenAll(trendTask, detailTask);

            var points = trendTask.Result;
            var detail = (points != null && points.Count > 0) ? detailTask.Result : null;

            var t = _apiClient.LastTiming;
            var renderSw = System.Diagnostics.Stopwatch.StartNew();

            TrendPoints.Clear();
            if (points != null && points.Count > 0)
            {
                foreach (var p in points)
                    TrendPoints.Add(p);

                AvgValue = points.Average(p => p.Value);
                MaxValue = points.Max(p => p.Value);

                if (detail != null)
                {
                    SelectedProcessServiceName = detail.ServiceName ?? "—";
                    ProcessDetailInfo = $"PID: {string.Join(", ", detail.Pids)} | " +
                        $"样本数: {detail.SampleCount:N0} | " +
                        $"首次: {detail.FirstSeen} | 末次: {detail.LastSeen}";
                }
            }
            else
            {
                AvgValue = 0;
                MaxValue = 0;
                ProcessDetailInfo = "无数据";
            }

            renderSw.Stop();
            ApiMs = t.NetworkMs;
            ServerMs = t.ServerMs;
            DbMs = t.DbMs;
            RenderMs = renderSw.ElapsedMilliseconds;
            _apiClient.SetTiming(t.NetworkMs, t.ServerMs, t.DbMs, renderSw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            SetError($"加载趋势失败: {ex.Message}");
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
            OnPropertyChanged(nameof(TrendUnit));
            if (!string.IsNullOrEmpty(SelectedProcess))
                LoadTrendCommand.Execute(null);
        }
    }

    partial void OnSelectedRangeOptionChanged(NamedOption value)
    {
        if (SelectedRange != value.Value)
        {
            SelectedRange = value.Value;
            if (!string.IsNullOrEmpty(SelectedProcess))
                LoadTrendCommand.Execute(null);
        }
    }
}
