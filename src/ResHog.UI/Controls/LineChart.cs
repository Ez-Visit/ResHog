using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.Specialized;
using ResHog.Shared.Dtos;

namespace ResHog.UI.Controls;

/// <summary>
/// A self-drawn line chart for trend visualization.
/// Renders axes, grid lines, and a polyline connecting data points.
/// Zero third-party dependencies — trim-safe.
/// </summary>
public class LineChart : Control
{
    public static readonly StyledProperty<IList<TrendPointDto>?> DataPointsProperty =
        AvaloniaProperty.Register<LineChart, IList<TrendPointDto>?>(nameof(DataPoints));

    public static readonly StyledProperty<IBrush> LineColorProperty =
        AvaloniaProperty.Register<LineChart, IBrush>(nameof(LineColor), defaultValue: Brushes.OrangeRed);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<LineChart, string>(nameof(Unit), defaultValue: "");

    public static readonly StyledProperty<double> ManualMaxValueProperty =
        AvaloniaProperty.Register<LineChart, double>(nameof(ManualMaxValue), defaultValue: 0);

    public IList<TrendPointDto>? DataPoints
    {
        get => GetValue(DataPointsProperty);
        set => SetValue(DataPointsProperty, value);
    }

    public IBrush LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>
    /// If > 0, use this as the Y-axis maximum instead of auto-scaling.
    /// </summary>
    public double ManualMaxValue
    {
        get => GetValue(ManualMaxValueProperty);
        set => SetValue(ManualMaxValueProperty, value);
    }

    static LineChart()
    {
        AffectsRender<LineChart>(
            DataPointsProperty, LineColorProperty, UnitProperty, ManualMaxValueProperty);
    }

