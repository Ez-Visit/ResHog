using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ResHog.Shared.Dtos;

namespace ResHog.UI.Services;

/// <summary>
/// HTTP client that calls the ResHog backend API (localhost:5180).
/// All methods return null on failure — callers should check for null
/// and display appropriate "service offline" UI.
/// </summary>
public sealed class MonitorApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public string BaseUrl { get; }

    /// <summary>
    /// Milliseconds consumed by the most recent successful API call.
    /// Updated automatically by every public API method.
    /// </summary>
    public long LastResponseTimeMs { get; private set; }

    public MonitorApiClient()
    {
        BaseUrl = "http://127.0.0.1:5180";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Quick health check — returns false if service is unreachable.
    /// </summary>
    public async Task<bool> IsServiceOnlineAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = await _httpClient.GetAsync("/api/health");
            if (resp.IsSuccessStatusCode)
                LastResponseTimeMs = sw.ElapsedMilliseconds;
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<HealthDto?> GetHealthAsync()
    {
        return await GetAsync<HealthDto>("/api/health");
    }

    public async Task<DashboardDto?> GetDashboardAsync()
    {
        return await GetAsync<DashboardDto>("/api/dashboard");
    }

    public async Task<List<TopNResultDto>?> GetTopNAsync(string metric, int limit, string range)
    {
        return await GetAsync<List<TopNResultDto>>(
            $"/api/topn?metric={metric}&limit={limit}&range={range}");
    }

    public async Task<List<TrendPointDto>?> GetTrendAsync(string process, string metric, string range)
    {
        var encoded = Uri.EscapeDataString(process);
        return await GetAsync<List<TrendPointDto>>(
            $"/api/trend?process={encoded}&metric={metric}&range={range}");
    }

    public async Task<List<AlertDto>?> GetAlertsAsync(string range = "24h", string? severity = null)
    {
        var url = $"/api/alerts?range={range}";
        if (!string.IsNullOrEmpty(severity))
            url += $"&severity={severity}";
        return await GetAsync<List<AlertDto>>(url);
    }

    public async Task<List<string>?> GetProcessNamesAsync()
    {
        return await GetAsync<List<string>>("/api/processes");
    }

    public async Task<ProcessDetailDto?> GetProcessDetailAsync(string name, string range = "24h")
    {
        var encoded = Uri.EscapeDataString(name);
        return await GetAsync<ProcessDetailDto>(
            $"/api/process/{encoded}?range={range}");
    }

    public async Task<List<ProcessInfoDto>?> SearchProcessesAsync(string query)
    {
        return await PostAsync<List<ProcessInfoDto>>("/api/processes/search",
            new ProcessSearchRequestDto(query));
    }

    public async Task<KillProcessResponseDto?> KillProcessAsync(int pid)
    {
        return await PostAsync<KillProcessResponseDto>("/api/processes/kill",
            new KillProcessRequestDto(pid));
    }

    private async Task<T?> PostAsync<T>(string url, object body) where T : class
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var typeInfo = MonitorJsonContext.Default.GetTypeInfo(body.GetType());
            var json = typeInfo != null
                ? JsonSerializer.Serialize(body, typeInfo)
                : JsonSerializer.Serialize(body);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
                return null;

            var respJson = await resp.Content.ReadAsStringAsync();
            var respTypeInfo = MonitorJsonContext.Default.GetTypeInfo(typeof(T));
            if (respTypeInfo == null) return null;

            var result = JsonSerializer.Deserialize(respJson, respTypeInfo) as T;
            LastResponseTimeMs = sw.ElapsedMilliseconds;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task<T?> GetAsync<T>(string url) where T : class
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = await _httpClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync();
            var typeInfo = MonitorJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfo == null)
                return null;

            var result = JsonSerializer.Deserialize(json, typeInfo) as T;
            LastResponseTimeMs = sw.ElapsedMilliseconds;
            return result;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
