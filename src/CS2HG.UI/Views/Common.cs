using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace CS2HG.UI.Views;

/// <summary>True when the bound value's string equals the converter parameter (used for nav highlighting).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is ViewModels.HomeViewModel vm && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await vm.DropFilesAsync(files);
        }
    }
}

public partial class AnalysisView : UserControl
{
    public AnalysisView() => InitializeComponent();
}

public partial class HighlightsView : UserControl
{
    public HighlightsView() => InitializeComponent();
}

public partial class ExportView : UserControl
{
    public ExportView() => InitializeComponent();
}

public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();
}

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
