using System.ComponentModel.Composition;
using System.Windows;
using DbgX.Interfaces;

namespace WinDbgBridge;

[NamedPartMetadata("WinDbgBridge"), Export(typeof(IDbgToolWindow))]
public class BridgeToolWindowViewModel : IDbgToolWindow
{
    [Import]
    private BridgeService _bridgeService = null!;

    public FrameworkElement GetToolWindowView(object parameter)
    {
        return new BridgeToolWindow(_bridgeService);
    }
}
