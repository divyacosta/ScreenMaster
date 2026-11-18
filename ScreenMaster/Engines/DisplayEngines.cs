using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ScreenMaster.Core;
using ScreenMaster.Native;

namespace ScreenMaster.Engines
{
    /// <summary>Fullscreen color matrix via the Magnification API. Released automatically when the process exits.</summary>
    public sealed class MatrixEngine
    {
        public bool Ready { get; private set; }
        public string Status { get; private set; } = "não iniciado";
        bool _nonIdentity;

        public bool Init()
        {
            try
            {
                Ready = Win32.MagInitialize();
                Status = Ready ? "ok" : $"MagInitialize falhou ({Marshal.GetLastWin32Error()})";
            }
            catch (Exception ex) { Status = "erro: " + ex.Message; Ready = false; }
            Log.Write("matrix init: " + Status);
            return Ready;
        }

        public bool Apply(ColorMatrix m)
        {
            if (!Ready) return false;
            if (m.IsIdentity && !_nonIdentity) return true;
            var e = m.ToMag();
            bool ok = Win32.MagSetFullscreenColorEffect(ref e);
            if (!ok) { Status = $"Windows recusou a matriz ({Marshal.GetLastWin32Error()})"; Log.Write(Status); }
            else { Status = "ok"; _nonIdentity = !m.IsIdentity; }
            return ok;
        }

        public float[]? Read()
        {
            if (!Ready) return null;
            var e = new MAGCOLOREFFECT { transform = new float[25] };
            return Win32.MagGetFullscreenColorEffect(ref e) ? e.transform : null;
        }

        public void Reset()
        {
            if (!Ready) return;
            var e = ColorMatrix.Identity().ToMag();
            Win32.MagSetFullscreenColorEffect(ref e);
            _nonIdentity = false;
        }

        public void Shutdown()
        {
            if (!Ready) return;
            Reset();
            Win32.MagUninitialize();
            Ready = false;
        }
    }

    /// <summary>Per-monitor gamma ramps (display hardware LUT). Composited on top of the monitor's original calibration ramp.</summary>
    public sealed class GammaEngine
    {
        readonly DisplayState _state;
        readonly Dictionary<string, ushort[]> _desired = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public GammaEngine(DisplayState state) { _state = state; }

        static IntPtr Dc(string device) => Win32.CreateDC(device, device, null, IntPtr.Zero);

        public static ushort[]? ReadRamp(string device)
        {
            var dc = Dc(device);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var r = new ushort[768];
                return Win32.GetDeviceGammaRamp(dc, r) ? r : null;
            }
            finally { Win32.DeleteDC(dc); }
        }

        static bool WriteRamp(string device, ushort[] ramp)
        {
            var dc = Dc(device);
            if (dc == IntPtr.Zero) return false;
            try { return Win32.SetDeviceGammaRamp(dc, ramp); }
            finally { Win32.DeleteDC(dc); }
        }

