using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ResHog.Shared.Dtos;

namespace ResHog.UI.Services;

/// <summary>
/// Timing breakdown for a single API call.
/// NetworkMs / ServerMs / DbMs are populated automatically by MonitorApiClient
/// from Stopwatch and response headers. RenderMs is set separately by each
/// ViewModel after updating observable properties.
/// </summary>
public record ApiCallTiming(
    long NetworkMs,      // Wall-clock time from client perspective (Stopwatch)
    long ServerMs,       // From X-Processing-Time-Ms response header (0 if absent)
    long DbMs,           // From X-Db-Query-Time-Ms response header (0 if absent)
    long RenderMs = 0    // Time spent updating UI properties after API data received
)
{
    public static readonly ApiCallTiming Zero = new(0, 0, 0);

    public string NetworkText => $"{NetworkMs}ms";
    public string ServerText => ServerMs > 0 ? $"{ServerMs}ms" : "-";
    public string DbText => DbMs > 0 ? $"{DbMs}ms" : "-";

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add($"API {NetworkMs}ms");
            if (DbMs > 0) parts.Add($"DB {DbMs}ms");
            if (RenderMs > 0) parts.Add($"渲染 {RenderMs}ms");
            return string.Join(" | ", parts);
        }
    }
}

/// <summary>
/// HTTP client that calls the ResHog backend API (localhost:5180).
/// All methods return null on failure — callers should check for null
/// and display appropriate "service offline" UI.
///
/// Timing: every successful call updates <see cref="LastTiming"/> so the
/// caller can snapshot timing right after await to get per-endpoint metrics.
/// </summary>
public sealed class MonitorApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public string BaseUrl { get; }

    /// <summary>
    /// Timing breakdown of the most recent successful API call.
    /// Each ViewModel snapshots this right after its own await to get
    /// per-endpoint timing that cannot be overwritten by another call.
    /// </summary>
    public ApiCallTiming LastTiming { get; private set; } = ApiCallTiming.Zero;

    /// <summary>
    /// Client-measured wall-clock ms of the most recent successful API call.
    /// </summary>
    public long LastResponseTimeMs => LastTiming.NetworkMs;

    /// <summary>
    /// Fired every time LastTiming is updated — after every successful API call
    /// or ViewModel render-timing push. MainViewModel subscribes to update the
    /// status-bar display without polling.
    /// </summary>
    public event Action<ApiCallTiming>? TimingUpdated;

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
            {
                ParseTimingHeaders(resp, sw.ElapsedMilliseconds);
            }
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

    private void ParseTimingHeaders(System.Net.Http.HttpResponseMessage resp, long networkMs)
    {
        var serverMs = 0L;
        var dbMs = 0L;
        if (resp.Headers.TryGetValues("X-Processing-Time-Ms", out var srv))
            long.TryParse(srv.FirstOrDefault(), out serverMs);
        if (resp.Headers.TryGetValues("X-Db-Query-Time-Ms", out var db))
            long.TryParse(db.FirstOrDefault(), out dbMs);
        LastTiming = new ApiCallTiming(networkMs, serverMs, dbMs);
        TimingUpdated?.Invoke(LastTiming);
    }

    /// <summary>
    /// Called by ViewModels after they measure rendering time,
    /// to attach RenderMs to the most recent API timing snapshot.
    /// </summary>
    public void SetTiming(long networkMs, long serverMs, long dbMs, long renderMs)
    {
        LastTiming = new ApiCallTiming(networkMs, serverMs, dbMs, renderMs);
        TimingUpdated?.Invoke(LastTiming);
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
            ParseTimingHeaders(resp, sw.ElapsedMilliseconds);
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
            ParseTimingHeaders(resp, sw.ElapsedMilliseconds);
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
