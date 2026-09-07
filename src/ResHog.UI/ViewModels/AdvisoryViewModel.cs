using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResHog.Shared.Dtos;
using ResHog.UI.Services;

namespace ResHog.UI.ViewModels;

/// <summary>
/// 健康诊断建议页(2026-09-06)。三个视图:当前建议卡片 / 历史 / 规则设置。
/// 纯推荐系统:仅展示与标记,服务端不执行任何进程/服务动作。
/// </summary>
public partial class AdvisoryViewModel : ViewModelBase
{
    private readonly MonitorApiClient _apiClient;

    [ObservableProperty]
    private NamedOption _selectedViewMode = ViewModes[0];   // active / history / rules

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private string _saveMessage = string.Empty;

    public ObservableCollection<FindingDto> Findings { get; } = new();
    public ObservableCollection<FindingDto> History { get; } = new();
    public ObservableCollection<AdvisoryRule> Rules { get; } = new();

    public bool IsViewActive => SelectedViewMode.Value == "active";
    public bool IsViewHistory => SelectedViewMode.Value == "history";
    public bool IsViewRules => SelectedViewMode.Value == "rules";

    public static NamedOption[] ViewModes { get; } =
    [
        new("active", "当前建议"),
        new("history", "历史记录"),
        new("rules", "规则设置")
    ];

    public AdvisoryViewModel(MonitorApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    partial void OnSelectedViewModeChanged(NamedOption value)
    {
        OnPropertyChanged(nameof(IsViewActive));
        OnPropertyChanged(nameof(IsViewHistory));
        OnPropertyChanged(nameof(IsViewRules));
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        ClearError();
        try
        {
            if (SelectedViewMode.Value == "rules")
            {
                var rules = await _apiClient.GetRulesAsync();
                if (rules != null)
                {
                    Rules.Clear();
                    foreach (var r in rules.Rules)
                        Rules.Add(r);
                }
                else SetError("规则加载失败,服务未响应。");
                return;
            }

            var status = SelectedViewMode.Value == "history" ? "history" : "active";
            var data = await _apiClient.GetFindingsAsync(status);
            if (data == null)
            {
                SetError("诊断建议加载失败,服务未响应。");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (status == "history")
            {
                History.Clear();
                foreach (var f in data) History.Add(f);
            }
            else
            {
                Findings.Clear();
                foreach (var f in data) Findings.Add(f);
                ActiveCount = data.Count;
            }
            sw.Stop();
            RenderMs = sw.ElapsedMilliseconds;
            _apiClient.SetTiming(_apiClient.LastTiming.NetworkMs, _apiClient.LastTiming.ServerMs,
                _apiClient.LastTiming.DbMs, sw.ElapsedMilliseconds);
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

    /// <summary>“不再提醒”:仅标记服务端 ignored,不做任何其他动作。</summary>
    [RelayCommand]
    private async Task IgnoreAsync(FindingDto finding)
    {
        var ok = await _apiClient.IgnoreFindingAsync(finding.Id);
        if (ok)
            Findings.Remove(finding);
        else
            SetError("操作失败,服务未响应。");
    }

    /// <summary>保存规则表(全量替换;下一分钟评估即按新阈值执行)。</summary>
    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        IsLoading = true;
        ClearError();
        SaveMessage = string.Empty;
        try
        {
            var ok = await _apiClient.SaveRulesAsync(new AdvisoryRulesDto(Rules.ToList()));
            SaveMessage = ok ? "✔ 已保存,下一分钟评估生效。" : "✘ 保存失败,服务未响应。";
        }
        catch (Exception ex)
        {
            SaveMessage = $"✘ 保存失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