        public static ushort[] IdentityRamp()
        {
            var r = new ushort[768];
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < 256; i++) r[c * 256 + i] = (ushort)(i * 257);
            return r;
        }

        public ushort[] Original(string device)
        {
            if (!_state.OriginalRamps.TryGetValue(device, out var o) || o.Length != 768)
            {
                o = ReadRamp(device) ?? IdentityRamp();
                _state.OriginalRamps[device] = o;
                _state.Save();
            }
            return o;
        }

        /// <summary>Capture current ramps as originals (only after a clean previous exit).</summary>
        public void CaptureOriginals(IEnumerable<string> devices)
        {
            foreach (var d in devices)
            {
                var r = ReadRamp(d);
                if (r != null) _state.OriginalRamps[d] = r;
            }
            _state.Save();
        }

        public bool Apply(string device, double[][]? lut)
        {
            if (lut == null)
            {
                // Hands-off unless we changed this monitor: never fight Night Light / f.lux / calibration loaders.
                Errors.Remove(device);
                if (!_desired.Remove(device)) return true;
                return WriteRamp(device, Original(device));
            }
            var orig = Original(device);
            var ramp = new ushort[768];
            {
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < 256; i++)
                    {
                        double pos = Math.Clamp(lut[c][i], 0, 1) * 255;
                        int i0 = (int)Math.Floor(pos), i1 = Math.Min(255, i0 + 1);
                        double t = pos - i0;
                        double v = orig[c * 256 + i0] * (1 - t) + orig[c * 256 + i1] * t;
                        ramp[c * 256 + i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
                    }
            }

            if (_desired.TryGetValue(device, out var cur) && cur.SequenceEqual(ramp) && !Errors.ContainsKey(device))
                return true;

            bool ok = WriteRamp(device, ramp);
            if (ok)
            {
                _desired[device] = ramp;
                Errors.Remove(device);
            }
            else
            {
                Errors[device] = "Windows recusou a curva (forte demais). Libere a faixa de gama nas Configurações.";
                Log.Write($"gamma rejected on {device} (err {Marshal.GetLastWin32Error()})");
            }
            return ok;
        }

        /// <summary>Re-apply desired ramps if something (display change, game, sleep) reset them.</summary>
        public void Verify()
        {
            foreach (var (device, ramp) in _desired.ToList())
            {
                var cur = ReadRamp(device);
                if (cur == null) continue;
                bool diff = false;
                for (int i = 0; i < 768; i += 7) if (Math.Abs(cur[i] - ramp[i]) > 300) { diff = true; break; }
                if (diff)
                {
                    Log.Write($"gamma drift on {device}, reapplying");
                    WriteRamp(device, ramp);
                }
            }
        }

        /// <param name="all">true = every stored original (crash repair); false = only monitors this run changed.</param>
        public void RestoreAll(bool all)
        {
            foreach (var (device, orig) in _state.OriginalRamps)
                if (all || _desired.ContainsKey(device)) WriteRamp(device, orig);
            _desired.Clear();
            Errors.Clear();
        }
    }

    /// <summary>NVIDIA Digital Vibrance per monitor.</summary>
    public sealed class VibranceEngine
    {
        readonly DisplayState _state;
        Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (int min, int max, int def)> _range = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _current = new(StringComparer.OrdinalIgnoreCase);
        public string Status { get; private set; } = "não iniciado";
        public bool Ready { get; private set; }

        public VibranceEngine(DisplayState state) { _state = state; }

        public bool Init()
        {
            Ready = NvApi.Init();
            Status = NvApi.Status;
            if (Ready) Refresh();
            Log.Write("vibrance init: " + Status + $" ({_handles.Count} displays)");
            return Ready;
        }

        public void Refresh()
        {
            if (!Ready) return;
            _handles = NvApi.EnumDisplays();
            _range.Clear();
            foreach (var (name, h) in _handles)
            {
                if (NvApi.TryGet(h, out var cur, out var min, out var max, out var def, out var st))
                {
                    _range[name] = (min, max, def);
                    Log.Write($"dvc {name}: cur={cur} min={min} max={max} def={def}");
                }
                else Log.Write($"dvc {name}: GetDVCInfo failed ({st})");
            }
            if (_handles.Count == 0) Status = "nenhum monitor NVIDIA encontrado";
        }

        public bool Supports(string device) => _range.ContainsKey(device);

        public int? ReadLevel(string device)
            => _handles.TryGetValue(device, out var h) && NvApi.TryGet(h, out var cur, out _, out _, out _, out _) ? cur : null;

        public void CaptureOriginals()
        {
            foreach (var d in _range.Keys)
            {
                var lv = ReadLevel(d);
                if (lv.HasValue) _state.OriginalDvc[d] = lv.Value;
            }
            _state.Save();
        }

        int OriginalOf(string device)
        {
            if (_state.OriginalDvc.TryGetValue(device, out var v)) return v;
            var lv = ReadLevel(device) ?? (_range.TryGetValue(device, out var r) ? r.def : 0);
            _state.OriginalDvc[device] = lv;
            _state.Save();
            return lv;
        }

        /// <summary>percent: 0..100 where 50 = driver default; null = restore original.</summary>
        public string? Apply(string device, double? percent)
        {
            if (!Ready) return "Vibração indisponível: " + Status;
            if (!_handles.TryGetValue(device, out var h) || !_range.TryGetValue(device, out var rg))
                return "Este monitor não está ligado na placa NVIDIA.";

            int level;
            if (percent == null)
            {
                // Hands-off unless we changed this monitor (VibranceGUI / NVCP may own it).
                if (!_current.ContainsKey(device)) return null;
                int r0 = NvApi.Set(h, OriginalOf(device));
                _current.Remove(device);
                return r0 == 0 ? null : $"O driver recusou restaurar o vibrance (código {r0}).";
            }
            else
            {
                double p = Math.Clamp(percent.Value, 0, 100);
                level = p >= 50
                    ? (int)Math.Round(rg.def + (rg.max - rg.def) * (p - 50) / 50)
                    : (int)Math.Round(rg.def - (rg.def - rg.min) * (50 - p) / 50);
                OriginalOf(device);
            }
            if (_current.TryGetValue(device, out var cur) && cur == level) return null;
            int r = NvApi.Set(h, level);
            if (r != 0)
            {
                Log.Write($"dvc set {device} -> {level} failed ({r})");
                return $"O driver recusou o vibrance (código {r}).";
            }
            _current[device] = level;
            return null;
        }

        /// <param name="all">true = every stored original (crash repair); false = only monitors this run changed.</param>
        public void RestoreAll(bool all)
        {
            if (!Ready) return;
            foreach (var (device, lv) in _state.OriginalDvc)
                if ((all || _current.ContainsKey(device)) && _handles.TryGetValue(device, out var h)) NvApi.Set(h, lv);
            _current.Clear();
        }
    }
}
