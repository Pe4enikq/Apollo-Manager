using System.Runtime.InteropServices;

namespace Walkman.L1.DevicePoller.Interop;

/// <summary>
/// AnyCPU-safe P/Invoke wrappers for Windows SetupAPI USB enumeration.
/// Used alongside CsWin32 because CsWin32 marks SetupDi* APIs architecture-specific.
/// </summary>
internal static class SetupApiNative
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public const uint SPDRP_DEVICEDESC = 0x00000000;
    public const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        char[] deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    public static IReadOnlyList<(string InstanceId, string? Description)> EnumeratePresentPnpDevices(string enumerator)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<(string, string?)>();
        }

        IntPtr devInfoSet = SetupDiGetClassDevsW(
            IntPtr.Zero,
            enumerator,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_ALLCLASSES);

        if (devInfoSet == InvalidHandleValue || devInfoSet == IntPtr.Zero)
        {
            return Array.Empty<(string, string?)>();
        }

        var results = new List<(string, string?)>();
        try
        {
            var devInfo = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            uint index = 0;
            var idBuffer = new char[512];

            while (SetupDiEnumDeviceInfo(devInfoSet, index++, ref devInfo))
            {
                if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfo, idBuffer, idBuffer.Length, out int requiredChars) ||
                    requiredChars <= 1)
                {
                    continue;
                }

                var instanceId = new string(idBuffer, 0, requiredChars - 1);
                var friendly = ReadRegistryStringProperty(devInfoSet, ref devInfo, SPDRP_FRIENDLYNAME)
                            ?? ReadRegistryStringProperty(devInfoSet, ref devInfo, SPDRP_DEVICEDESC);

                results.Add((instanceId, friendly));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

    private static string? ReadRegistryStringProperty(
        IntPtr devInfoSet,
        ref SP_DEVINFO_DATA devInfo,
        uint propertyCode)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryPropertyW(
                devInfoSet,
                ref devInfo,
                propertyCode,
                out _,
                buffer,
                (uint)buffer.Length,
                out uint requiredBytes) || requiredBytes < 2)
        {
            return null;
        }

        int byteCount = (int)Math.Min(requiredBytes, (uint)buffer.Length);
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, byteCount);
        return text.TrimEnd('\0');
    }
}
