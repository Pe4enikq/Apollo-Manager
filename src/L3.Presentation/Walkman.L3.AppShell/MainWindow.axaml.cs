using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Walkman.L1.DevicePoller.Interop;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;
using Walkman.L3.AppShell.ViewModels;

namespace Walkman.L3.AppShell;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly WalkmanBackendService _backend;
    private readonly string? _captureDir;
    private readonly UsbNotificationBridge _usbBridge = new();
    private readonly Stopwatch _windowClock = Stopwatch.StartNew();
    private DispatcherTimer? _usbDebounceTimer;
    private DateTime _introCooldownUntilUtc = DateTime.MinValue;
    private bool _wndProcHooked;

    public MainWindow()
    {
        InitializeComponent();
        _backend = new WalkmanBackendService();
        var player = new AudioPlaybackService();
        _player = player;
        _vm = new MainWindowViewModel(_backend, player);
        DataContext = _vm;
    }

    private readonly AudioPlaybackService? _player;

    public MainWindow(MainWindowViewModel vm, WalkmanBackendService backend, string? captureDir = null)
    {
        InitializeComponent();
        _vm = vm;
        _backend = backend;
        _captureDir = captureDir;
        DataContext = _vm;

        WireControlsAndHandlers();
    }

    private void WireControlsAndHandlers()
    {
        AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);

        CentralMedallion.LibraryRequested += () => _vm.OpenModule(ModuleIds.Library);
        CentralMedallion.PairSyncRequested += () => _vm.OpenModule(ModuleIds.PairSync);
        CentralMedallion.ExpandRingRequested += () =>
        {
            _vm.StartPhase = ModuleRegistry.TransitionStartState(_vm.StartPhase, StartScreenEvent.ClickPlus);
            StartGlassRing.Expanded = _vm.StartPhase == StartScreenPhase.RingExpanded;
        };
        CentralMedallion.CollapseRingRequested += () =>
        {
            _vm.StartPhase = ModuleRegistry.TransitionStartState(_vm.StartPhase, StartScreenEvent.CollapseRing);
            StartGlassRing.Expanded = false;
        };

        StartGlassRing.ModuleActivated += moduleId => _vm.OpenModule(moduleId);

        EdgeJogDial.ModuleActivated += moduleId => _vm.OpenModule(moduleId);
        EdgeJogDial.ReturnStartRequested += () =>
        {
            StartGlassRing.Expanded = false;
            _vm.ReturnToStartHub();
        };

        _vm.IntroReplayedRequested += () =>
        {
            _introCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            StartGlassRing.Expanded = false;
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.StartPhase))
            {
                StartGlassRing.Expanded = _vm.StartPhase == StartScreenPhase.RingExpanded;
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.ActiveModuleId) && _captureDir is null && _vm.ActiveModuleId is not null)
            {
                _ = AnimateWorkspaceTabInAsync();
            }
            else if (e.PropertyName == nameof(MainWindowViewModel.HasInputDialog) && _vm.HasInputDialog)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    InputDialogTextBox.Focus();
                    InputDialogTextBox.SelectAll();
                });
            }
        };

        _vm.PickFolderAsyncHandler = async title =>
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };

        _vm.PickCoverFileHandler = async () =>
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose Cover Art Image",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png"] }]
            });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };

        _vm.PickNwmOpenFileAsyncHandler = async () =>
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Walkman .NWM Screensaver",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Walkman Screensaver (*.nwm)") { Patterns = ["*.nwm"] }]
            });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };

        _vm.PickNwmSaveFileAsyncHandler = async suggestedName =>
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var picked = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Walkman .NWM Screensaver",
                SuggestedFileName = suggestedName,
                DefaultExtension = "nwm",
                FileTypeChoices = [new FilePickerFileType("Walkman Screensaver (*.nwm)") { Patterns = ["*.nwm"] }]
            });
            return picked?.TryGetLocalPath();
        };

        _vm.PickOpenGenericFileAsyncHandler = async (title, filterName, pattern) =>
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            string[] patterns = pattern.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }]
            });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };

        _vm.PickSaveGenericFileAsyncHandler = async (title, filterName, suggestedName) =>
        {
            var top = GetTopLevel(this);
            if (top?.StorageProvider is null) return null;
            var picked = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = [$"*{Path.GetExtension(suggestedName)}"] }]
            });
            return picked?.TryGetLocalPath();
        };
    }

    private CancellationTokenSource? _tabAnimCts;

    private async Task AnimateWorkspaceTabInAsync()
    {
        _tabAnimCts?.Cancel();
        _tabAnimCts?.Dispose();
        _tabAnimCts = new CancellationTokenSource();
        var ct = _tabAnimCts.Token;

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(210),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.None,
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0.0),
                    Setters =
                    {
                        new Avalonia.Styling.Setter(OpacityProperty, 0.0),
                        new Avalonia.Styling.Setter(RenderTransformProperty, Avalonia.Media.Transformation.TransformOperations.Parse("translateY(14px)"))
                    }
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1.0),
                    Setters =
                    {
                        new Avalonia.Styling.Setter(OpacityProperty, 1.0),
                        new Avalonia.Styling.Setter(RenderTransformProperty, Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)"))
                    }
                }
            }
        };

        try
        {
            await anim.RunAsync(WorkspaceHost, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSidebarHomeClicked(object? sender, RoutedEventArgs e)
    {
        StartGlassRing.Expanded = false;
        _vm.ReturnToStartHub();
    }

    private void OnDismissToastClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToastItem toast })
        {
            _vm.DismissToastCommand.Execute(toast.Id);
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        try
        {
            RegisterUsbHotPlugHook();

            if (_captureDir is not null)
            {
                await RunVisualVerificationCaptureTourAsync(_captureDir);
                Close();
                return;
            }

            await _vm.RescanUsbHardwareCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _vm.ShowToast("Startup Error", ex.Message, ToastSeverity.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _usbDebounceTimer?.Stop();
        _usbDebounceTimer = null;
        _tabAnimCts?.Cancel();
        _tabAnimCts?.Dispose();
        _tabAnimCts = null;
        _player?.Dispose();
        _usbBridge.Dispose();
        base.OnClosed(e);
    }

    private void RegisterUsbHotPlugHook()
    {
        if (!OperatingSystem.IsWindows() || _wndProcHooked)
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _usbBridge.RegisterForWindow(handle);
        _vm.AttachWindowHandleForSmtc(handle);
        Win32Properties.AddWndProcHookCallback(this, (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (UsbNotificationBridge.IsRelevantDeviceChangeEvent((int)msg, wParam))
            {
                Dispatcher.UIThread.Post(ScheduleDebouncedUsbRescan);
            }
            else if (msg == 0x0319 && _vm.TryConsumeExternalMediaKeyGuard()) // WM_APPCOMMAND (Windows hardware media keys)
            {
                int cmd = (int)(((long)lParam >> 16) & 0x0FFF);
                switch (cmd)
                {
                    case 14: // APPCOMMAND_MEDIA_PLAY_PAUSE
                    case 46: // APPCOMMAND_MEDIA_PLAY
                    case 47: // APPCOMMAND_MEDIA_PAUSE
                        if (_vm.HasPlayerTrack)
                        {
                            Dispatcher.UIThread.Post(() => _vm.ToggleGlobalPlayPauseCommand.Execute(null));
                            handled = true;
                        }
                        break;
                    case 11: // APPCOMMAND_MEDIA_NEXTTRACK
                        if (_vm.HasPlayerTrack)
                        {
                            Dispatcher.UIThread.Post(() => _vm.PlayNextTrackCommand.Execute(null));
                            handled = true;
                        }
                        break;
                    case 12: // APPCOMMAND_MEDIA_PREVIOUSTRACK
                        if (_vm.HasPlayerTrack)
                        {
                            Dispatcher.UIThread.Post(() => _vm.PlayPreviousTrackCommand.Execute(null));
                            handled = true;
                        }
                        break;
                    case 13: // APPCOMMAND_MEDIA_STOP
                        if (_vm.HasPlayerTrack)
                        {
                            Dispatcher.UIThread.Post(() => _vm.CloseGlobalPlayerCommand.Execute(null));
                            handled = true;
                        }
                        break;
                }
            }

            return IntPtr.Zero;
        });
        _wndProcHooked = true;
    }

    private void ScheduleDebouncedUsbRescan()
    {
        _usbDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _usbDebounceTimer.Stop();
        _usbDebounceTimer.Tick -= OnUsbDebounceTick;
        _usbDebounceTimer.Tick += OnUsbDebounceTick;
        _usbDebounceTimer.Start();
    }

    private void OnUsbDebounceTick(object? sender, EventArgs e)
    {
        _usbDebounceTimer?.Stop();
        _ = _vm.RescanUsbHardwareCommand.ExecuteAsync(null);
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm.ActiveModuleId is not null || _vm.StartPhase != StartScreenPhase.IdleBlobs)
        {
            return;
        }

        if (_windowClock.ElapsedMilliseconds < 250 || DateTime.UtcNow < _introCooldownUntilUtc)
        {
            return;
        }

        var pt = e.GetPosition(RootGrid);
        if (pt.X <= 1 && pt.Y <= 1)
        {
            return;
        }

        _vm.TriggerPointerEnterStart();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm.ActiveModuleId is null && _vm.StartPhase == StartScreenPhase.IdleBlobs)
        {
            _vm.TriggerPointerEnterStart();
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm.IsJogDialOpen = false;
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        bool inTextBox = focused is TextBox || e.Source is TextBox;

        if (e.Key == Key.Tab && !inTextBox && !_vm.HasConfirmDialog)
        {
            if (_vm.IsWorkspace && !_vm.UseSidebarRail)
            {
                _vm.IsJogDialOpen = !_vm.IsJogDialOpen;
                e.Handled = true;
                return;
            }

            if (_vm.IsStartHub && _vm.StartPhase != StartScreenPhase.IdleBlobs)
            {
                var ev = _vm.StartPhase == StartScreenPhase.RingExpanded
                    ? StartScreenEvent.CollapseRing
                    : StartScreenEvent.ClickPlus;
                _vm.StartPhase = ModuleRegistry.TransitionStartState(_vm.StartPhase, ev);
                StartGlassRing.Expanded = _vm.StartPhase == StartScreenPhase.RingExpanded;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter)
        {
            if (_vm.HasInputDialog)
            {
                _vm.ConfirmInputDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_vm.HasConfirmDialog)
            {
                _vm.ConfirmActiveDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            if (_vm.HasInputDialog)
            {
                _vm.CancelInputDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_vm.HasConfirmDialog)
            {
                _vm.CancelActiveDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_vm.IsJogDialOpen)
            {
                _vm.IsJogDialOpen = false;
                e.Handled = true;
                return;
            }

            if (_vm.IsStartHub && _vm.StartPhase == StartScreenPhase.RingExpanded)
            {
                _vm.StartPhase = ModuleRegistry.TransitionStartState(_vm.StartPhase, StartScreenEvent.CollapseRing);
                StartGlassRing.Expanded = false;
                e.Handled = true;
                return;
            }
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.K:
                    _vm.OpenModule(ModuleIds.AllFeatures);
                    e.Handled = true;
                    return;
                case Key.T:
                    _vm.ToggleThemeCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.F:
                    _vm.RequestFocusSearch();
                    e.Handled = true;
                    return;
                case Key.A when !inTextBox:
                    if (_vm.IsFileManagerActive) _vm.ToggleSelectAllFmSourceCommand.Execute(null);
                    else if (_vm.IsCodecConverterActive) _vm.ToggleSelectAllConvSourceCommand.Execute(null);
                    else if (_vm.IsCustomizationActive && _vm.IsCustomTagsTab) _vm.ToggleSelectAllTagTracksCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D1 or Key.NumPad1 when _vm.IsDeviceConnected:
                    _vm.OpenModule(ModuleIds.FileManager);
                    e.Handled = true;
                    return;
                case Key.D2 or Key.NumPad2:
                    _vm.OpenModule(ModuleIds.Library);
                    e.Handled = true;
                    return;
                case Key.D3 or Key.NumPad3:
                    _vm.OpenModule(ModuleIds.Converter);
                    e.Handled = true;
                    return;
                case Key.D4 or Key.NumPad4:
                    _vm.OpenModule(ModuleIds.Customization);
                    e.Handled = true;
                    return;
                case Key.D5 or Key.NumPad5:
                    _vm.OpenModule(ModuleIds.DeviceSystem);
                    e.Handled = true;
                    return;
                case Key.D6 or Key.NumPad6:
                    _vm.OpenModule(ModuleIds.Settings);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Space &&
            _vm.HasPlayerTrack &&
            !inTextBox &&
            focused is not Button and not CheckBox and not ComboBox &&
            e.Source is not Button and not CheckBox and not ComboBox)
        {
            _vm.ToggleGlobalPlayPauseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async Task RunVisualVerificationCaptureTourAsync(string captureDir)
    {
        Directory.CreateDirectory(captureDir);

        async Task Snap(string fileName, int delayMs = 340)
        {
            UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(delayMs);

            var pixelSize = new PixelSize(1440, 900);
            using var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            rtb.Render(this);
            rtb.Save(Path.Combine(captureDir, fileName));
        }

        _vm.IsDemoModeEnabled = true;
        var demoDevices = DemoModeOverlay.CreateDemoDevices();

        // 1. Cold-Start Idle Blobs
        _vm.IsDark = false;
        _vm.UseSidebarRail = false;
        _vm.ActiveModuleId = null;
        _vm.StartPhase = StartScreenPhase.IdleBlobs;
        BlobCanvas.SnapAnimationStateForPhase(StartScreenPhase.IdleBlobs);
        CentralMedallion.SnapStateForPhase(StartScreenPhase.IdleBlobs);
        StartGlassRing.SnapExpandedForCapture(false);
        await Snap("01_start_blobs.png");

        // 2. Medallion Ready (Disconnected, calm fixed top-left specular sheen)
        _vm.SelectDevice(null);
        _vm.StartPhase = StartScreenPhase.MedallionReady;
        BlobCanvas.SnapAnimationStateForPhase(StartScreenPhase.MedallionReady);
        CentralMedallion.SnapStateForPhase(StartScreenPhase.MedallionReady);
        StartGlassRing.SnapExpandedForCapture(false);
        await Snap("02_start_medallion_disconnected.png");

        // 3. Expanded 360 Ring (NW-HD5 Connected + Hover Neon Under-Glass Icon + Bottom Utility Bar)
        _vm.SelectDevice(demoDevices[0]);
        _vm.StartPhase = StartScreenPhase.RingExpanded;
        BlobCanvas.SnapAnimationStateForPhase(StartScreenPhase.RingExpanded);
        CentralMedallion.SnapStateForPhase(StartScreenPhase.RingExpanded);
        StartGlassRing.SnapExpandedForCapture(true);
        StartGlassRing.SetSimulatedHoverForCapture(ModuleIds.Customization);
        await Snap("03_start_ring_connected_hover.png");

        // 4. Device & System Submenu Drill-Down (4 children + Back replaces parent slot)
        StartGlassRing.SetSimulatedHoverForCapture(ModuleIds.DeviceDumpWizard);
        StartGlassRing.SnapExpandedForCapture(true, ModuleIds.DeviceSystem);
        StartGlassRing.SetSimulatedHoverForCapture(ModuleIds.DeviceDumpWizard);
        await Snap("04_start_ring_submenu_device_system.png");

        // 5. File Manager (Machined recessed metal-wells + OMGAUDIO Group + Projected Storage Bar + Dual Dock)
        StartGlassRing.SnapExpandedForCapture(false);
        _vm.OpenModule(ModuleIds.FileManager);
        _vm.FileManagerDestTracks.First().IsDestSelected = true;
        _vm.NotifyDestSelectionChanged();
        _vm.PlayOrToggleTrack(_vm.FileManagerSourceTracks[0]);
        _vm.SeekPlayer(2);
        _vm.TransferJob = new ActiveTransferJob("cap", "2 files transferred", "Committed to OMGAUDIO (Electronic)", "Auto", 2, 2, 100, 0, true);
        await Snap("05_file_manager_dual_dock.png");

        // 6. File Manager Custom Codec Locked + Collapsed Bottom Reserve Margin
        _vm.FmUploadMode = UploadMode.Custom;
        _vm.FmRememberChoice = true;
        _vm.TransferJob = null;
        _vm.CloseGlobalPlayerCommand.Execute(null);
        await Snap("06_file_manager_locked_collapsed.png");

        // 7. Pop-Out Edge Jog Dial (Rotated Century Gothic + 0-deg Neon Under-Glass Hover + Unified Bottom-Left Bar)
        _vm.FmRememberChoice = false;
        _vm.FmUploadMode = UploadMode.Auto;
        _vm.IsJogDialOpen = true;
        EdgeJogDial.SnapOpenForCapture(true, ModuleIds.Customization);
        await Snap("07_popout_jog_dial_hover.png");

        // 8. Compact Sidebar Rail Mode (My Library)
        _vm.IsJogDialOpen = false;
        EdgeJogDial.SnapOpenForCapture(false);
        _vm.UseSidebarRail = true;
        _vm.OpenModule(ModuleIds.Library);
        await Snap("08_sidebar_rail_library.png");

        // 9. Codec Converter Light (Recessed wells + Dual Dock)
        _vm.UseSidebarRail = false;
        _vm.SelectDevice(null);
        _vm.OpenModule(ModuleIds.Converter);
        _vm.PlayOrToggleTrack(_vm.ConverterSourceTracks[0]);
        _vm.SeekPlayer(2);
        _vm.TransferJob = new ActiveTransferJob("cap2", "Aqua Drift", "Normalizing & encoding", "ATRAC3+ 256k · 44.1 kHz", 1, 3, 33, 7, false);
        await Snap("09_codec_converter_light.png");

        // 10. Dark Graphite Theme (Codec Converter Lossless FLAC Fixed-Height Well + Dual Dock)
        _vm.IsDark = true;
        _vm.SetConverterCodecCommand.Execute(TargetCodec.Flac);
        await Snap("10_codec_converter_dark_lossless.png");

        // 11. My Library + Global Player Volume Bar
        _vm.IsDark = false;
        _vm.SetConverterCodecCommand.Execute(TargetCodec.Atrac3Plus);
        _vm.TransferJob = null;
        _vm.OpenModule(ModuleIds.Library);
        await Snap("11_my_library_player.png");

        // 12. Customization -> Screen & OLED Post-MVP Placeholder (Ticket UI-01)
        _vm.CloseGlobalPlayerCommand.Execute(null);
        _vm.SelectDevice(demoDevices[0]);
        _vm.OpenModule(ModuleIds.CustomOled);
        await Snap("12_customization_oled_placeholder.png");

        // 13. Customization -> Subtitles Dual-Pane Split Workbench
        _vm.OpenModule(ModuleIds.CustomSubtitles);
        await Snap("13_customization_subtitles_split.png");

        // 14. Customization -> Batch Tag Editor Split Workbench
        _vm.OpenModule(ModuleIds.CustomTags);
        if (_vm.CustomizationTracks.Count > 1)
        {
            _vm.CustomizationTracks[0].IsTagSelected = true;
            _vm.CustomizationTracks[1].IsTagSelected = true;
            _vm.NotifyTagSelectionChanged();
        }
        await Snap("14_customization_tags_split.png");

        // 15. Device & System -> Pair & Sync
        _vm.OpenModule(ModuleIds.PairSync);
        await Snap("15_device_system_pair.png");

        // 16. Device & System -> Firmware & Dump Wizard (Dark Graphite)
        _vm.OpenModule(ModuleIds.CustomFirmware);
        await Snap("16a_device_system_firmware.png");
        _vm.IsDark = true;
        _vm.OpenModule(ModuleIds.DeviceDumpWizard);
        await Snap("16b_device_system_dump_dark.png");

        // 17. Device & System -> Technical Info
        _vm.IsDark = false;
        _vm.OpenModule(ModuleIds.DeviceTechInfo);
        await Snap("17_device_system_tech_info.png");

        // 18. All Features Summary Cards + Toast Notification + Confirmation Modal
        _vm.OpenModule(ModuleIds.AllFeatures);
        _vm.ShowToast("Demo Mode Active", "Demo devices & catalog.", ToastSeverity.Info);
        _vm.RequestConfirmation(
            "Delete 2 Track(s)?",
            "Permanently remove 2 selected track(s) from NW-HD5? Free MagicGate/storage slots will be reclaimed.",
            "Delete (2)",
            isDestructive: true,
            () => { });
        await Snap("18_all_features_toast_modal.png");
        _vm.CancelActiveDialogCommand.Execute(null);
        _vm.Toasts.Clear();

        // 19. Settings (Persisted Preferences, Demo Mode Toggle, Sidebar Rail Toggle)
        _vm.OpenModule(ModuleIds.Settings);
        await Snap("19_settings.png");
    }
}
