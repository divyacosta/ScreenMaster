using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenMaster.Core;

namespace ScreenMaster.Native
{
    /// <summary>Detects things that fight with gamma ramps / the color matrix.</summary>
    public static class Diagnostics
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_TARGET
        {
            public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
            public uint refreshNum, refreshDen, scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        struct MODE_INFO { public uint infoType, id; public LUID adapterId; }

        [StructLayout(LayoutKind.Sequential)]
        struct HEADER { public uint type, size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SOURCE_NAME
        {
            public HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ADV_COLOR { public HEADER header; public uint value, colorEncoding, bitsPerColorChannel; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ADV_COLOR info);

        /// <summary>GDI device names whose advanced color (HDR or Auto Color Management) is on.</summary>
        public static HashSet<string> AdvancedColorDisplays()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                const uint QDC_ONLY_ACTIVE_PATHS = 2;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return set;
                var paths = new PATH_INFO[np];
                var modes = new MODE_INFO[nm];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return set;
                for (int i = 0; i < np; i++)
                {
                    var p = paths[i];
                    var sn = new SOURCE_NAME { header = new HEADER { type = 1, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = p.source.adapterId, id = p.source.id } };
                    if (DisplayConfigGetDeviceInfo(ref sn) != 0) continue;
                    var ac = new ADV_COLOR { header = new HEADER { type = 9, size = (uint)Marshal.SizeOf<ADV_COLOR>(), adapterId = p.target.adapterId, id = p.target.id } };
                    if (DisplayConfigGetDeviceInfo(ref ac) != 0) continue;
                    if ((ac.value & 0x2) != 0) set.Add(sn.viewGdiDeviceName);
                }
            }
            catch (Exception ex) { Log.Write("advanced color check: " + ex.Message); }
            return set;
        }

        public static bool NightLightOn()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate");
                if (k?.GetValue("Data") is byte[] d && d.Length > 18) return d[18] == 0x15;
            }
            catch { }
            return false;
        }

        public static bool ColorFiltersOn()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\ColorFiltering");
                return k?.GetValue("Active") is int v && v == 1;
            }
            catch { return false; }
        }

        public static bool ProcessRunning(string name)
        {
            try
            {
                var ps = Process.GetProcessesByName(name);
                foreach (var p in ps) p.Dispose();
                return ps.Length > 0;
            }
            catch { return false; }
        }

        public static bool GammaRangeUnlocked()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM");
                return k?.GetValue("GdiIcmGammaRange") is int v && v >= 256;
            }
            catch { return false; }
        }

        public static List<string> Warnings(IEnumerable<MonitorInfo> monitors)
        {
            var w = new List<string>();
            if (NightLightOn()) w.Add("Luz noturna do Windows está ligada: ela briga com Níveis/Curvas. Desligue em Configurações › Vídeo.");
            if (ColorFiltersOn()) w.Add("Filtros de cor do Windows estão ligados: eles disputam o mesmo recurso dos ajustes de cor.");
            if (ProcessRunning("Magnify")) w.Add("A Lupa do Windows está aberta: ela desativa os ajustes de cor enquanto estiver aberta.");
            if (ProcessRunning("flux")) w.Add("f.lux está rodando: ele sobrescreve Níveis/Curvas.");
            if (ProcessRunning("vibranceGUI")) w.Add("VibranceGUI está rodando: ele e o ajuste Vibração vão disputar o vibrance da NVIDIA. Feche um dos dois.");
            var adv = AdvancedColorDisplays();
            foreach (var m in monitors)
                if (adv.Contains(m.DeviceName))
                    w.Add($"{m.Label}: HDR ou Gerenciamento automático de cor ligado. Níveis/Curvas podem não ter efeito nesse monitor.");
            return w;
        }
    }
}
