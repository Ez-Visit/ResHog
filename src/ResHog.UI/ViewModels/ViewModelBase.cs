using CommunityToolkit.Mvvm.ComponentModel;

namespace ResHog.UI.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    // --- API & rendering timing (set by each ViewModel after its own API calls) ---
    /// <summary>Client-measured network round-trip ms for the most recent call.</summary>
    [ObservableProperty] private long _apiMs;

    /// <summary>Server-side processing ms from X-Processing-Time-Ms header (0 if absent).</summary>
    [ObservableProperty] private long _serverMs;

    /// <summary>SQL query ms from X-Db-Query-Time-Ms header (0 if absent).</summary>
    [ObservableProperty] private long _dbMs;

    /// <summary>Time spent updating observable properties after receiving API data.</summary>
    [ObservableProperty] private long _renderMs;

    protected void ClearError() => ErrorMessage = null;

    protected void SetError(string message)
    {
        ErrorMessage = message;
        IsLoading = false;
    }
}
