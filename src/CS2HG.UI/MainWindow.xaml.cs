using System.Windows;
using CS2HG.UI.ViewModels;

namespace CS2HG.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Keyboard and UI Automation check the radio button without a click; navigate on Checked too.</summary>
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        // Only after load: radio buttons can raise Checked while the window builds its visual tree.
        if (!IsLoaded) return;
        if (e.OriginalSource is System.Windows.Controls.RadioButton { CommandParameter: string page } && DataContext is MainViewModel vm &&
            vm.CurrentPageKey != page)
        {
            vm.NavigateCommand.Execute(page);
        }
    }

    /// <summary>Ctrl+V anywhere (outside text boxes) with a CS2 share code in the clipboard opens that match.</summary>
    protected override async void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != System.Windows.Input.Key.V || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (DataContext is not MainViewModel vm || !Clipboard.ContainsText()) return;
        if (Core.Models.ShareCode.Find(Clipboard.GetText()) is not { } code) return;
        e.Handled = true;
        vm.Home.ShareCodeText = code;
        await vm.OpenShareCodeAsync(code);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await vm.Home.DropFilesAsync(files);
    }
}
