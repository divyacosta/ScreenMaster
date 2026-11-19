using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>Shared rules for adding a layer anywhere (local preset or agenda).</summary>
    public static class LayerSetup
    {
        static App Ctl => App.Ctl;

        public static IReadOnlyList<string> Devices => Ctl.Engine.Monitors.Select(m => m.DeviceName).ToList();

        public static string? Blocked(LayerKind kind, IEnumerable<Layer> existing) => LayerDefs.BlockReason(kind, existing, Devices);

        /// <summary>
        /// Unique kinds take only the monitors still free; the Monitor adjustment takes exactly one
        /// monitor and starts from that monitor's current values (read over DDC), so adding it changes nothing.
        /// </summary>
        public static void Prepare(Layer l, IEnumerable<Layer> existing, Action? valuesLoaded = null)
        {
            var def = LayerDefs.Get(l.Kind);
            if (!def.Unique || !def.PerMonitor) return;
            var free = LayerDefs.FreeMonitors(l.Kind, existing, Devices);
            if (l.Kind == LayerKind.Grain && free.Count > 0)
            {
                var probe = new Layer { Kind = l.Kind, Monitors = free.Count < Devices.Count ? free.ToList() : new List<string>() };
                l.P["fps"] = MaxFps(probe);
            }
            if (free.Count == 0) return;
            if (def.SingleMonitor)
            {
                l.Monitors = new List<string> { free[0] };
                l.P["brightness_on"] = 1;
                LoadMonitorValues(l, valuesLoaded);
            }
            else if (free.Count < Devices.Count) l.Monitors = free.ToList();
        }

        /// <summary>Fastest refresh rate among the monitors this layer acts on: the grain speed limit.</summary>
        public static int MaxFps(Layer l)
        {
            var mons = Ctl.Engine.Monitors.Where(m => l.AppliesTo(m.DeviceName)).ToList();
            if (mons.Count == 0) mons = Ctl.Engine.Monitors.ToList();
            return mons.Count == 0 ? 60 : mons.Max(m => Native.DisplayModes.RefreshOf(m.DeviceName));
        }

        /// <summary>
        /// Makes a copied layer fit next to existing ones: a unique kind keeps only the monitors still free.
        /// False when there is no room for it (it would duplicate an adjustment that can only exist once).
        /// </summary>
        public static bool Fit(Layer l, IEnumerable<Layer> existing)
        {
            var def = LayerDefs.Get(l.Kind);
            if (!def.Unique) return true;
            var same = existing.Where(x => x.Kind == l.Kind).ToList();
            if (!def.PerMonitor) return same.Count == 0;
            var free = LayerDefs.FreeMonitors(l.Kind, same, Devices);
            var mine = l.Monitors.Count == 0 ? Devices.ToList() : l.Monitors;
            var keep = mine.Where(m => free.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (keep.Count == 0) return false;
            l.Monitors = def.SingleMonitor ? new List<string> { keep[0] } : keep.Count == Devices.Count ? new List<string>() : keep;
            return true;
        }

        /// <summary>Fills the Monitor adjustment's params with the monitor's current values (percent of max).</summary>
        public static void LoadMonitorValues(Layer l, Action? done)
        {
            if (l.Monitors.Count != 1 || !Ctl.Engine.Hardware.IsReady) return;
            var dev = l.Monitors[0];
            var codes = LayerDefs.HardwareCodes.Values.ToArray();
            Ctl.Engine.Hardware.ReadAll(dev, codes, values => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                foreach (var (key, code) in LayerDefs.HardwareCodes)
                {
                    if (!values.TryGetValue(code, out var v) || v.max <= 0) continue;
                    if (key == "temp")
                    {
                        int idx = Array.IndexOf(LayerDefs.HardwareTempVcp, v.cur & 0xFF);
                        if (idx >= 0) l.P[key] = idx;
                    }
                    else l.P[key] = Math.Round(v.cur * 100.0 / v.max);
                }
                done?.Invoke();
            }));
        }

        /// <summary>Monitor adjustment params the chosen monitor supports (all of them while DDC info isn't known).</summary>
        public static bool Supports(Layer l, string key)
        {
            if (l.Kind != LayerKind.MonitorHardware || !LayerDefs.HardwareCodes.TryGetValue(key, out var code)) return true;
            if (l.Monitors.Count != 1 || !Ctl.Engine.Hardware.IsReady) return true;
            var ddc = Ctl.Engine.Hardware.Get(l.Monitors[0]);
            return ddc == null || ddc.Has(code);
        }
    }
}
