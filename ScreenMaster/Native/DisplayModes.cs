using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenMaster.Native
{
    public sealed record DisplayMode(int Width, int Height, int Hz)
    {
        public override string ToString() => $"{Width}×{Height}";
    }

    /// <summary>Resolution / refresh rate via EnumDisplaySettings / ChangeDisplaySettingsEx.</summary>
    public static class DisplayModes
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

        const int ENUM_CURRENT_SETTINGS = -1;
        const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
        const int CDS_UPDATEREGISTRY = 0x1;

        static DEVMODE New() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DEVMODE>() };

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int hz, DateTime at)> _hz = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Refresh rate of a monitor in Hz (60 if unknown), cached for a few seconds.</summary>
        public static int RefreshOf(string device)
        {
            if (_hz.TryGetValue(device, out var c) && (DateTime.UtcNow - c.at).TotalSeconds < 5) return c.hz;
            int hz = Current(device)?.Hz is int h && h > 1 ? h : 60;
            _hz[device] = (hz, DateTime.UtcNow);
            return hz;
        }

        public static DisplayMode? Current(string device)
        {
            var dm = New();
            return EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) ? new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
        }

        public static List<DisplayMode> All(string device)
        {
            var set = new HashSet<DisplayMode>();
            var dm = New();
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.dmBitsPerPel >= 24 && dm.dmPelsWidth >= 800) set.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
                dm = New();
            }
            return set.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Hz).ToList();
        }

        /// <summary>Returns the Win32 DISP_CHANGE code (0 = success).</summary>
        public static int Set(string device, DisplayMode mode)
        {
            var dm = New();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return -1;
            dm.dmPelsWidth = mode.Width;
            dm.dmPelsHeight = mode.Height;
            dm.dmDisplayFrequency = mode.Hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
    }
}
