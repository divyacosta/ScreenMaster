using System;
using System.Linq;
using System.Threading;
using ScreenMaster.Core;

namespace ScreenMaster
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--selftest")) return SelfTest.Run();
            if (args.Contains("--probe")) return SelfTest.Probe();
            if (args.Contains("--migratecheck"))
            {
                // read-only: loads the saved settings, migrates them in memory and checks every layer still has a definition
                var raw = Core.Json.Load<Core.Settings>(Core.Paths.Settings);
                var sb = new System.Text.StringBuilder();
                if (raw == null) sb.AppendLine("settings: none (defaults would be used)");
                else
                {
                    int before = raw.Presets.Sum(p => p.Layers.Count) + raw.Profiles.Sum(p => p.Tracks.Count);
                    App.Migrate(raw);
                    int after = raw.Presets.Sum(p => p.Layers.Count) + raw.Profiles.Sum(p => p.Tracks.Count);
                    int bad = 0;
                    foreach (var l in raw.Presets.SelectMany(p => p.Layers).Concat(raw.Profiles.SelectMany(p => p.Tracks.Select(t => t.Layer))))
                        try { Core.LayerDefs.Get(l.Kind); } catch { bad++; }
                    sb.AppendLine($"version={raw.Version} presets={raw.Presets.Count} layers {before} -> {after} (removed {before - after}) invalid={bad}");
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(Core.Paths.Dir, "migratecheck.txt"), sb.ToString());
                return sb.ToString().Contains("invalid=0") || raw == null ? 0 : 1;
            }
            if (args.Contains("--exit")) { Ipc.SignalExit(); return 0; }
            if (args.Contains("--restore"))
            {
                // Only when no instance is running (a running instance owns the hardware state).
                using var m = new Mutex(false, "ScreenMaster.SingleInstance");
                if (!m.WaitOne(0)) { Ipc.SignalShow(); return 1; }
                Engines.Engine.RepairOnly();
                m.ReleaseMutex();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--write-icon") { UI.AppIcon.WriteIco(args[1]); return 0; }

            using var mutex = new Mutex(true, "ScreenMaster.SingleInstance", out bool first);
            if (!first)
            {
                // Ask the running instance to show itself.
                Ipc.SignalShow();
                return 0;
            }

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
