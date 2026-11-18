using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ScreenMaster.Core;
using ScreenMaster.Native;

namespace ScreenMaster.Engines
{
    /// <param name="Value">Percent (0..100) when <paramref name="Percent"/>, else a raw/enumerated VCP value.</param>
    /// <param name="Weight">0..1 blend from the user's baseline (layer opacity × schedule fade).</param>
    public readonly record struct HwTarget(double Value, bool Percent, double Weight);

    /// <summary>
    /// Monitor hardware settings over DDC/CI. Every DDC call runs on one worker thread
    /// (they block for tens of ms). Writes are coalesced and throttled, because each one
    /// goes to the monitor's own memory.
    ///
    /// Baseline rule: a code we are not controlling belongs to the user. When a layer takes
    /// control of a code we read its current value as the baseline and persist it (crash-safe);
    /// when the layer lets go we write the baseline back.
    /// </summary>
    public sealed class HardwareEngine
    {
        readonly DisplayState _state;
        readonly Thread _thread;
        readonly BlockingCollection<Action> _jobs = new();
        readonly AutoResetEvent _wake = new(false);
        readonly object _lock = new();
        volatile bool _stop;

        List<DdcMonitor> _monitors = new();
        readonly Dictionary<string, Dictionary<byte, HwTarget>> _desired = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<byte, int>> _written = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<byte, int>> _max = new(StringComparer.OrdinalIgnoreCase);

        public event Action? Ready;
        public bool IsReady { get; private set; }

        public HardwareEngine(DisplayState state)
        {
            _state = state;
            _thread = new Thread(Run) { IsBackground = true, Name = "ScreenMaster DDC" };
        }

        public void Start(bool repairFirst)
        {
            _thread.Start();
            Post(() =>
            {
                _monitors = DdcMonitor.OpenAll();
                if (repairFirst) RestoreFromState();
                IsReady = true;
                Ready?.Invoke();
            });
        }

        // ------------------------------------------------------------------ public API (any thread)

        /// <summary>Snapshot of monitors with DDC support (safe to read on UI thread after Ready).</summary>
        public List<DdcMonitor> Monitors { get { lock (_lock) return _monitors.ToList(); } }

        public DdcMonitor? Get(string device) => Monitors.FirstOrDefault(m => m.Device.Equals(device, StringComparison.OrdinalIgnoreCase) && m.Ok);

        /// <summary>Codes currently controlled by a layer on this monitor.</summary>
        public HashSet<byte> Controlled(string device)
        {
            lock (_lock) return _desired.TryGetValue(device, out var d) ? d.Keys.ToHashSet() : new HashSet<byte>();
        }

        /// <summary>Set the codes layers want on a monitor. Codes absent from <paramref name="targets"/> go back to baseline.</summary>
        public void SetTargets(string device, Dictionary<byte, HwTarget> targets)
        {
            lock (_lock)
            {
                if (_desired.TryGetValue(device, out var cur) && cur.Count == targets.Count && cur.All(kv => targets.TryGetValue(kv.Key, out var v) && v == kv.Value))
                    return;
                _desired[device] = new Dictionary<byte, HwTarget>(targets);
            }
            _wake.Set();
        }

        /// <summary>Percent (0..100) → raw value for a code, using the monitor's reported max (default 100).</summary>
        public int Raw(string device, byte code, double percent)
        {
            int max = 100;
            lock (_lock) if (_max.TryGetValue(device, out var m) && m.TryGetValue(code, out var mx) && mx > 0) max = mx;
            return (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * max);
        }

        /// <summary>Baseline (user's own value) for a code if known.</summary>
        public int? Baseline(string device, byte code)
        {
            lock (_lock)
                return _state.OriginalVcp.TryGetValue(device, out var d) && d.TryGetValue(Key(code), out var v) ? v : null;
        }

