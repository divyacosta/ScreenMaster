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
