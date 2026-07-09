using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.Shared.Dtos;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly MonitorApiClient _apiClient;
    private CancellationTokenSource? _pollingCts;

    [ObservableProperty]
    private DateTime _lastUpdate;

    [ObservableProperty]
    private long _lastResponseTimeMs;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private double _memoryMb;

    [ObservableProperty]
    private double _ioReadMbS;

    [ObservableProperty]
    private double _ioWriteMbS;

    [ObservableProperty]
    private int _totalProcesses;

    [ObservableProperty]
    private int _totalThreads;

    [ObservableProperty]
    private int _totalHandles;

    [ObservableProperty]
    private string? _lastError;

    public ObservableCollection<ProcessSummaryDto> TopCpu { get; } = new();
    public ObservableCollection<ProcessSummaryDto> TopMemory { get; } = new();
    public ObservableCollection<ProcessSummaryDto> TopIo { get; } = new();

    public bool IsPolling => _pollingCts != null && !_pollingCts.IsCancellationRequested;

    public DashboardViewModel(MonitorApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public void StartPolling()
    {
        if (IsPolling)
            return;

        _pollingCts?.Cancel();
        _pollingCts = new CancellationTokenSource();
        _ = PollDashboardAsync(_pollingCts.Token);
    }

    public void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts = null;
    }

    private async Task PollDashboardAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var dto = await _apiClient.GetDashboardAsync();
                if (dto != null)
                {
                    UpdateFromDto(dto);
                    LastResponseTimeMs = _apiClient.LastResponseTimeMs;
                    LastError = null;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            try
            {
                await Task.Delay(2000, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void UpdateFromDto(DashboardDto dto)
    {
        LastUpdate = DateTime.Now;

        var sys = dto.System;
        CpuPercent = sys.TotalCpuPercent;
        MemoryMb = sys.TotalMemoryMb;
        IoReadMbS = sys.TotalIoReadMbS;
        IoWriteMbS = sys.TotalIoWriteMbS;
        TotalProcesses = sys.TotalProcesses;
        TotalThreads = sys.TotalThreads;
        TotalHandles = sys.TotalHandles;

        UpdateList(TopCpu, dto.TopCpu);
        UpdateList(TopMemory, dto.TopMemory);
        UpdateList(TopIo, dto.TopIo);
    }

    private static void UpdateList<T>(ObservableCollection<T> collection, List<T> data)
    {
        collection.Clear();
        foreach (var item in data)
            collection.Add(item);
    }
}
