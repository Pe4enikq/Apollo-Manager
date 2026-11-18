using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Walkman.L1.DevicePoller.Interop;

/// <summary>
/// CsWin32 wrapper for RegisterDeviceNotificationW / WM_DEVICECHANGE USB hot-plug hooks.
/// Intended for binding to the L3 AppShell window handle once Avalonia UI launches in M4.
/// </summary>
public sealed class UsbNotificationBridge : IDisposable
{
    public const int WmDeviceChange = 0x0219;
    public const int DbtDeviceArrival = 0x8000;
    public const int DbtDeviceRemoveComplete = 0x8004;
    public const int DbtDevNodesChanged = 0x0007;
    private const uint DbtDevTypDeviceInterface = 0x00000005;

    // GUID_DEVINTERFACE_USB_DEVICE = {A5DCBF10-6530-11D2-901F-00C04FB951ED}
    private static readonly Guid UsbDeviceInterfaceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private HDEVNOTIFY _notificationHandle;

    public bool IsRegistered => _notificationHandle != default;

    public unsafe bool RegisterForWindow(nint windowHandle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600) || windowHandle == 0 || IsRegistered)
        {
            return false;
        }

        var filter = new DEV_BROADCAST_DEVICEINTERFACE_W
        {
            dbcc_size = (uint)sizeof(DEV_BROADCAST_DEVICEINTERFACE_W),
            dbcc_devicetype = DbtDevTypDeviceInterface,
            dbcc_classguid = UsbDeviceInterfaceGuid
        };

        _notificationHandle = PInvoke.RegisterDeviceNotification(
            new HANDLE(windowHandle),
            &filter,
            REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);

        return IsRegistered;
    }

    public static bool IsRelevantDeviceChangeEvent(int msg, nint wParam)
    {
        if (msg != WmDeviceChange)
        {
            return false;
        }

        int code = unchecked((int)wParam.ToInt64());
        return code is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600) && _notificationHandle != default)
        {
            PInvoke.UnregisterDeviceNotification(_notificationHandle);
            _notificationHandle = default;
        }
    }
}
