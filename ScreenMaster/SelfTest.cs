using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ScreenMaster.Core;
using ScreenMaster.Engines;

namespace ScreenMaster
{
    /// <summary>
    /// Applies a mild value on every engine, reads it back from the system, reverts,
    /// and writes pass/fail to %APPDATA%\ScreenMaster\selftest.txt. Screen changes last ~1s.
    /// </summary>
    public static class SelfTest
    {
        static string ArrangementTime(double m) => UI.ArrangementView.Fmt(m);

        /// <summary>Read-only: writes current gamma mid-values and DVC levels to probe.txt.</summary>
        public static int Probe()
        {
            var sb = new StringBuilder();
            var state = DisplayState.Load();
            sb.AppendLine("applied=" + state.Applied);
            var vib = new VibranceEngine(state);
            vib.Init();
            foreach (var m in Native.Win32.GetMonitors())
            {
                var r = GammaEngine.ReadRamp(m.DeviceName);
                sb.AppendLine($"{m.DeviceName} gamma[64]={r?[64]} gamma[128]={r?[128]} gamma[192]={r?[192]} dvc={vib.ReadLevel(m.DeviceName)}");
            }
            Paths.Ensure();
            File.WriteAllText(Path.Combine(Paths.Dir, "probe.txt"), sb.ToString());
            return 0;
        }

        public static int Run()
        {
            var sb = new StringBuilder();
            int fails = 0;
            void R(string name, bool ok, string detail = "")
            {
                sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name} {detail}");
                if (!ok) fails++;
            }

