using System;
using System.Collections.Generic;
using System.Linq;
using ScreenMaster.Core;
using ScreenMaster.Native;

namespace ScreenMaster.Engines
{
    /// <summary>Coordinates the five engines and owns crash-safety state.</summary>
    public sealed class Engine
    {
        public DisplayState State { get; }
        public MatrixEngine Matrix { get; } = new();
        public GammaEngine Gamma { get; }
        public VibranceEngine Vibrance { get; }
        public OverlayEngine Overlay { get; } = new();
        public HardwareEngine Hardware { get; }
        public SystemEngine System { get; }
        public List<MonitorInfo> Monitors { get; private set; } = new();

        /// <summary>Per-layer error messages from the last apply.</summary>
        public Dictionary<Guid, string> LayerErrors { get; } = new();
        public bool RecoveredFromCrash { get; private set; }

        bool _shutdown;

        public Engine()
        {
            State = DisplayState.Load();
            Gamma = new GammaEngine(State);
            Vibrance = new VibranceEngine(State);
            Hardware = new HardwareEngine(State);
            System = new SystemEngine(State);
        }

        static bool PendingVcp(DisplayState s) => s.OriginalVcp.Values.Any(d => d.Count > 0);

        /// <summary>Must run before anything else: repairs the screen after an unclean exit, then captures originals.</summary>
        public void Start()
        {
            Monitors = Win32.GetMonitors();
            Vibrance.Init();

            bool repairVcp = State.Applied || PendingVcp(State);
            if (State.Applied)
            {
                Log.Write("previous run did not exit cleanly - restoring originals");
                RecoveredFromCrash = true;
                Gamma.RestoreAll(all: true);
                Vibrance.RestoreAll(all: true);
                State.Applied = false;
                State.Save();
            }
            else
            {
                Gamma.CaptureOriginals(Monitors.Select(m => m.DeviceName));
                Vibrance.CaptureOriginals();
            }

            Hardware.Start(repairFirst: repairVcp);
            System.Release(); // a theme left by a previous run goes back to the user's
            Matrix.Init();
            Overlay.Start();
        }

        /// <summary>Standalone repair: restore originals if a previous run left the hardware changed.</summary>
        public static bool RepairOnly()
        {
            var state = DisplayState.Load();
            bool vcp = PendingVcp(state);
            if (state.OriginalTheme != null) SystemEngine.Repair(state);
            if (!state.Applied && !vcp) return false;
            var gamma = new GammaEngine(state);
            var vib = new VibranceEngine(state);
            vib.Init();
            gamma.RestoreAll(all: true);
            vib.RestoreAll(all: true);
            if (vcp)
            {
                var hw = new HardwareEngine(state);
                hw.Start(repairFirst: true);
                hw.RestoreAll(all: true, wait: true);
                hw.Stop();
            }
            state.Applied = false;
            state.Save();
            Log.Write("--restore: originals restored");
            return true;
        }

        public void RefreshMonitors()
        {
            MonitorNames.Clear();
            Monitors = Win32.GetMonitors();
            Vibrance.Refresh();
        }

        void MarkApplied()
        {
            if (State.Applied) return;
            State.Applied = true;
            State.Save();
        }

        static EngineKind EngineOf(Layer l) => LayerDefs.Get(l.Kind).Engine;

