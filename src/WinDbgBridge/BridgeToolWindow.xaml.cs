namespace WinDbgBridge;

public partial class BridgeToolWindow
{
    public BridgeToolWindow(BridgeService bridgeService)
    {
        DataContext = bridgeService;
        InitializeComponent();
    }
}
