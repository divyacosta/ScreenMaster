using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ScreenMaster.Core;

namespace ScreenMaster.Native
{
    /// <summary>MCCS VCP codes we use.</summary>
    public static class Vcp
    {
        public const byte Brightness = 0x10, Contrast = 0x12, ColorPreset = 0x14,
            GainR = 0x16, GainG = 0x18, GainB = 0x1A, Input = 0x60, Volume = 0x62,
            BlackR = 0x6C, BlackG = 0x6E, BlackB = 0x70, Sharpness = 0x87, Mute = 0x8D, DisplayMode = 0xDC, Power = 0xD6;

        public static readonly Dictionary<int, string> PresetNames = new()
        {
            [0x01] = "sRGB", [0x02] = "Nativo", [0x03] = "4000K", [0x04] = "5000K", [0x05] = "6500K", [0x06] = "7500K",
            [0x07] = "8200K", [0x08] = "9300K", [0x09] = "10000K", [0x0A] = "11500K", [0x0B] = "Usuário 1", [0x0C] = "Usuário 2", [0x0D] = "Usuário 3",
        };

        public static readonly Dictionary<int, string> InputNames = new()
        {
            [0x01] = "VGA 1", [0x02] = "VGA 2", [0x03] = "DVI 1", [0x04] = "DVI 2", [0x0F] = "DisplayPort 1", [0x10] = "DisplayPort 2",
            [0x11] = "HDMI 1", [0x12] = "HDMI 2", [0x1B] = "USB-C",
        };

        public static readonly Dictionary<int, string> ModeNames = new()
        {
            [0x00] = "Padrão", [0x01] = "Produtividade", [0x02] = "Misto", [0x03] = "Filme", [0x04] = "Usuário", [0x05] = "Jogos",
            [0x06] = "Esportes", [0x07] = "Profissional", [0x08] = "Padrão (energia média)", [0x09] = "Padrão (economia)", [0x0A] = "Demonstração",
            [0xF0] = "Contraste dinâmico",
        };

        public static string NameOf(Dictionary<int, string> map, int v) => map.TryGetValue(v, out var n) ? n : $"Modo 0x{v:X2}";
    }

