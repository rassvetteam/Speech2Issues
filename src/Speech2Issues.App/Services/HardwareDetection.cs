using System.Runtime.InteropServices;

namespace Speech2Issues.App.Services;

public static class HardwareDetection
{
    public static bool HasNvidiaGpu()
    {
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++)
        {
            if ((device.DeviceString ?? string.Empty).Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            device.cb = Marshal.SizeOf<DisplayDevice>();
        }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceKey;
    }
}