        /// <summary>Apply the effective layer stack (base preset + schedule, already weighted).</summary>
        public void Apply(List<Layer> layers, bool master, bool hideOverlayFromCapture)
        {
            if (_shutdown) return;
            LayerErrors.Clear();
            if (!master)
            {
                RestoreAll();
                return;
            }

            // ---- matrix (global)
            var m = ColorMath.ComposeMatrix(layers);
            if (!Matrix.Apply(m))
                foreach (var l in layers.Where(l => l.Enabled && EngineOf(l) == EngineKind.Matrix))
                    LayerErrors[l.Id] = Matrix.Ready ? Matrix.Status : "Filtro de cor do Windows indisponível: " + Matrix.Status;

            bool touchesHardware = layers.Any(l => l.Enabled && l.Opacity > 0 &&
                EngineOf(l) is EngineKind.Gamma or EngineKind.Driver or EngineKind.Hardware);
            if (touchesHardware) MarkApplied();

            foreach (var mon in Monitors)
            {
                var dev = mon.DeviceName;

                // ---- gamma
                var lut = ColorMath.ComposeGamma(layers, dev);
                if (!Gamma.Apply(dev, lut) && Gamma.Errors.TryGetValue(dev, out var gerr))
                    foreach (var l in layers.Where(l => l.Enabled && l.AppliesTo(dev) && EngineOf(l) == EngineKind.Gamma))
                        LayerErrors[l.Id] = gerr;

                // ---- vibrance (last enabled layer wins)
                var vib = layers.LastOrDefault(l => l.Enabled && l.Kind == LayerKind.Vibrance && l.AppliesTo(dev));
                if (vib != null)
                {
                    double pct = 50 + (vib.Get("level") - 50) * Math.Clamp(vib.Opacity / 100.0, 0, 1);
                    var err = Vibrance.Apply(dev, pct);
                    if (err != null && (!vib.AllMonitors || Vibrance.Supports(dev) || !Vibrance.Ready))
                        LayerErrors[vib.Id] = err;
                }
                else if (Vibrance.Supports(dev)) Vibrance.Apply(dev, null);

                // ---- monitor hardware (last layer wins per setting)
                var targets = new Dictionary<byte, HwTarget>();
                foreach (var l in layers.Where(l => l.Enabled && l.Kind == LayerKind.MonitorHardware && l.AppliesTo(dev)))
                {
                    double w = Math.Clamp(l.Opacity / 100.0, 0, 1);
                    if (w <= 0) continue;
                    if (l.IsOn("brightness")) targets[Vcp.Brightness] = new HwTarget(l.Get("brightness"), true, w);
                    if (l.IsOn("contrast")) targets[Vcp.Contrast] = new HwTarget(l.Get("contrast"), true, w);
                    if (l.IsOn("sharpness")) targets[Vcp.Sharpness] = new HwTarget(l.Get("sharpness"), true, w);
                    if (l.IsOn("blackR")) targets[Vcp.BlackR] = new HwTarget(l.Get("blackR"), true, w);
                    if (l.IsOn("blackG")) targets[Vcp.BlackG] = new HwTarget(l.Get("blackG"), true, w);
                    if (l.IsOn("blackB")) targets[Vcp.BlackB] = new HwTarget(l.Get("blackB"), true, w);
                    if (l.IsOn("volume")) targets[Vcp.Volume] = new HwTarget(l.Get("volume"), true, w);
                    if (l.IsOn("gainR")) targets[Vcp.GainR] = new HwTarget(l.Get("gainR"), true, w);
                    if (l.IsOn("gainG")) targets[Vcp.GainG] = new HwTarget(l.Get("gainG"), true, w);
                    if (l.IsOn("gainB")) targets[Vcp.GainB] = new HwTarget(l.Get("gainB"), true, w);
                    if (l.IsOn("temp"))
                    {
                        int idx = Math.Clamp((int)l.Get("temp"), 0, LayerDefs.HardwareTempVcp.Length - 1);
                        targets[Vcp.ColorPreset] = new HwTarget(LayerDefs.HardwareTempVcp[idx], false, w);
                    }
                    if (Hardware.IsReady && Hardware.Get(dev) == null)
                        LayerErrors[l.Id] = $"O monitor {mon.Short} não responde a DDC/CI (ative DDC/CI no menu do monitor).";
                }
                Hardware.SetTargets(dev, targets);
            }

            // ---- overlay
            Overlay.Update(Monitors, layers, hideOverlayFromCapture);

            // ---- Windows settings (theme)
            try { System.Apply(layers); } catch (Exception ex) { Log.Write("system apply: " + ex.Message); }

            if (!touchesHardware && State.Applied)
            {
                // Hardware is back at originals (applied above), clear the flag.
                State.Applied = false;
                State.Save();
            }
        }

        public void RestoreAll()
        {
            try { Matrix.Reset(); } catch (Exception ex) { Log.Write("matrix reset: " + ex.Message); }
            try { Gamma.RestoreAll(all: false); } catch (Exception ex) { Log.Write("gamma restore: " + ex.Message); }
            try { Vibrance.RestoreAll(all: false); } catch (Exception ex) { Log.Write("dvc restore: " + ex.Message); }
            try { Hardware.RestoreAll(all: false, wait: false); } catch (Exception ex) { Log.Write("ddc restore: " + ex.Message); }
            try { Overlay.Clear(); } catch (Exception ex) { Log.Write("overlay clear: " + ex.Message); }
            try { System.Release(); } catch (Exception ex) { Log.Write("system restore: " + ex.Message); }
            if (State.Applied) { State.Applied = false; State.Save(); }
        }

        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            RestoreAll();
            try { Hardware.RestoreAll(all: false, wait: true); Hardware.Stop(); } catch { }
            try { Overlay.Stop(); } catch { }
            try { Matrix.Shutdown(); } catch { }
            Log.Write("clean shutdown");
        }
    }
}