        /// <summary>Read current values (worker thread) and return them on completion.</summary>
        public void ReadAll(string device, byte[] codes, Action<Dictionary<byte, (int cur, int max)>> done)
        {
            Post(() =>
            {
                var res = new Dictionary<byte, (int, int)>();
                var m = _monitors.FirstOrDefault(x => x.Device.Equals(device, StringComparison.OrdinalIgnoreCase));
                if (m != null)
                    foreach (var c in codes)
                        if (m.Has(c) && m.TryGet(c, out var cur, out var max))
                        {
                            res[c] = (cur, max);
                            lock (_lock) Max(device)[c] = max;
                        }
                done(res);
            });
        }

        /// <summary>User change from the Monitors page (only for codes no layer controls).</summary>
        public void SetDirect(string device, byte code, int value)
        {
            Post(() =>
            {
                lock (_lock) if (_desired.TryGetValue(device, out var d) && d.ContainsKey(code)) return;
                var m = _monitors.FirstOrDefault(x => x.Device.Equals(device, StringComparison.OrdinalIgnoreCase));
                if (m == null) return;
                m.Set(code, value);
                Thread.Sleep(60);
            });
        }

        /// <summary>Write every baseline back. all=true: everything stored (crash repair); false: only codes we wrote.</summary>
        public void RestoreAll(bool all, bool wait)
        {
            lock (_lock) _desired.Clear();
            var done = new ManualResetEventSlim(false);
            Post(() =>
            {
                if (all) RestoreFromState();
                else Flush();
                done.Set();
            });
            if (wait) done.Wait(4000);
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            _jobs.CompleteAdding();
            _thread.Join(3000);
            foreach (var m in _monitors) m.Dispose();
        }

        // ------------------------------------------------------------------ worker

        static string Key(byte code) => code.ToString("X2");

        Dictionary<byte, int> Max(string device)
        {
            if (!_max.TryGetValue(device, out var m)) _max[device] = m = new Dictionary<byte, int>();
            return m;
        }

        void Post(Action a)
        {
            try { _jobs.Add(a); _wake.Set(); } catch (InvalidOperationException) { }
        }

        void Run()
        {
            bool pending = false;
            while (!_stop)
            {
                _wake.WaitOne(pending ? 100 : 500);
                while (_jobs.TryTake(out var job))
                {
                    try { job(); } catch (Exception ex) { Log.Write("ddc job: " + ex.Message); }
                }
                pending = false;
                if (IsReady)
                {
                    try { pending = Flush(force: false); } catch (Exception ex) { Log.Write("ddc flush: " + ex.Message); }
                }
            }
        }

        /// <summary>Re-open physical monitor handles (after a display change / resume). Keeps desired/written state by device name.</summary>
        public void Reopen()
        {
            Post(() =>
            {
                List<DdcMonitor> old;
                lock (_lock) old = _monitors;
                foreach (var m in old) m.Dispose();
                var fresh = DdcMonitor.OpenAll();
                lock (_lock) _monitors = fresh;
                Log.Write($"ddc reopened: {fresh.Count(m => m.Ok)} monitor(s)");
                Ready?.Invoke();
            });
        }