    private NotifyCollectionChangedEventHandler? _collectionHandler;
    private INotifyCollectionChanged? _subscribedCollection;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DataPointsProperty)
        {
            // Unsubscribe from old collection
            if (_subscribedCollection != null && _collectionHandler != null)
                _subscribedCollection.CollectionChanged -= _collectionHandler;

            _subscribedCollection = null;
            _collectionHandler = null;

            // Subscribe to new collection's CollectionChanged so Clear/Add triggers re-render
            if (change.NewValue is INotifyCollectionChanged newCollection)
            {
                _subscribedCollection = newCollection;
                _collectionHandler = (s, e) => InvalidateVisual();
                newCollection.CollectionChanged += _collectionHandler;
            }
        }
    }


    private const double LeftPadding = 55;
    private const double RightPadding = 15;
    private const double TopPadding = 15;
    private const double BottomPadding = 35;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width < 80 || height < 60)
            return;

        var plotLeft = LeftPadding;
        var plotTop = TopPadding;
        var plotRight = width - RightPadding;
        var plotBottom = height - BottomPadding;
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;

        // Draw background
        context.DrawRectangle(Brushes.White, null, new Rect(plotLeft, plotTop, plotWidth, plotHeight));

        var points = DataPoints;
        if (points == null || points.Count == 0)
        {
            DrawNoDataMessage(context, plotLeft, plotTop, plotWidth, plotHeight);
            DrawAxes(context, plotLeft, plotTop, plotRight, plotBottom, 0, "", 0);
            return;
        }

        // Calculate Y range
        var maxVal = ManualMaxValue > 0 ? ManualMaxValue : Math.Max(points.Max(p => p.Value), 0.1);
        // Round up to a nice number
        maxVal = NiceMax(maxVal);

        // Draw axes and grid
        DrawAxes(context, plotLeft, plotTop, plotRight, plotBottom, maxVal, Unit, points.Count);

        // Draw the polyline
        if (points.Count >= 2)
        {
            var stepX = plotWidth / Math.Max(points.Count - 1, 1);
            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                var firstX = plotLeft;
                var firstY = plotBottom - (points[0].Value / maxVal) * plotHeight;
                ctx.BeginFigure(new Point(firstX, firstY), false);

                for (int i = 1; i < points.Count; i++)
                {
                    var x = plotLeft + i * stepX;
                    var y = plotBottom - (points[i].Value / maxVal) * plotHeight;
                    ctx.LineTo(new Point(x, y));
                }
                ctx.EndFigure(false);
            }

            var pen = new Pen(LineColor, 1.5);
            context.DrawGeometry(null, pen, path);

            // Fill area under the line
            var fillPath = new StreamGeometry();
            using (var ctx = fillPath.Open())
            {
                ctx.BeginFigure(new Point(plotLeft, plotBottom), true);
                for (int i = 0; i < points.Count; i++)
                {
                    var x = plotLeft + i * stepX;
                    var y = plotBottom - (points[i].Value / maxVal) * plotHeight;
                    ctx.LineTo(new Point(x, y));
                }
                ctx.LineTo(new Point(plotLeft + (points.Count - 1) * stepX, plotBottom));
                ctx.EndFigure(true);
            }

            var fillColor = LineColor;
            if (fillColor is ISolidColorBrush scb)
            {
                var fillBrush = new SolidColorBrush(scb.Color, 0.15);
                context.DrawGeometry(fillBrush, null, fillPath);
            }
        }

        // Draw data points
        if (points.Count <= 200)
        {
            var stepX = plotWidth / Math.Max(points.Count - 1, 1);
            var pointPen = new Pen(LineColor, 1);
            var pointBrush = LineColor;

            for (int i = 0; i < points.Count; i++)
            {
                var x = plotLeft + i * stepX;
                var y = plotBottom - (points[i].Value / maxVal) * plotHeight;
                context.DrawEllipse(pointBrush, pointPen, new Point(x, y), 2, 2);
            }
        }
    }

    private void DrawAxes(DrawingContext context, double left, double top, double right, double bottom,
        double maxVal, string unit, int pointCount)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), 0.5);
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        var labelFont = new Typeface("Inter", FontStyle.Normal, FontWeight.Normal);

        // Y-axis grid lines and labels (5 steps)
        int ySteps = 5;
        for (int i = 0; i <= ySteps; i++)
        {
            var y = top + (bottom - top) * i / ySteps;
            var val = maxVal * (ySteps - i) / ySteps;

            // Grid line
            context.DrawLine(gridPen, new Point(left, y), new Point(right, y));

            // Y label
            var labelText = FormatValue(val, unit);
            var formattedText = new FormattedText(labelText, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, labelFont, 10, labelBrush);
            context.DrawText(formattedText, new Point(left - formattedText.Width - 5, y - formattedText.Height / 2));
        }

        // X-axis labels (up to 6 ticks)
        int xTicks = Math.Min(pointCount, 6);
        if (xTicks > 0)
        {
            for (int i = 0; i < xTicks; i++)
            {
                var ratio = xTicks == 1 ? 0 : (double)i / (xTicks - 1);
                var x = left + (right - left) * ratio;

                // Tick mark
                context.DrawLine(axisPen, new Point(x, bottom), new Point(x, bottom + 4));

                // Label
                string label = "";
                if (DataPoints != null && DataPoints.Count > 0)
                {
                    var idx = (int)(ratio * (DataPoints.Count - 1));
                    if (idx >= 0 && idx < DataPoints.Count)
                    {
                        var ts = DataPoints[idx].Timestamp;
                        label = FormatTimestamp(ts);
                    }
                }

                if (!string.IsNullOrEmpty(label))
                {
                    var formattedText = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, labelFont, 9, labelBrush);
                    context.DrawText(formattedText, new Point(x - formattedText.Width / 2, bottom + 6));
                }
            }
        }

        // Axis lines
        context.DrawLine(axisPen, new Point(left, top), new Point(left, bottom));
        context.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));

        // Y-axis unit label (top of the axis)
        if (!string.IsNullOrEmpty(unit))
        {
            var unitLabel = new FormattedText(unit, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, labelFont, 10, new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
            context.DrawText(unitLabel, new Point(left + 4, top - 4));
        }
    }

    private void DrawNoDataMessage(DrawingContext context, double left, double top, double width, double height)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        var font = new Typeface("Inter", FontStyle.Normal, FontWeight.Normal);
        var text = new FormattedText("暂无数据", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, font, 14, brush);
        var x = left + (width - text.Width) / 2;
        var y = top + (height - text.Height) / 2;
        context.DrawText(text, new Point(x, y));
    }

    private static double NiceMax(double value)
    {
        if (value <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var norm = value / mag;
        double niceNorm;
        if (norm <= 1) niceNorm = 1;
        else if (norm <= 2) niceNorm = 2;
        else if (norm <= 5) niceNorm = 5;
        else niceNorm = 10;
        return niceNorm * mag;
    }

    private static string FormatValue(double val, string unit)
    {
        if (string.IsNullOrEmpty(unit))
            return val.ToString("F0");

        return unit switch
        {
            "%" => $"{val:F0}%",
            "MB" => val >= 1024 ? $"{val / 1024:F1}GB" : $"{val:F0}MB",
            "MB/s" => $"{val:F1} MB/s",
            _ => $"{val:F1} {unit}"
        };
    }

    private static string FormatTimestamp(string ts)
    {
        // Input: "2026-07-06T17:11:39Z"
        // Output: "17:11" or "07-06"
        if (string.IsNullOrEmpty(ts) || ts.Length < 16)
            return ts;

        // Extract time part
        if (ts.Contains('T'))
        {
            var timePart = ts.Substring(ts.IndexOf('T') + 1);
            if (timePart.Length >= 5)
                return timePart.Substring(0, 5);
        }
        return ts.Length >= 16 ? ts.Substring(5, 11) : ts;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(availableSize.Width, Math.Max(availableSize.Height, 150));
    }
}
