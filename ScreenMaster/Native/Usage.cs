using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenMaster.Native
{
    /// <summary>Brand of the CPU and of the GPU that drives the main display, for the brand-colored indicators.</summary>
    public static class Hardware
    {
        public sealed record Brand(string Name, string Color);

        static readonly Brand Intel = new("Intel", "#0071C5");
        static readonly Brand Amd = new("AMD", "#ED1C24");
        static readonly Brand Nvidia = new("NVIDIA", "#76B900");
        static readonly Brand Unknown = new("", "#8A8A90");

        static Brand? _cpu, _gpu;
        static string? _gpuName;

        public static Brand Cpu => _cpu ??= ReadCpu();
        public static Brand Gpu { get { if (_gpu == null) ReadGpu(); return _gpu!; } }
        public static string GpuName { get { if (_gpu == null) ReadGpu(); return _gpuName ?? ""; } }

        static Brand ReadCpu()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var v = k?.GetValue("VendorIdentifier") as string ?? "";
                if (v.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return Intel;
                if (v.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return Amd;
            }
            catch { }
            return Unknown;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        static void ReadGpu()
        {
            string name = "";
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & 0x4) != 0) { name = dd.DeviceString; break; } // primary
                    if (name.Length == 0 && (dd.StateFlags & 0x1) != 0) name = dd.DeviceString; // attached to desktop
                }
            }
            catch { }
            _gpuName = name.Trim();
            _gpu = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? Nvidia
                 : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? Amd
                 : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? Intel : Unknown;
        }
    }

    /// <summary>
    /// How much CPU and GPU this process uses, sampled on demand.
    /// CPU = process time over wall time across all cores (as Task Manager shows it).
    /// GPU = Windows' "GPU Engine" counters for this process: busiest engine type, as Task Manager shows it.
    /// </summary>
    public sealed class Usage : IDisposable
    {
        readonly Process _proc = Process.GetCurrentProcess();
        TimeSpan _lastCpu;
        DateTime _lastAt;
        IntPtr _query, _counter;
        bool _gpuOk;

        public Usage()
        {
            _lastCpu = _proc.TotalProcessorTime;
            _lastAt = DateTime.UtcNow;
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0 &&
                    PdhAddEnglishCounter(_query, $@"\GPU Engine(pid_{Environment.ProcessId}_*)\Utilization Percentage", IntPtr.Zero, out _counter) == 0)
                {
                    PdhCollectQueryData(_query);
                    _gpuOk = true;
                }
            }
            catch { _gpuOk = false; }
        }

        /// <summary>Percent of the whole CPU and of the GPU since the last call (GPU null if the counters aren't available).</summary>
        public (double cpu, double? gpu) Sample()
        {
            _proc.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = _proc.TotalProcessorTime;
            double wall = (now - _lastAt).TotalMilliseconds;
            double cpu = wall > 0 ? (cpuTime - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100 : 0;
            _lastCpu = cpuTime;
            _lastAt = now;
            return (Math.Clamp(cpu, 0, 100), _gpuOk ? Gpu() : null);
        }

        double? Gpu()
        {
            try
            {
                if (PdhCollectQueryData(_query) != 0) return null;
                uint size = 0, count = 0;
                int rc = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
                if (rc == PDH_CSTATUS_NO_INSTANCE || count == 0 && rc != PDH_MORE_DATA) return 0;
                if (rc != PDH_MORE_DATA) return null;
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return null;
                    var byType = new Dictionary<string, double>();
                    int item = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                    for (int i = 0; i < count; i++)
                    {
                        var it = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * item);
                        var name = Marshal.PtrToStringUni(it.szName) ?? "";
                        int at = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                        var type = at >= 0 ? name[(at + 8)..] : name;
                        byType[type] = byType.GetValueOrDefault(type) + (it.FmtValue.CStatus == 0 ? it.FmtValue.doubleValue : 0);
                    }
                    return byType.Count == 0 ? 0 : Math.Clamp(byType.Values.Max(), 0, 100);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
            _proc.Dispose();
        }

        const uint PDH_FMT_DOUBLE = 0x00000200;
        const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        const int PDH_CSTATUS_NO_INSTANCE = unchecked((int)0x800007D1);

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
        [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    }
}
