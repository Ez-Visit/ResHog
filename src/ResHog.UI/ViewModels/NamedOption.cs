namespace ResHog.UI.ViewModels;

/// <summary>
/// A selectable option with a machine-readable value and a human-readable display label.
/// Used for ComboBoxes where the selected value must remain a code (e.g. "cpu")
/// while the user sees a localized label (e.g. "CPU").
/// </summary>
public sealed class NamedOption
{
    public string Value { get; }
    public string Display { get; }

    public NamedOption(string value, string display)
    {
        Value = value;
        Display = display;
    }
}
