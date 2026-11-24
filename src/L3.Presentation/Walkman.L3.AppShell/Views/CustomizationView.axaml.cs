using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class CustomizationView : UserControl
{
    public CustomizationView()
    {
        InitializeComponent();
        ViewInteractionHelpers.AttachSearchFocus(
            this,
            SubSearchBox,
            id => id is ModuleIds.Customization or ModuleIds.CustomSubtitles or ModuleIds.CustomTags);
    }

    private static AudioTrackItem? UnwrapTrack(object? sender) => (sender as Control)?.DataContext switch
    {
        TrackListEntry { Track: { } t } => t,
        AudioTrackItem t => t,
        _ => null
    };

    private void OnFolderHeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control ctrl && DataContext is MainWindowViewModel vm)
        {
            ViewInteractionHelpers.ToggleFolderPreservingScroll(ctrl, vm);
        }
    }

    private void OnSubtitleTrackPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (UnwrapTrack(sender) is { } track && DataContext is MainWindowViewModel vm)
        {
            vm.SelectedSubtitleTrack = track;
        }
    }

    private void OnTagTrackPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (UnwrapTrack(sender) is { } track && DataContext is MainWindowViewModel vm)
        {
            vm.SelectedTagTrack = track;
        }
    }

    private void OnTagTrackCheckBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.NotifyTagSelectionChanged();
        }
        e.Handled = true;
    }

    private void OnPreviewSelectedSubtitleTrack(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedSubtitleTrack is { } trk)
        {
            vm.PlayOrToggleTrack(trk);
        }
    }
}
