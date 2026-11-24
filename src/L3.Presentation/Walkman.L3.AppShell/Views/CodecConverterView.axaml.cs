using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class CodecConverterView : UserControl
{
    private AudioTrackItem? _lastClickedConvTrack;

    public CodecConverterView()
    {
        InitializeComponent();
        ViewInteractionHelpers.AttachSearchFocus(this, ConvSearchBox, id => id == ModuleIds.Converter);
        ViewInteractionHelpers.AttachExplorerAudioDrop(ConvSourceDropPanel, forConverter: true);
    }

    private static AudioTrackItem? UnwrapTrack(object? sender) => (sender as Control)?.DataContext switch
    {
        TrackListEntry { Track: { } t } => t,
        AudioTrackItem t => t,
        _ => null
    };

    private void OnInputRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (UnwrapTrack(sender) is { } track && DataContext is MainWindowViewModel vm)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _lastClickedConvTrack is not null)
            {
                vm.SelectTrackRange(vm.ConverterSourceEntries, _lastClickedConvTrack, track, isDestSelection: false, targetState: true);
                _lastClickedConvTrack = track;
                e.Handled = true;
                return;
            }

            _lastClickedConvTrack = track;
            vm.ToggleTrackSelectionAndRefresh(track);
        }
    }

    private void OnInputCheckBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (UnwrapTrack(sender) is { } track)
            {
                _lastClickedConvTrack = track;
            }
            vm.NotifySelectionChanged();
        }
        e.Handled = true;
    }

    private void OnPreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (UnwrapTrack(sender) is { } track && DataContext is MainWindowViewModel vm)
        {
            vm.PlayOrToggleTrack(track);
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
