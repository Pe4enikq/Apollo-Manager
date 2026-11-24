using Avalonia.Controls;
using Avalonia.Interactivity;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class FloatingBottomDockView : UserControl
{
    public FloatingBottomDockView()
    {
        InitializeComponent();
    }

    private void OnScrubberSeekRequested(double seconds)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SeekPlayer(seconds);
        }
    }

    private async void OnCopyPlayerTrackClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is MainWindowViewModel { PlayerTrack: { } track } &&
                TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                string text = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Title} — {track.Artist}";
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Clipboard contention on Windows can throw COMException; ignore safely.
        }
    }
}