        readonly Dictionary<string, DateTime> _lastFlush = new(StringComparer.OrdinalIgnoreCase);
        static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Make hardware match _desired (and baseline for released codes). Each monitor is written at most
        /// once per 250 ms (slider drags, fades) — returns true when something is waiting for that window.
        /// </summary>
        bool Flush(bool force = true)
        {
            bool pending = false;
            List<DdcMonitor> monitors;
            lock (_lock) monitors = _monitors;
            foreach (var m in monitors)
            {
                if (!m.Ok) continue;
                if (!force && _lastFlush.TryGetValue(m.Device, out var last) && DateTime.UtcNow - last < MinInterval)
                {
                    pending = true;
                    continue;
                }
                bool wroteAny = false;
                Dictionary<byte, HwTarget> want;
                lock (_lock) want = _desired.TryGetValue(m.Device, out var d) ? new Dictionary<byte, HwTarget>(d) : new();
                // RGB gains only take effect in the monitor's "user" color preset.
                if (!want.ContainsKey(Vcp.ColorPreset) && (want.ContainsKey(Vcp.GainR) || want.ContainsKey(Vcp.GainG) || want.ContainsKey(Vcp.GainB))
                    && m.Supported.TryGetValue(Vcp.ColorPreset, out var presets) && presets.Contains(0x0B))
                    want[Vcp.ColorPreset] = new HwTarget(0x0B, false, 1);
                if (!_written.TryGetValue(m.Device, out var written)) _written[m.Device] = written = new Dictionary<byte, int>();

                // released codes -> baseline
                foreach (var code in written.Keys.Where(c => !want.ContainsKey(c)).ToList())
                {
                    var b = Baseline(m.Device, code);
                    if (b.HasValue) { m.Set(code, b.Value); Thread.Sleep(60); wroteAny = true; }
                    written.Remove(code);
                    // The value is the user's again: forget the stored baseline so a later crash repair can't write a stale one.
                    lock (_lock)
                        if (_state.OriginalVcp.TryGetValue(m.Device, out var d) && d.Remove(Key(code))) _state.Save();
                }

                // Color preset first, so gains land in the right (user) preset.
                foreach (var (code, t) in want.OrderBy(kv => kv.Key == Vcp.ColorPreset ? 0 : 1))
                {
                    if (!m.Has(code)) continue;
                    EnsureBaseline(m, code);
                    int value = Resolve(m.Device, code, t);
                    if (written.TryGetValue(code, out var w) && w == value) continue;
                    if (m.Set(code, value)) written[code] = value;
                    Thread.Sleep(60);
                    wroteAny = true;
                }
                if (wroteAny) _lastFlush[m.Device] = DateTime.UtcNow;
            }
            return pending;
        }

        /// <summary>Final raw value: percent → raw by the monitor's max, then blended from the baseline by weight (fades/opacity).</summary>
        int Resolve(string device, byte code, HwTarget t)
        {
            int max = 100;
            lock (_lock) if (_max.TryGetValue(device, out var mm) && mm.TryGetValue(code, out var mx) && mx > 0) max = mx;
            int target = t.Percent ? (int)Math.Round(Math.Clamp(t.Value, 0, 100) / 100.0 * max) : (int)t.Value;
            var b = Baseline(device, code);
            if (b == null || t.Weight >= 0.999) return target;
            if (!t.Percent) return t.Weight >= 0.5 ? target : b.Value; // enumerated values can't be blended
            return (int)Math.Round(b.Value + (target - b.Value) * Math.Clamp(t.Weight, 0, 1));
        }

        void EnsureBaseline(DdcMonitor m, byte code)
        {
            lock (_lock)
            {
                if (!_state.OriginalVcp.TryGetValue(m.Device, out var d))
                    _state.OriginalVcp[m.Device] = d = new Dictionary<string, int>();
                // Re-read the baseline every time we take control: while uncontrolled, the value is the user's.
                if (_written.TryGetValue(m.Device, out var w) && w.ContainsKey(code)) return;
            }
            if (m.TryGet(code, out var cur, out var max))
            {
                lock (_lock)
                {
                    _state.OriginalVcp[m.Device][Key(code)] = cur;
                    Max(m.Device)[code] = max;
                    _state.Save();
                }
            }
        }

        void RestoreFromState()
        {
            foreach (var m in _monitors)
            {
                if (!_state.OriginalVcp.TryGetValue(m.Device, out var d)) continue;
                foreach (var (k, v) in d.OrderBy(kv => kv.Key == Key(Vcp.ColorPreset) ? 0 : 1))
                {
                    if (byte.TryParse(k, System.Globalization.NumberStyles.HexNumber, null, out var code) && m.Has(code))
                    {
                        m.Set(code, v);
                        Thread.Sleep(60);
                    }
                }
            }
            _written.Clear();
            lock (_lock) { _state.OriginalVcp.Clear(); _state.Save(); }
        }
    }
}
