using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using DbgX.Interfaces;
using DbgX.Util;

namespace WinDbgBridge;

[RibbonTabGroupExtensionMetadata("ViewRibbonTab", "Windows", 5), Export(typeof(IDbgRibbonTabGroupExtension))]
public class BridgeButtonViewModel : IDbgRibbonTabGroupExtension
{
    [Import]
    private IDbgToolWindowManager _toolWindowManager = null!;

    public BridgeButtonViewModel()
    {
        ShowCommand = new DelegateCommand(Show);
    }

    public DelegateCommand ShowCommand { get; }

    public IEnumerable<FrameworkElement> Controls => new[] { new BridgeButton(this) };

    private void Show()
    {
        _toolWindowManager.OpenToolWindow("WinDbgBridge");
    }
}
