using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using ScreenMaster.Core;

namespace ScreenMaster.Engines
{
    /// <summary>
    /// Windows settings driven by layers (category "Sistema"). Today: light/dark theme.
    /// Same rule as the monitor hardware: when a layer takes control we save the user's value
    /// (crash-safe, in DisplayState), and when it lets go we write it back.
    /// </summary>
    public sealed class SystemEngine
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        readonly DisplayState _state;
        (int apps, int system)? _written;

        public SystemEngine(DisplayState state) { _state = state; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

        static (int apps, int system) Read()
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            int apps = k?.GetValue("AppsUseLightTheme") is int a ? a : 1;
            int sys = k?.GetValue("SystemUsesLightTheme") is int s ? s : 1;
            return (apps, sys);
        }

        static void Write(int apps, int sys)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                k.SetValue("AppsUseLightTheme", apps, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", sys, RegistryValueKind.DWord);
                // tell running apps/explorer; off the UI thread so a hung window can't block us
                Task.Run(() => SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 200, out _));
            }
            catch (Exception ex) { Log.Write("theme write: " + ex.Message); }
        }

        /// <summary>Last enabled theme layer wins; opacity below 50% counts as "not applied" (a choice can't be blended).</summary>
        public void Apply(IEnumerable<Layer> layers)
        {
            var l = layers.LastOrDefault(x => x.Enabled && x.Kind == LayerKind.WindowsTheme && x.Opacity >= 50);
            if (l == null) { Release(); return; }

            var cur = Read();
            if (_state.OriginalTheme == null)
            {
                _state.OriginalTheme = new[] { cur.apps, cur.system };
                _state.Save();
            }
            int light = (int)l.Get("theme") == 1 ? 1 : 0;
            int apps = l.Get("apps") > 0.5 ? light : _state.OriginalTheme[0];
            int sys = l.Get("system") > 0.5 ? light : _state.OriginalTheme[1];
            if (_written == (apps, sys) && cur == (apps, sys)) return;
            Write(apps, sys);
            _written = (apps, sys);
        }

        public void Release()
        {
            if (_state.OriginalTheme is not { Length: 2 } o) return;
            if (Read() != (o[0], o[1])) Write(o[0], o[1]);
            _state.OriginalTheme = null;
            _written = null;
            _state.Save();
        }

        /// <summary>Crash repair: a theme saved by a previous run is written back.</summary>
        public static void Repair(DisplayState state) => new SystemEngine(state).Release();
    }
}
