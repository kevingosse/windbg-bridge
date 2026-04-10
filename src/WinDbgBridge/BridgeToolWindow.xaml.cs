using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinDbgBridge;

public partial class BridgeToolWindow
{
    public BridgeToolWindow(BridgeService bridgeService)
    {
        DataContext = bridgeService;
        InitializeComponent();
    }

    private void ActivityLogListBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (sender is not ListBox listBox || listBox.SelectedItems.Count == 0)
        {
            return;
        }

        string selectedText = string.Join(
            Environment.NewLine,
            listBox.Items
                .Cast<object>()
                .Where(item => listBox.SelectedItems.Contains(item))
                .Select(item => item?.ToString() ?? string.Empty));

        Clipboard.SetText(selectedText);
        e.Handled = true;
    }
}
