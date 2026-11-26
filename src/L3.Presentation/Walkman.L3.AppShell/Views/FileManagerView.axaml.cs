using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

public partial class FileManagerView : UserControl
{
    private Point? _sourceDragStartPoint;
    private AudioTrackItem? _sourceDragTrack;
    private bool _sourceDragStarted;
    private Point? _destDragStartPoint;
    private AudioTrackItem? _destDragTrack;
    private bool _destDragStarted;
    private Point? _destFolderDragStartPoint;
    private string? _destFolderDragName;
    private bool _destFolderDragStarted;
    private AudioTrackItem? _lastClickedSourceTrack;
    private AudioTrackItem? _lastClickedDestTrack;
    private TrackListEntry? _activeDropTargetEntry;
    private readonly Stopwatch _autoScrollClock = Stopwatch.StartNew();
    private long _lastAutoScrollMs;

    public FileManagerView()
    {
        InitializeComponent();
        ViewInteractionHelpers.AttachSearchFocus(this, LeftSearchBox, id => id == ModuleIds.FileManager);
        ViewInteractionHelpers.AttachExplorerAudioDrop(SourceDropPanel, forConverter: false);
        DragDrop.SetAllowDrop(DestListBox, true);
        DestListBox.AddHandler(DragDrop.DragOverEvent, OnDestDragOver);
        DestListBox.AddHandler(DragDrop.DragLeaveEvent, OnDestDragLeave);
        DestListBox.AddHandler(DragDrop.DropEvent, OnDestDrop);
        DestListBox.AddHandler(PointerPressedEvent, OnDestFolderHeaderTunnelPressed, RoutingStrategies.Tunnel);
    }

    private void SetDropTargetEntry(TrackListEntry? entry, bool isFolderOver = false)
    {
        if (_activeDropTargetEntry is not null)
        {
            _activeDropTargetEntry.IsDropTargetBefore = false;
            _activeDropTargetEntry.IsDropTargetOver = false;
        }

        _activeDropTargetEntry = entry;
        if (_activeDropTargetEntry is not null)
        {
            if (isFolderOver)
            {
                _activeDropTargetEntry.IsDropTargetOver = true;
            }
            else
            {
                _activeDropTargetEntry.IsDropTargetBefore = true;
            }
        }
    }

    private void TryAutoScrollDestList(DragEventArgs e)
    {
        long nowMs = _autoScrollClock.ElapsedMilliseconds;
        if (nowMs - _lastAutoScrollMs < 30)
        {
            return;
        }

        double y = e.GetPosition(DestListBox).Y;
        double h = DestListBox.Bounds.Height;
        double delta = 0;
        if (y < 44)
        {
            delta = -18;
        }
        else if (y > h - 44)
        {
            delta = 18;
        }

        if (delta != 0 && DestListBox.FindDescendantOfType<ScrollViewer>() is { } sv)
        {
            _lastAutoScrollMs = nowMs;
            double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            sv.Offset = new Vector(sv.Offset.X, Math.Clamp(sv.Offset.Y + delta, 0, maxY));
        }
    }

