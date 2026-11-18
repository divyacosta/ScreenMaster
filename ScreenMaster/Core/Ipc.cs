using System;
using System.Threading;

namespace ScreenMaster.Core
{
    /// <summary>Lets a second launch ask the running instance to show its window.</summary>
    public static class Ipc
    {
        const string EventName = "ScreenMaster.ShowWindow";
        const string ExitName = "ScreenMaster.Exit";

        public static void SignalShow() => Signal(EventName);
        /// <summary>Asks the running instance to exit cleanly (restoring the screen).</summary>
        public static void SignalExit() => Signal(ExitName);

        static void Signal(string name)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(name, out var ev)) { ev.Set(); ev.Dispose(); }
            }
            catch { }
        }

        public static void Listen(Action onShow, Action onExit)
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(new WaitHandle[] { show, exit });
                    if (i == 0) onShow(); else onExit();
                }
            }) { IsBackground = true, Name = "ScreenMaster ipc" };
            t.Start();
        }
    }
}