            // ---- agenda logic (pure, no screen changes)
            {
                var mon = new DateTime(2026, 11, 16); // a Monday
                var lane = new Lane { Param = "opacity" };
                foreach (var (t, v) in new[] { (7 * 60.0, 0.0), (19 * 60.0, 0.0), (22 * 60.0, 100.0), (6 * 60.0, 100.0) }) lane.Points.Add(new Keyframe { T = t, V = v });
                double E(double h) => Automation.Eval(lane, h * 60, mon, null, false) ?? -1;
                R("automação: 12:00 = 0", Math.Abs(E(12)) < 0.01, $"{E(12):0.##}");
                R("automação: 20:30 rampa 50%", Math.Abs(E(20.5) - 50) < 0.5, $"{E(20.5):0.##}");
                R("automação: 02:00 cheio (atravessa meia-noite)", Math.Abs(E(2) - 100) < 0.01, $"{E(2):0.##}");
                R("automação: 06:30 descendo 50%", Math.Abs(E(6.5) - 50) < 0.5, $"{E(6.5):0.##}");
                lane.Points[1].Curve = 0.8; // the 19:00 point starts the 19:00→22:00 segment
                R("automação: curva entorta o trecho", E(20.5) > 55, $"{E(20.5):0.##}");

                // profiles own weekdays; forced by hand until midnight; a program rule wins
                var s = new Settings();
                var work = new DayProfile { Name = "Trabalho" };
                var game = new DayProfile { Name = "Jogo" };
                s.Profiles.AddRange(new[] { work, game });
                for (int d = 0; d < 5; d++) s.Week[d] = work.Id;
                s.Week[5] = game.Id;
                R("perfil: segunda = Trabalho", Automation.ProfileFor(s, mon) == work);
                R("perfil: sábado = Jogo", Automation.ProfileFor(s, mon.AddDays(5)) == game);
                R("perfil: domingo sem perfil = sem agenda", Automation.ProfileFor(s, mon.AddDays(6)) == null);
                R("perfil: dias do perfil", Automation.DaysOf(s, work) == 0x1F && Automation.DaysOf(s, game) == 0x20);
                s.ForcedProfile = game.Id; s.ForcedUntil = mon.AddDays(1);
                R("perfil: forçado vale até meia-noite", Automation.ProfileFor(s, mon.AddHours(23)) == game && Automation.ProfileFor(s, mon.AddDays(1).AddHours(1)) == work);
                R("perfil: regra de programa vence o forçado", Automation.ProfileFor(s, mon.AddHours(10), work.Id) == work);

                // per-lane weekdays: seg-sex lane + fim de semana lane of the same parameter
                {
                    var wk = new Lane { Param = "opacity", Days = 0x1F, Points = { new Keyframe { T = 0, V = 10 } } };
                    var we = new Lane { Param = "opacity", Days = 0x60, Points = { new Keyframe { T = 0, V = 90 } } };
                    var both = new[] { wk, we };
                    R("dias: segunda usa a faixa seg-sex", Automation.ActiveLanes(both, DayOfWeek.Monday).Single() == wk);
                    R("dias: domingo usa a faixa de fim de semana", Automation.ActiveLanes(both, DayOfWeek.Sunday).Single() == we);
                    R("dias: sem conflito", !Automation.Conflicts(both, wk).Any());
                    we.Days = 0x61; // also Monday
                    R("dias: conflito detectado", Automation.Conflicts(both, wk).Single() == we);
                    R("dias: no conflito vale a de baixo", Automation.ActiveLanes(both, DayOfWeek.Monday).Single() == we);
                    R("dias: texto", Automation.DaysText(0x1F) == "seg a sex" && Automation.DaysText(0x60) == "fim de semana" && Automation.DaysText(0x7F) == "todos os dias");

                    // a track whose lanes are all off today does nothing
                    var bc = Layer.Create(LayerKind.BrightnessContrast);
                    var prof = new DayProfile();
                    prof.Tracks.Add(new AgendaTrack { Layer = bc, Lanes = { new Lane { Param = "opacity", Days = 0x60, Points = { new Keyframe { T = 0, V = 100 } } } } });
                    var ds = new Settings { ScheduleEnabled = true, Profiles = { prof }, Week = Enumerable.Repeat(prof.Id, 7).ToArray(), Presets = { new Preset { Name = "V" } } };
                    var stMon = Stack.Resolve(ds, ds.Presets[0], null, null, mon.AddHours(12), simulated: true, bypass: false);
                    var stSun = Stack.Resolve(ds, ds.Presets[0], null, null, mon.AddDays(6).AddHours(12), simulated: true, bypass: false);
                    R("dias: faixa de fim de semana não age na segunda", stMon.Agenda.Count == 0, $"{stMon.Agenda.Count}");
                    R("dias: faixa de fim de semana age no domingo", stSun.Agenda.Count == 1, $"{stSun.Agenda.Count}");
                }

                // pinned simulation: the monitor's hardware follows the pinned time, not while dragging
                {
                    var hw = Layer.Create(LayerKind.MonitorHardware); hw.P["brightness_on"] = 1; hw.Monitors = new() { "X" };
                    var prof = new DayProfile();
                    prof.Tracks.Add(new AgendaTrack { Layer = hw, Lanes = { new Lane { Param = "brightness", Points = { new Keyframe { T = 0, V = 20 }, new Keyframe { T = 720, V = 80 } } } } });
                    var hs = new Settings { ScheduleEnabled = true, Profiles = { prof }, Week = Enumerable.Repeat(prof.Id, 7).ToArray(), Presets = { new Preset { Name = "V" } } };
                    double B(DateTime? hwWhen) => Stack.Resolve(hs, hs.Presets[0], null, null, mon.AddHours(12), simulated: true, bypass: false, hardwareWhen: hwWhen).Agenda[0].layer.Get("brightness");
                    R("simulação parada: monitor segue o horário", Math.Abs(B(mon.AddHours(12)) - 80) < 0.5, $"{B(mon.AddHours(12)):0}");
                    R("simulação arrastando: monitor fica no horário fixado", Math.Abs(B(mon) - 20) < 0.5, $"{B(mon):0}");
                }

                // Camadas switch: the local preset goes off, the agenda keeps acting
                {
                    var bc = Layer.Create(LayerKind.BrightnessContrast);
                    var pf = Layer.Create(LayerKind.PhotoFilter);
                    var prof = new DayProfile();
                    prof.Tracks.Add(new AgendaTrack { Layer = pf, Lanes = { new Lane { Param = "opacity", Points = { new Keyframe { T = 0, V = 100 } } } } });
                    var cs = new Settings { ScheduleEnabled = true, LayersEnabled = false, Profiles = { prof }, Week = Enumerable.Repeat(prof.Id, 7).ToArray(), Presets = { new Preset { Name = "L", Layers = { bc } } } };
                    var st = Stack.Resolve(cs, cs.Presets[0], null, null, mon.AddHours(12), simulated: true, bypass: false);
                    R("camadas desligadas: preset sai, agenda fica", st.Effective.All(x => x.Id != bc.Id) && st.Effective.Any(x => x.Id == pf.Id));
                }

                // adjustments that can exist only once
                {
                    var mons = new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" };
                    var vib = Layer.Create(LayerKind.Vibrance);
                    R("únicos: vibrance em todos bloqueia outro", LayerDefs.BlockReason(LayerKind.Vibrance, new[] { vib }, mons) != null);
                    vib.Monitors = new() { mons[0] };
                    R("únicos: vibrance num monitor deixa o outro livre", LayerDefs.BlockReason(LayerKind.Vibrance, new[] { vib }, mons) == null
                        && LayerDefs.FreeMonitors(LayerKind.Vibrance, new[] { vib }, mons).SequenceEqual(new[] { mons[1] }));
                    var hw1 = Layer.Create(LayerKind.MonitorHardware); hw1.Monitors = new() { mons[0] };
                    var hw2 = Layer.Create(LayerKind.MonitorHardware); hw2.Monitors = new() { mons[1] };
                    R("únicos: Monitor para os dois bloqueia", LayerDefs.BlockReason(LayerKind.MonitorHardware, new[] { hw1, hw2 }, mons) != null);
                    R("únicos: tema do Windows só uma vez", LayerDefs.BlockReason(LayerKind.WindowsTheme, new[] { Layer.Create(LayerKind.WindowsTheme) }, mons) != null);
                    R("únicos: Curves repete à vontade", LayerDefs.BlockReason(LayerKind.Curves, new[] { Layer.Create(LayerKind.Curves) }, mons) == null);
                }

                // new color math
                {
                    var k65 = ColorMath.KelvinGains(6500);
                    var k30 = ColorMath.KelvinGains(3000);
                    R("kelvin: 6500K neutro", k65.All(v => Math.Abs(v - 1) < 0.01), string.Join("/", k65.Select(v => v.ToString("0.###"))));
                    R("kelvin: 3000K quente", k30[0] > k30[2] && k30.Max() <= 1.0001, string.Join("/", k30.Select(v => v.ToString("0.###"))));
                    bool rowsOk = true;
                    for (int t = 0; t < 4; t++)
                    {
                        var cb = Layer.Create(LayerKind.ColorBlind); cb.P["type"] = t;
                        var m = ColorMath.MatrixFor(cb).M;
                        for (int r = 0; r < 3; r++) if (Math.Abs(m[r * 4] + m[r * 4 + 1] + m[r * 4 + 2] - 1) > 0.02) rowsOk = false;
                    }
                    R("daltonismo: branco continua branco", rowsOk);
                    var lgg = ColorMath.ToneFor(Layer.Create(LayerKind.LiftGammaGain));
                    R("lift/gamma/gain: padrão não muda nada", Enumerable.Range(0, 3).All(c => Math.Abs(lgg(c, 0.5) - 0.5) < 1e-3 && Math.Abs(lgg(c, 0) ) < 1e-3 && Math.Abs(lgg(c, 1) - 1) < 1e-3));
                }

                // sun anchor (São Paulo, ~05:00 sunrise / ~18:30 sunset in November)
                var sun = Sun.Times(mon, -23.55, -46.63);
                R("sol: São Paulo plausível", sun != null && sun.Value.rise is > 4 * 60 and < 6 * 60 && sun.Value.set is > 18 * 60 and < 19.5 * 60,
                  sun != null ? $"nascer {ArrangementTime(sun.Value.rise)} pôr {ArrangementTime(sun.Value.set)}" : "null");

                // linked automation: a local preset layer's param follows the agenda
                {
                    var bc = Layer.Create(LayerKind.BrightnessContrast);
                    var local = new Preset { Name = "L", Layers = { bc } };
                    var ls = new Settings { Presets = { local }, ScheduleEnabled = true };
                    var prof = new DayProfile();
                    ls.Profiles.Add(prof);
                    ls.Week = Enumerable.Repeat(prof.Id, 7).ToArray();
                    var ll = prof.AddLinked(bc.Id, "brightness");
                    ll.Points.Add(new Keyframe { T = 8 * 60, V = 0 });
                    ll.Points.Add(new Keyframe { T = 20 * 60, V = -40 });
                    var st = Stack.Resolve(ls, local, null, null, mon.AddHours(14), simulated: true, bypass: false);
                    double b14 = st.Effective[0].Get("brightness");
                    ll.Overridden = true;
                    bc.P["brightness"] = 10;
                    var st2 = Stack.Resolve(ls, local, null, null, mon.AddHours(14), simulated: true, bypass: false);
                    R("vinculada: 14:00 = -20 (meio da rampa)", Math.Abs(b14 + 20) < 0.5, $"{b14:0.#}");
                    R("vinculada: sobrescrito usa valor manual", Math.Abs(st2.Effective[0].Get("brightness") - 10) < 0.01);
                    R("vinculada: original do preset intacto", Math.Abs(bc.Get("brightness") - 10) < 0.01 && st.Effective[0] != bc);
                }

                // migration of a v2 block
                var p = new Preset { Name = "N", Layers = { Layer.Create(LayerKind.PhotoFilter) } };
                var tracks = Automation.FromBlock(new ScheduleBlock { Start = 22 * 60, End = 7 * 60, Fade = 30 }, p).ToList();
                double M(double h) => Automation.Eval(tracks[0].Lanes[0], h * 60, mon, null, false) ?? -1;
                R("migração v2: 23:00 cheio, 12:00 zero", Math.Abs(M(23) - 100) < 0.01 && Math.Abs(M(12)) < 0.01, $"{M(23):0} / {M(12):0}");
            }

