namespace WinDbgBridge;

public partial class BridgeButton
{
    public BridgeButton(BridgeButtonViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
