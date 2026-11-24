using Avalonia.Controls;
using Avalonia.Interactivity;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class DeviceSystemView : UserControl
{
    public DeviceSystemView()
    {
        InitializeComponent();
    }

    private void OnPairCardButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WalkmanDeviceCardViewModel card } && DataContext is MainWindowViewModel vm)
        {
            vm.SelectDevice(card.IsConnected ? null : card.Profile);
        }
    }
}
