using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ResHog.UI.Converters;

/// <summary>
/// Converts metric codes to display names: cpu → CPU, memory → 内存, etc.
/// Also accepts the server-side English display names.
/// </summary>
public class MetricNameConverter : IValueConverter
{
    public static readonly MetricNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return value;

        return s.ToLowerInvariant() switch
        {
            "cpu" or "totalcpu" or "total_cpu" => "CPU",
            "memory" or "mem" => "内存",
            "io" => "磁盘读写",
            "io_read" or "disk_read" or "io read" or "i/o read" => "磁盘读取",
            "io_write" or "disk_write" or "io write" or "i/o write" => "磁盘写入",
            "threads" or "thread" or "thread_count" => "线程数",
            "handles" or "handle" or "handle_count" => "句柄数",
            "network_recv" or "net_recv" or "network receive" => "网络接收",
            "network_sent" or "net_sent" or "network send" => "网络发送",
            _ => s
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts range codes to display names: 1h → 最近 1 小时, 24h → 最近 24 小时, etc.
/// </summary>
public class RangeNameConverter : IValueConverter
{
    public static readonly RangeNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return value;

        return s.ToLowerInvariant() switch
        {
            "1h" => "最近 1 小时",
            "24h" => "最近 24 小时",
            "7d" => "最近 7 天",
            "30d" => "最近 30 天",
            "90d" => "最近 90 天",
            _ => s
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts severity strings to colors: critical → red, warning → orange, info → blue.
/// </summary>
public class SeverityToBrushConverter : IValueConverter
{
    public static readonly SeverityToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

        return s.ToLowerInvariant() switch
        {
            "critical" => new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)),
            "warning" => new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27)),
            "info" => new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),
            _ => new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts severity strings to display icons: critical → 🔴, warning → 🟡, info → 🔵.
/// </summary>
public class SeverityToIconConverter : IValueConverter
{
    public static readonly SeverityToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return "⚪";

        return s.ToLowerInvariant() switch
        {
            "critical" => "🔴",
            "warning" => "🟡",
            "info" => "🔵",
            _ => "⚪"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a boolean (online/offline) to a color brush.
/// </summary>
public class BoolToStatusBrushConverter : IValueConverter
{
    public static readonly BoolToStatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)); // green

        return new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)); // gray
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts metric codes to unit strings: cpu → %, memory → MB, io_read → MB/s, etc.
/// </summary>
public class MetricToUnitConverter : IValueConverter
{
    public static readonly MetricToUnitConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return "";

        return s.ToLowerInvariant() switch
        {
            "cpu" => "%",
            "memory" => "MB",
            "io_read" or "disk_read" => "MB/s",
            "io_write" or "disk_write" => "MB/s",
            "network_recv" or "net_recv" => "KB/s",
            "network_sent" or "net_sent" => "KB/s",
            _ => ""
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts bytes (MB) to human-readable string: 1024 → "1.0 GB".
/// </summary>
public class MemoryFormatConverter : IValueConverter
{
    public static readonly MemoryFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double mb)
            return value?.ToString() ?? "—";

        if (mb >= 1024)
            return $"{mb / 1024:F1} GB";
        return $"{mb:F0} MB";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts severity strings to Chinese display names.
/// </summary>
public class SeverityNameConverter : IValueConverter
{
    public static readonly SeverityNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
            return value;

        return s.ToLowerInvariant() switch
        {
            "critical" => "严重",
            "warning" => "警告",
            "info" => "提示",
            "all" => "全部",
            _ => s
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
