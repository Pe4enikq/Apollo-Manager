using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class LibraryPlayerView : UserControl
{
    public LibraryPlayerView()
    {
        InitializeComponent();
        ViewInteractionHelpers.AttachSearchFocus(this, LibSearchBox, id => id == ModuleIds.Library);
        ViewInteractionHelpers.AttachExplorerAudioDrop(this, forConverter: false);
    }

    private void OnTrackRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var track = (sender as Control)?.DataContext switch
        {
            TrackListEntry { Track: { } t } => t,
            AudioTrackItem t => t,
            _ => null
        };
        if (track is not null && DataContext is MainWindowViewModel vm)
        {
            vm.PlayOrToggleTrack(track);
        }
    }

    private void OnPlayButtonClicked(object? sender, RoutedEventArgs e)
    {
        var track = (sender as Control)?.DataContext switch
        {
            TrackListEntry { Track: { } t } => t,
            AudioTrackItem t => t,
            _ => null
        };
        if (track is not null && DataContext is MainWindowViewModel vm)
        {
            vm.PlayOrToggleTrack(track);
        }
        e.Handled = true;
    }

    private void OnRatingStarClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tagStr } ctrl
            && int.TryParse(tagStr, out int stars)
            && DataContext is MainWindowViewModel vm)
        {
            var track = ctrl.DataContext switch
            {
                TrackListEntry { Track: { } t } => t,
                AudioTrackItem t => t,
                _ => null
            };
            if (track is not null)
            {
                vm.SetTrackRating(track, stars);
            }
        }

        e.Handled = true;
    }

    private void OnFolderHeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control ctrl && DataContext is MainWindowViewModel vm)
        {
            ViewInteractionHelpers.ToggleFolderPreservingScroll(ctrl, vm);
        }
    }
}