    private void OnDestDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsTransferRunning: true })
        {
            SetDropTargetEntry(null);
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

#pragma warning disable CS0618
        if (e.Data.Contains("ApolloDestCollectionName"))
        {
            e.DragEffects = DragDropEffects.Move;
            var target = FindDropTargetEntry(e.Source);
            SetDropTargetEntry(target is { IsHeader: true } ? target : null, isFolderOver: false);
            TryAutoScrollDestList(e);
            e.Handled = true;
        }
        else if (e.Data.Contains("ApolloDestTrackId"))
        {
            e.DragEffects = DragDropEffects.Move;
            var target = FindDropTargetEntry(e.Source);
            SetDropTargetEntry(target, isFolderOver: target is { IsHeader: true });
            TryAutoScrollDestList(e);
            e.Handled = true;
        }
        else if (e.Data.Contains("ApolloTrackId"))
        {
            e.DragEffects = DragDropEffects.Copy;
            var target = FindDropTargetEntry(e.Source);
            SetDropTargetEntry(target, isFolderOver: target is { IsHeader: true });
            TryAutoScrollDestList(e);
            e.Handled = true;
        }
        else
        {
            SetDropTargetEntry(null);
            e.DragEffects = DragDropEffects.None;
        }
#pragma warning restore CS0618
    }

    private void OnDestDragLeave(object? sender, DragEventArgs e)
    {
        SetDropTargetEntry(null);
    }

    private static TrackListEntry? FindDropTargetEntry(object? eventSource)
    {
        for (var v = eventSource as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: TrackListEntry entry })
            {
                return entry;
            }
        }

        return null;
    }

    private void OnDestDrop(object? sender, DragEventArgs e)
    {
        SetDropTargetEntry(null);
        if (DataContext is not MainWindowViewModel vm || vm.IsTransferRunning)
        {
            return;
        }

        var targetEntry = FindDropTargetEntry(e.Source);

#pragma warning disable CS0618
        if (e.Data.Contains("ApolloDestCollectionName") && e.Data.Get("ApolloDestCollectionName") is string draggedCollection)
        {
            if (targetEntry is { IsHeader: true } && !string.IsNullOrWhiteSpace(targetEntry.HeaderTitle))
            {
                vm.ReorderDestCollectionBefore(draggedCollection, targetEntry.HeaderTitle);
            }
            e.Handled = true;
            return;
        }

        if (e.Data.Contains("ApolloDestTrackId") && e.Data.Get("ApolloDestTrackId") is string destTrackId)
        {
            if (targetEntry is { IsTrack: true, Track: { } targetTrack })
            {
                vm.ReorderDestTrackBefore(destTrackId, targetTrack);
            }
            else if (targetEntry is { IsHeader: true } && !string.IsNullOrWhiteSpace(targetEntry.HeaderTitle))
            {
                vm.MoveDestTrackToCollection(destTrackId, targetEntry.HeaderTitle);
            }
            e.Handled = true;
            return;
        }

        if (e.Data.Contains("ApolloTrackId") && e.Data.Get("ApolloTrackId") is string trackId)
#pragma warning restore CS0618
        {
            var src = vm.FileManagerSourceTracks.FirstOrDefault(t => t.Id == trackId);
            if (src is not null)
            {
                if (targetEntry is { IsTrack: true, Track: { } targetTrack })
                {
                    vm.StageTrackToCollectionAt(src, targetTrack.GroupName, targetTrack.Id);
                }
                else if (targetEntry is { IsHeader: true } && !string.IsNullOrWhiteSpace(targetEntry.HeaderTitle))
                {
                    vm.StageTrackToCollectionAt(src, targetEntry.HeaderTitle, null);
                }
                else
                {
                    vm.StageTrackToCollection(src, vm.FmTargetGroupName);
                }
                e.Handled = true;
            }
        }
    }

    private static AudioTrackItem? UnwrapTrack(object? sender) => (sender as Control)?.DataContext switch
    {
        TrackListEntry { Track: { } t } => t,
        AudioTrackItem t => t,
        _ => null
    };

    private void OnSourceRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (UnwrapTrack(sender) is { } track && DataContext is MainWindowViewModel vm)
        {
            if (vm.IsTransferRunning)
            {
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _lastClickedSourceTrack is not null)
            {
                vm.SelectTrackRange(vm.FileManagerSourceEntries, _lastClickedSourceTrack, track, isDestSelection: false, targetState: true);
                _lastClickedSourceTrack = track;
                _sourceDragTrack = null;
                _sourceDragStartPoint = null;
                e.Handled = true;
                return;
            }

            _sourceDragStartPoint = e.GetPosition(this);
            _sourceDragTrack = track;
            _sourceDragStarted = false;
        }
    }

    private void OnSourceRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_sourceDragTrack is null || _sourceDragStartPoint is null || _sourceDragStarted)
        {
            return;
        }

        if (DataContext is MainWindowViewModel { IsTransferRunning: true } ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _sourceDragTrack = null;
            _sourceDragStartPoint = null;
            return;
        }

        var delta = e.GetPosition(this) - _sourceDragStartPoint.Value;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _sourceDragStarted = true;
