using Avalonia.Controls;
using Avalonia.Input;
using ResHog.UI.ViewModels;

namespace ResHog.UI.Views;

public partial class TrendView : UserControl
{
    public TrendView()
    {
        InitializeComponent();

        // Wire up the AutoCompleteBox so pressing Enter triggers the query.
        // This is done in code-behind because the control itself does not expose
        // a command-friendly KeyDown event in XAML in a MVVM-friendly way.
        if (this.FindControl<AutoCompleteBox>("ProcessFilter") is { } filter)
        {
            filter.KeyDown += OnProcessFilterKeyDown;
        }
    }

    private void OnProcessFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TrendViewModel vm)
        {
            vm.LoadTrendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
