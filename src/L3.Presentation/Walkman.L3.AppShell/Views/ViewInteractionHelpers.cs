using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell.Views;

internal static class ViewInteractionHelpers
{
    public static void AttachSearchFocus(UserControl control, TextBox searchBox, Func<string?, bool> matchesModule)
    {
        MainWindowViewModel? wiredVm = null;

        void Handler(string? moduleId)
        {
            if (matchesModule(moduleId))
            {
                searchBox.Focus();
                searchBox.SelectAll();
            }
        }

        control.DataContextChanged += (_, _) =>
        {
            if (wiredVm is not null)
            {
                wiredVm.FocusSearchRequested -= Handler;
            }

            wiredVm = control.DataContext as MainWindowViewModel;
            if (wiredVm is not null)
            {
                wiredVm.FocusSearchRequested += Handler;
            }
        };
    }

    public static void AttachExplorerAudioDrop(Control target, bool forConverter)
    {
        DragDrop.SetAllowDrop(target, true);

        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        });

        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (target.DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            var items = e.DataTransfer.TryGetFiles();
            if (items is null)
            {
                return;
            }

            var localPaths = items
                .Select(i => i.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToArray();

            if (localPaths.Length > 0)
            {
                vm.ImportDroppedFiles(localPaths, forConverter);
                e.Handled = true;
            }
        });
    }

    public static void ToggleFolderPreservingScroll(Control sender, MainWindowViewModel vm)
    {
        if (sender.DataContext is not TrackListEntry entry || string.IsNullOrWhiteSpace(entry.HeaderKey))
        {
            return;
        }

        var listBox = sender.FindAncestorOfType<ListBox>();
        var scrollViewer = listBox?.FindDescendantOfType<ScrollViewer>();
        double savedY = scrollViewer?.Offset.Y ?? 0;

        vm.ToggleFolderExpandedCommand.Execute(entry.HeaderKey);

        if (scrollViewer is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Max(0, savedY));
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
    }
}