            var engine = new Engine();
            try
            {
                engine.Start();
                sb.AppendLine("Monitores: " + string.Join(", ", engine.Monitors.Select(m => m.Label + " " + m.DeviceName)));
                sb.AppendLine("Recuperado de crash: " + engine.RecoveredFromCrash);

                // ---- matrix
                R("matrix init", engine.Matrix.Ready, engine.Matrix.Status);
                if (engine.Matrix.Ready)
                {
                    var m = ColorMath.Saturate(1.1);
                    bool set = engine.Matrix.Apply(m);
                    var back = engine.Matrix.Read();
                    float expected = (float)m.M[0];
                    R("matrix apply+readback", set && back != null && Math.Abs(back[0] - expected) < 1e-3,
                      $"set={set} read[0]={back?[0]} expected={expected}");
                    engine.Matrix.Reset();
                    back = engine.Matrix.Read();
                    R("matrix reset", back != null && Math.Abs(back[0] - 1) < 1e-4, $"read[0]={back?[0]}");
                }

                // ---- gamma (mild brightness)
                foreach (var mon in engine.Monitors)
                {
                    var orig = GammaEngine.ReadRamp(mon.DeviceName);
                    R($"gamma read {mon.DeviceName}", orig != null);
                    var layer = Layer.Create(LayerKind.BrightnessContrast);
                    layer.P["brightness"] = 8;
                    var lut = ColorMath.ComposeGamma(new[] { layer }, mon.DeviceName);
                    engine.State.Applied = true; engine.State.Save();
                    bool ok = engine.Gamma.Apply(mon.DeviceName, lut);
                    var now = GammaEngine.ReadRamp(mon.DeviceName);
                    bool changed = now != null && orig != null && now[128] != orig[128];
                    R($"gamma apply {mon.DeviceName}", ok && changed, $"ok={ok} mid {orig?[128]} -> {now?[128]}");

                    var strong = Layer.Create(LayerKind.Threshold);
                    bool okStrong = engine.Gamma.Apply(mon.DeviceName, ColorMath.ComposeGamma(new[] { strong }, mon.DeviceName));
                    sb.AppendLine($"   info: Threshold aceito pelo Windows em {mon.DeviceName}: {okStrong}");
                    engine.Gamma.Apply(mon.DeviceName, null);
                    var restored = GammaEngine.ReadRamp(mon.DeviceName);
                    R($"gamma restore {mon.DeviceName}", restored != null && orig != null && restored.SequenceEqual(orig));
                }

                // ---- vibrance
                R("nvapi init", engine.Vibrance.Ready, engine.Vibrance.Status);
                foreach (var mon in engine.Monitors)
                {
                    if (!engine.Vibrance.Supports(mon.DeviceName)) { sb.AppendLine($"   info: sem DVC em {mon.DeviceName}"); continue; }
                    var before = engine.Vibrance.ReadLevel(mon.DeviceName);
                    var err = engine.Vibrance.Apply(mon.DeviceName, 60);
                    var during = engine.Vibrance.ReadLevel(mon.DeviceName);
                    R($"dvc apply {mon.DeviceName}", err == null && during != before, $"err={err} {before} -> {during}");
                    engine.Vibrance.Apply(mon.DeviceName, null);
                    var after = engine.Vibrance.ReadLevel(mon.DeviceName);
                    R($"dvc restore {mon.DeviceName}", after == before, $"-> {after}");
                }

                // ---- DDC/CI (mild brightness nudge, restored)
                for (int i = 0; i < 100 && !engine.Hardware.IsReady; i++) Thread.Sleep(100);
                R("ddc ready", engine.Hardware.IsReady);
                foreach (var hm in engine.Hardware.Monitors)
                    sb.AppendLine($"   info: ddc handle {hm.Device} ok={hm.Ok} model={hm.Model} codes={hm.Supported.Count}");
                void Nudge(string tag)
                {
                foreach (var mon in engine.Monitors)
                {
                    var ddc = engine.Hardware.Get(mon.DeviceName);
                    sb.AppendLine($"   info: {mon.DeviceName} nome='{mon.Name}' ddc={(ddc != null ? $"{ddc.Model} ({ddc.Supported.Count} códigos)" : "não")}");
                    if (ddc == null || !ddc.Has(Native.Vcp.Brightness)) continue;
                    var layer = Layer.Create(LayerKind.MonitorHardware);
                    layer.Monitors.Add(mon.DeviceName);
                    ddc.TryGet(Native.Vcp.Brightness, out var before, out var max);
                    double target = Math.Clamp(before > 50 ? before - 5 : before + 5, 0, max);
                    layer.P["brightness_on"] = 1; layer.P["brightness"] = target * 100.0 / max;
                    engine.State.Applied = true; engine.State.Save();
                    engine.Apply(new() { layer }, true, true);
                    Thread.Sleep(900);
                    ddc.TryGet(Native.Vcp.Brightness, out var during, out _);
                    engine.Apply(new(), true, true);
                    Thread.Sleep(900);
                    ddc.TryGet(Native.Vcp.Brightness, out var after, out _);
                    R($"ddc brightness {tag} {mon.DeviceName}", during == (int)target && after == before, $"{before} -> {during} -> {after}");
                }
                }
                Nudge("");
                bool reopened = false;
                engine.Hardware.Ready += () => reopened = true;
                engine.Hardware.Reopen();
                for (int i = 0; i < 100 && !reopened; i++) Thread.Sleep(100);
                R("ddc reopen", reopened);
                Nudge("após reabrir");

                // ---- overlay (grain, short)
                var g = Layer.Create(LayerKind.Grain);
                engine.Overlay.Update(engine.Monitors, new() { g }, true);
                Thread.Sleep(700);
                R("overlay grain (sem exceção)", true, engine.Overlay.CaptureStatus);
                engine.Overlay.Clear();
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                R("exception", false, ex.ToString());
            }
            finally
            {
                engine.Shutdown();
            }

            sb.AppendLine(fails == 0 ? "TUDO OK" : $"{fails} FALHA(S)");
            Paths.Ensure();
            File.WriteAllText(Path.Combine(Paths.Dir, "selftest.txt"), sb.ToString());
            return fails;
        }
    }
}