#pragma warning disable CS0618
        var data = new DataObject();
        data.Set("ApolloTrackId", _sourceDragTrack.Id);
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
        _sourceDragTrack = null;
        _sourceDragStartPoint = null;
    }

    private void OnSourceRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_sourceDragTrack is { } track && !_sourceDragStarted && DataContext is MainWindowViewModel vm)
        {
            _lastClickedSourceTrack = track;
            vm.ToggleTrackSelectionAndRefresh(track);
        }

        _sourceDragTrack = null;
        _sourceDragStartPoint = null;
        _sourceDragStarted = false;
    }

    private void OnSourceCheckBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (vm.IsTransferRunning)
            {
                e.Handled = true;
                return;
            }

            if (UnwrapTrack(sender) is { } track)
            {
                _lastClickedSourceTrack = track;
            }

            var destSv = DestListBox.FindDescendantOfType<ScrollViewer>();
            double savedDestY = destSv?.Offset.Y ?? 0;
            vm.NotifySelectionChanged();
            if (destSv is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    double maxY = Math.Max(0, destSv.Extent.Height - destSv.Viewport.Height);
                    destSv.Offset = new Vector(destSv.Offset.X, Math.Clamp(savedDestY, 0, maxY));
                }, Avalonia.Threading.DispatcherPriority.Render);
            }
        }
        e.Handled = true;
    }

    private void OnDestCheckBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (vm.IsTransferRunning)
            {
                e.Handled = true;
                return;
            }

            if (UnwrapTrack(sender) is { } track)
            {
                _lastClickedDestTrack = track;
            }
            vm.NotifyDestSelectionChanged();
        }
        e.Handled = true;
    }

    private void OnPreviewTrackClicked(object? sender, RoutedEventArgs e)
    {
        if (UnwrapTrack(sender) is { } track && DataContext is MainWindowViewModel vm)
        {
            vm.PlayOrToggleTrack(track);
        }
        e.Handled = true;
    }

    private void OnPreviewDestRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (UnwrapTrack(sender) is { } track)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
                _lastClickedDestTrack is not null &&
                DataContext is MainWindowViewModel vm &&
                !vm.IsTransferRunning &&
                !track.IsStagedPreview)
            {
                vm.SelectTrackRange(vm.FileManagerDestEntries, _lastClickedDestTrack, track, isDestSelection: true, targetState: true);
                _lastClickedDestTrack = track;
                e.Handled = true;
                return;
            }

            _lastClickedDestTrack = track;
            _destDragStartPoint = e.GetPosition(this);
            _destDragTrack = track;
            _destDragStarted = false;
        }
    }

    private void OnDestRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_destDragTrack is null || _destDragStartPoint is null || _destDragStarted)
        {
            return;
        }

        if (DataContext is MainWindowViewModel { IsTransferRunning: true })
        {
            _destDragTrack = null;
            _destDragStartPoint = null;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _destDragTrack = null;
            _destDragStartPoint = null;
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _destDragStartPoint.Value;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _destDragStarted = true;
#pragma warning disable CS0618
        var data = new DataObject();
        data.Set("ApolloDestTrackId", _destDragTrack.Id);
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        _destDragTrack = null;
        _destDragStartPoint = null;
    }

    private void OnDestRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_destDragTrack is { } track && !_destDragStarted && DataContext is MainWindowViewModel vm)
        {
            vm.PlayOrToggleTrack(track);
        }

        _destDragTrack = null;
        _destDragStartPoint = null;
        _destDragStarted = false;
    }

    private void OnFolderHeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (_destFolderDragStarted)
        {
            _destFolderDragStarted = false;
            return;
        }

        if (sender is Control ctrl && DataContext is MainWindowViewModel vm)
        {
            ViewInteractionHelpers.ToggleFolderPreservingScroll(ctrl, vm);
        }
    }

    private void OnDestFolderHeaderTunnelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            DataContext is MainWindowViewModel { IsTransferRunning: true })
        {
            return;
        }

        if (e.Source is Visual v &&
            v.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: TrackListEntry { IsHeader: true, HeaderTitle: { Length: > 0 } name } })
        {
            _destFolderDragStartPoint = e.GetPosition(this);
            _destFolderDragName = name;
            _destFolderDragStarted = false;
        }
    }

    private void OnDestFolderHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            DataContext is MainWindowViewModel { IsTransferRunning: true })
        {
            return;
        }

        if (sender is Control { DataContext: TrackListEntry { IsHeader: true, HeaderTitle: { Length: > 0 } name } })
        {
            _destFolderDragStartPoint = e.GetPosition(this);
            _destFolderDragName = name;
            _destFolderDragStarted = false;
        }
    }

    private void OnDestFolderHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_destFolderDragName is null || _destFolderDragStartPoint is null || _destFolderDragStarted)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            DataContext is MainWindowViewModel { IsTransferRunning: true })
        {
            _destFolderDragName = null;
            _destFolderDragStartPoint = null;
            return;
        }

        var delta = e.GetPosition(this) - _destFolderDragStartPoint.Value;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _destFolderDragStarted = true;
#pragma warning disable CS0618
        var data = new DataObject();
        data.Set("ApolloDestCollectionName", _destFolderDragName);
        _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        _destFolderDragName = null;
        _destFolderDragStartPoint = null;
    }

    private void OnDestFolderHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _destFolderDragName = null;
        _destFolderDragStartPoint = null;
    }
}
