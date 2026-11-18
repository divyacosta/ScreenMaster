using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenMaster.Native
{
    /// <summary>
    /// Minimal NVAPI binding for Digital Vibrance (DVC).
    /// Public IDs checked against NVIDIA/nvapi nvapi_interface.h; DVC IDs and struct
    /// layouts are private and taken from NvAPIWrapper (falahati), the same ones VibranceGUI uses.
    /// Every call returns a status; nothing here throws.
    /// </summary>
    public static class NvApi
    {
        const uint ID_Initialize = 0x0150E828;
        const uint ID_EnumNvidiaDisplayHandle = 0x9ABDD40D;
        const uint ID_GetAssociatedNvidiaDisplayName = 0x22A78B05;
        const uint ID_GetDVCInfoEx = 0x0E45002D;
        const uint ID_SetDVCLevelEx = 0x4A82C2B1;
        const uint ID_GetDVCInfo = 0x4085DE45;
        const uint ID_SetDVCLevel = 0x172409B4;

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct DvcInfoEx
        {
            public uint Version;
            public int Current, Min, Max, Default;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct DvcInfo
        {
            public uint Version;
            public int Current, Min, Max;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumDisplayFn(int index, ref IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetNameFn(IntPtr handle, [MarshalAs(UnmanagedType.LPArray)] byte[] name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetDvcExFn(IntPtr handle, uint outputId, ref DvcInfoEx info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetDvcExFn(IntPtr handle, uint outputId, ref DvcInfoEx info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetDvcFn(IntPtr handle, uint outputId, ref DvcInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetDvcFn(IntPtr handle, uint outputId, int level);

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        static EnumDisplayFn? _enum;
        static GetNameFn? _getName;
        static GetDvcExFn? _getEx;
        static SetDvcExFn? _setEx;
        static GetDvcFn? _get;
        static SetDvcFn? _set;

        public static bool Available { get; private set; }
        public static string Status { get; private set; } = "não iniciado";

        static T? Fn<T>(uint id) where T : Delegate
        {
            var p = QueryInterface(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        public static bool Init()
        {
            if (Available) return true;
            try
            {
                var init = Fn<InitializeFn>(ID_Initialize);
                if (init == null) { Status = "nvapi64.dll sem Initialize"; return false; }
                int r = init();
                if (r != 0) { Status = $"NvAPI_Initialize falhou ({r})"; return false; }
                _enum = Fn<EnumDisplayFn>(ID_EnumNvidiaDisplayHandle);
                _getName = Fn<GetNameFn>(ID_GetAssociatedNvidiaDisplayName);
                _getEx = Fn<GetDvcExFn>(ID_GetDVCInfoEx);
                _setEx = Fn<SetDvcExFn>(ID_SetDVCLevelEx);
                _get = Fn<GetDvcFn>(ID_GetDVCInfo);
                _set = Fn<SetDvcFn>(ID_SetDVCLevel);
                if (_enum == null || _getName == null || (_getEx == null && _get == null))
                {
                    Status = "funções de vibrance não encontradas no driver";
                    return false;
                }
                Available = true;
                Status = "ok";
                return true;
            }
            catch (DllNotFoundException) { Status = "driver NVIDIA não encontrado"; return false; }
            catch (Exception ex) { Status = "erro: " + ex.Message; return false; }
        }

        /// <summary>Returns map of "\\.\DISPLAYn" -> NvDisplayHandle.</summary>
        public static Dictionary<string, IntPtr> EnumDisplays()
        {
            var map = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            if (!Available || _enum == null || _getName == null) return map;
            for (int i = 0; i < 16; i++)
            {
                IntPtr h = IntPtr.Zero;
                int r = _enum(i, ref h);
                if (r != 0) break; // NVAPI_END_ENUMERATION or error
                var buf = new byte[64];
                if (_getName(h, buf) == 0)
                {
                    var name = Encoding.ASCII.GetString(buf).TrimEnd('\0');
                    map[name] = h;
                }
            }
            return map;
        }

        public static bool TryGet(IntPtr handle, out int current, out int min, out int max, out int def, out int status)
        {
            current = min = max = def = 0;
            status = -1;
            try
            {
                if (_getEx != null)
                {
                    var info = new DvcInfoEx { Version = (uint)Marshal.SizeOf<DvcInfoEx>() | (1u << 16) };
                    status = _getEx(handle, 0, ref info);
                    if (status == 0)
                    {
                        current = info.Current; min = info.Min; max = info.Max; def = info.Default;
                        return true;
                    }
                }
                if (_get != null)
                {
                    var info = new DvcInfo { Version = (uint)Marshal.SizeOf<DvcInfo>() | (1u << 16) };
                    status = _get(handle, 0, ref info);
                    if (status == 0)
                    {
                        current = info.Current; min = info.Min; max = info.Max; def = info.Min;
                        _preferLegacy = true;
                        return true;
                    }
                }
            }
            catch (Exception) { status = -999; }
            return false;
        }

        static bool _preferLegacy;

        public static int Set(IntPtr handle, int level)
        {
            try
            {
                if (_setEx != null && !_preferLegacy)
                {
                    var info = new DvcInfoEx { Version = (uint)Marshal.SizeOf<DvcInfoEx>() | (1u << 16), Current = level };
                    int r = _setEx(handle, 0, ref info);
                    if (r == 0 || _set == null) return r;
                }
                return _set != null ? _set(handle, 0, level) : -1;
            }
            catch (Exception) { return -999; }
        }
    }
}
