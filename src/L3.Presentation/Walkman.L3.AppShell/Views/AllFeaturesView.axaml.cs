using Avalonia.Controls;
using Avalonia.Interactivity;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class AllFeaturesView : UserControl
{
    public AllFeaturesView()
    {
        InitializeComponent();
    }

    private void OnDisconnectChipClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectDevice(null);
        }
    }

    private void OnSelectDeviceChipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WalkmanDeviceCardViewModel card } && DataContext is MainWindowViewModel vm)
        {
            vm.SelectDevice(card.Profile);
        }
    }

    private void OnFeatureCardClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FeatureCardItem item } && DataContext is MainWindowViewModel vm)
        {
            vm.OpenModule(item.Id);
        }
    }
}