    /// <summary>A physical monitor reachable over DDC/CI. All calls are blocking (tens of ms) — use from a worker thread.</summary>
    public sealed class DdcMonitor : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }

        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr h);
        [DllImport("dxva2.dll")] static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder s, uint len);
        [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

        IntPtr _h;
        public string Device { get; }
        public string Caps { get; private set; } = "";
        public string Model { get; private set; } = "";
        /// <summary>Supported VCP codes; value list is non-empty for codes with enumerated values (e.g. 0x14, 0x60).</summary>
        public Dictionary<byte, List<int>> Supported { get; } = new();
        public bool Ok => _open && Supported.Count > 0;

        // Physical monitor handles are small integers and can legitimately be 0, so track "open" separately.
        bool _open;
        DdcMonitor(string device, IntPtr h) { Device = device; _h = h; _open = true; }

        public static List<DdcMonitor> OpenAll()
        {
            var list = new List<DdcMonitor>();
            var handles = new List<(IntPtr hmon, string dev)>();
            Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (Win32.GetMonitorInfo(h, ref mi)) handles.Add((h, mi.szDevice));
                return true;
            }, IntPtr.Zero);

            foreach (var (hmon, dev) in handles)
            {
                try
                {
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var n) || n == 0) continue;
                    var arr = new PHYSICAL_MONITOR[n];
                    if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, arr)) continue;
                    // One GDI device normally maps to one physical monitor; keep the first, release the rest.
                    for (int i = 1; i < arr.Length; i++) DestroyPhysicalMonitor(arr[i].h);
                    var m = new DdcMonitor(dev, arr[0].h);
                    m.LoadCaps();
                    list.Add(m);
                }
                catch (Exception ex) { Log.Write($"ddc open {dev}: {ex.Message}"); }
            }
            return list;
        }

        void LoadCaps()
        {
            // Some monitors (e.g. BenQ) answer the capabilities request only on a retry.
            for (int attempt = 0; attempt < 3 && Caps.Length == 0; attempt++)
            {
                try
                {
                    if (GetCapabilitiesStringLength(_h, out var len) && len > 0)
                    {
                        var sb = new StringBuilder((int)len);
                        if (CapabilitiesRequestAndCapabilitiesReply(_h, sb, len)) Caps = sb.ToString();
                    }
                }
                catch { }
                if (Caps.Length == 0) System.Threading.Thread.Sleep(300);
            }
            var mm = Regex.Match(Caps, @"model\(([^)]*)\)", RegexOptions.IgnoreCase);
            if (mm.Success) Model = mm.Groups[1].Value.Trim();
            ParseVcp();

            if (Supported.Count == 0)
            {
                // Fallback: no capabilities string — probe the common continuous controls directly.
                foreach (var code in new[] { Vcp.Brightness, Vcp.Contrast, Vcp.GainR, Vcp.GainG, Vcp.GainB })
                    if (TryGet(code, out _, out var max) && max > 0 && max <= 1000) Supported[code] = new List<int>();
            }
            if (Model.Length > 0) MonitorNames.SetModel(Device, Model);
            Log.Write($"ddc {Device}: model={Model} codes={Supported.Count} caps={(Caps.Length > 0 ? "sim" : "não")}");
        }

        void ParseVcp()
        {
            int i = Caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return;
            i += 4;
            int depth = 1;
            byte? last = null;
            var tok = new StringBuilder();
            void Flush()
            {
                if (tok.Length == 0) return;
                if (byte.TryParse(tok.ToString(), NumberStyles.HexNumber, null, out var b))
                {
                    if (depth == 1) { last = b; Supported[b] = new List<int>(); }
                    else if (depth == 2 && last != null) Supported[last.Value].Add(b);
                }
                tok.Clear();
            }
            for (; i < Caps.Length && depth > 0; i++)
            {
                char c = Caps[i];
                if (c == '(') { Flush(); depth++; }
                else if (c == ')') { Flush(); depth--; }
                else if (char.IsWhiteSpace(c)) Flush();
                else tok.Append(c);
            }
        }

        public bool Has(byte code) => Supported.ContainsKey(code);

        public bool TryGet(byte code, out int cur, out int max)
        {
            cur = max = 0;
            if (!_open) return false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (GetVCPFeatureAndVCPFeatureReply(_h, code, IntPtr.Zero, out var c, out var m))
                    {
                        cur = (int)c; max = (int)m;
                        return true;
                    }
                }
                catch { return false; }
                System.Threading.Thread.Sleep(40);
            }
            return false;
        }

        public bool Set(byte code, int value)
        {
            if (!_open) return false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { if (SetVCPFeature(_h, code, (uint)Math.Max(0, value))) return true; }
                catch { return false; }
                System.Threading.Thread.Sleep(50);
            }
            Log.Write($"ddc set {Device} 0x{code:X2}={value} failed");
            return false;
        }

        public void Dispose()
        {
            if (_open) { DestroyPhysicalMonitor(_h); _open = false; }
        }
    }

    /// <summary>Friendly monitor names ("BenQ XL2430") from the EDID in the registry.</summary>
    public static class MonitorNames
    {
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

        static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BNQ"] = "BenQ", ["DEL"] = "Dell", ["SAM"] = "Samsung", ["GSM"] = "LG", ["AUS"] = "ASUS", ["ACI"] = "ASUS", ["AOC"] = "AOC",
            ["HWP"] = "HP", ["HPN"] = "HP", ["LEN"] = "Lenovo", ["ACR"] = "Acer", ["MSI"] = "MSI", ["GBT"] = "Gigabyte", ["VSC"] = "ViewSonic",
            ["PHL"] = "Philips", ["SNY"] = "Sony", ["AOR"] = "AORUS", ["XMI"] = "Xiaomi", ["LGD"] = "LG", ["CMN"] = "Innolux",
        };

        public static string Get(string gdiDevice)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(gdiDevice, out var n)) return n;
                n = Read(gdiDevice) ?? "";
                if (n.Length > 0) n = WithModel(gdiDevice, n);
                Cache[gdiDevice] = n;
                return n;
            }
        }

        static readonly Dictionary<string, string> Models = new(StringComparer.OrdinalIgnoreCase);

        public static void Clear() { lock (Cache) Cache.Clear(); }

        /// <summary>The DDC model ("XL2430") is often more recognizable than the EDID name ("BenQ ZOWIE XL LCD").</summary>
        public static void SetModel(string gdiDevice, string model)
        {
            lock (Cache)
            {
                Models[gdiDevice] = model;
                Cache.Remove(gdiDevice);
            }
        }

        static string WithModel(string gdiDevice, string name)
        {
            if (!Models.TryGetValue(gdiDevice, out var model) || name.Contains(model, StringComparison.OrdinalIgnoreCase)) return name;
            var vendor = name.Split(' ')[0];
            return $"{vendor} {model}";
        }

        static string? Read(string gdiDevice)
        {
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
                if (!EnumDisplayDevices(gdiDevice, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return null;
                // \\?\DISPLAY#BNQ7F44#5&abc&0&UID4352#{guid}
                var parts = dd.DeviceID.Split('#');
                if (parts.Length < 3) return Clean(dd.DeviceString);
                string pnp = parts[1], inst = parts[2];
                string vendor = Vendors.TryGetValue(pnp.Substring(0, Math.Min(3, pnp.Length)), out var v) ? v : pnp.Substring(0, Math.Min(3, pnp.Length));
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pnp}\{inst}\Device Parameters");
                if (k?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                {
                    for (int o = 54; o + 18 <= 126; o += 18)
                    {
                        if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 3] == 0xFC)
                        {
                            var name = Encoding.ASCII.GetString(edid, o + 5, 13).Split('\n')[0].Trim();
                            if (name.Length > 0)
                                return name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? name : $"{vendor} {name}";
                        }
                    }
                }
                return vendor;
            }
            catch (Exception ex) { Log.Write("monitor name: " + ex.Message); return null; }
        }

        static string? Clean(string s) => string.IsNullOrWhiteSpace(s) || s.Contains("Generic", StringComparison.OrdinalIgnoreCase) ? null : s;
    }
}
