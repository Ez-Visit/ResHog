using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ResHog.UI.Controls;

/// <summary>
/// A self-drawn horizontal progress bar.
/// Used in dashboard overview cards and inline process bars.
/// </summary>
public class UsageBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Value));

    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(MaxValue), defaultValue: 100);

    public static readonly StyledProperty<IBrush> BarColorProperty =
        AvaloniaProperty.Register<UsageBar, IBrush>(nameof(BarColor), defaultValue: Brushes.OrangeRed);

    public static readonly StyledProperty<string> DisplayTextProperty =
        AvaloniaProperty.Register<UsageBar, string>(nameof(DisplayText), defaultValue: "");

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public IBrush BarColor
    {
        get => GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public string DisplayText
    {
        get => GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    static UsageBar()
    {
        AffectsRender<UsageBar>(ValueProperty, MaxValueProperty, BarColorProperty, DisplayTextProperty);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < 2)
            return;

        // Background
        var bgBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
        var radius = Math.Min(h / 2, 4);
        context.DrawRectangle(bgBrush, null, new Rect(0, 0, w, h), radius, radius);

        // Bar fill
        var ratio = MaxValue > 0 ? Math.Clamp(Value / MaxValue, 0, 1) : 0;
        if (ratio > 0)
        {
            var barWidth = w * ratio;
            context.DrawRectangle(BarColor, null, new Rect(0, 0, barWidth, h), radius, radius);
        }

        // Display text overlay
        if (!string.IsNullOrEmpty(DisplayText))
        {
            var font = new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold);
            var brush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            var text = new FormattedText(DisplayText, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, font, 11, brush);
            var x = w - text.Width - 6;
            var y = (h - text.Height) / 2;
            context.DrawText(text, new Point(x, y));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(availableSize.Width, Math.Max(availableSize.Height, 22));
    }
}
