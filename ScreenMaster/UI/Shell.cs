using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenMaster.Core;
using ScreenMaster.Native;
using WinForms = System.Windows.Forms;

namespace ScreenMaster.UI
{
    public static class AppIcon
    {
        static Icon? _icon;

        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            using (var bg = new GraphicsPath())
            {
                float r = s * 0.24f;
                bg.AddArc(0, 0, r * 2, r * 2, 180, 90);
                bg.AddArc(s - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
                bg.AddArc(s - r * 2 - 1, s - r * 2 - 1, r * 2, r * 2, 0, 90);
                bg.AddArc(0, s - r * 2 - 1, r * 2, r * 2, 90, 90);
                bg.CloseFigure();
                using var b = new SolidBrush(Color.FromArgb(255, 30, 30, 34));
                g.FillPath(b, bg);
            }
            // hue wheel
            float pad = s * 0.16f, d = s - pad * 2;
            for (int i = 0; i < 360; i += 4)
            {
                using var b = new SolidBrush(FromHue(i));
                g.FillPie(b, pad, pad, d, d, i - 90, 5);
            }
            float inner = d * 0.42f;
            using (var c = new SolidBrush(Color.FromArgb(255, 30, 30, 34)))
                g.FillEllipse(c, (s - inner) / 2, (s - inner) / 2, inner, inner);
            return bmp;
        }

        static Color FromHue(double h)
        {
            double x = 1 - Math.Abs(h / 60 % 2 - 1);
            (double r, double g, double b) = (h / 60) switch
            {
                < 1 => (1d, x, 0d), < 2 => (x, 1d, 0d), < 3 => (0d, 1d, x), < 4 => (0d, x, 1d), < 5 => (x, 0d, 1d), _ => (1d, 0d, x),
            };
            return Color.FromArgb(255, (int)(r * 235 + 20), (int)(g * 235 + 20), (int)(b * 235 + 20));
        }

        public static Icon Icon => _icon ??= Icon.FromHandle(Draw(32).GetHicon());

        /// <summary>Writes a multi-size .ico with PNG-compressed entries (used for the exe icon at build time).</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 256, 64, 48, 32, 24, 16 };
            var pngs = new List<byte[]>();
            foreach (var s in sizes)
            {
                using var bmp = Draw(s);
                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
            using var fs = System.IO.File.Create(path);
            using var w = new System.IO.BinaryWriter(fs);
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }

        public static BitmapSource Image(int size)
        {
            using var bmp = Draw(size);
            var h = bmp.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
            try { return Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            finally { Win32.DeleteObject(h); }
        }
    }

    public sealed class TrayIcon : IDisposable
    {
        readonly WinForms.NotifyIcon _icon;
        readonly App _app;

        public TrayIcon(App app)
        {
            _app = app;
            _icon = new WinForms.NotifyIcon { Icon = AppIcon.Icon, Text = "ScreenMaster", Visible = true };
            _icon.ContextMenuStrip = new WinForms.ContextMenuStrip();
            _icon.ContextMenuStrip.Opening += (_, _) => Build();
            _icon.DoubleClick += (_, _) => _app.ShowMain();
            _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _app.ShowMain(); };
            Build();
        }

        void Build()
        {
            var m = _icon.ContextMenuStrip!;
            m.Items.Clear();
            m.Items.Add("Abrir ScreenMaster", null, (_, _) => _app.ShowMain());
            var master = new WinForms.ToolStripMenuItem("Ajustes ligados") { Checked = _app.Settings.MasterEnabled, ShortcutKeyDisplayString = "Ctrl+Alt+Shift+S" };
            master.Click += (_, _) => _app.SetMaster(!_app.Settings.MasterEnabled);
            m.Items.Add(master);
            var presets = new WinForms.ToolStripMenuItem("Preset local");
            foreach (var p in _app.Settings.Presets)
            {
                var it = new WinForms.ToolStripMenuItem(p.Name) { Checked = p == _app.Manual };
                var captured = p;
                it.Click += (_, _) => { _app.SetManual(captured); if (!_app.Settings.MasterEnabled) _app.SetMaster(true); };
                presets.DropDownItems.Add(it);
            }
            m.Items.Add(presets);
            var layersOn = new WinForms.ToolStripMenuItem("Camadas ligadas") { Checked = _app.Settings.LayersEnabled };
            layersOn.Click += (_, _) => _app.SetLayers(!_app.Settings.LayersEnabled);
            m.Items.Add(layersOn);

            var s = _app.Settings;
            var agenda = new WinForms.ToolStripMenuItem("Agenda");
            var on = new WinForms.ToolStripMenuItem("Agenda ligada") { Checked = s.ScheduleEnabled };
            on.Click += (_, _) => _app.SetSchedule(!s.ScheduleEnabled);
            agenda.DropDownItems.Add(on);
            if (s.SchedulePausedUntil is DateTime until && until > DateTime.Now)
            {
                var resume = new WinForms.ToolStripMenuItem($"Retomar (pausada até {until:HH:mm})");
                resume.Click += (_, _) => _app.PauseSchedule(null);
                agenda.DropDownItems.Add(resume);
            }
            else
            {
                var p1 = new WinForms.ToolStripMenuItem("Pausar por 1 hora");
                p1.Click += (_, _) => _app.PauseSchedule(DateTime.Now.AddHours(1));
                var p2 = new WinForms.ToolStripMenuItem("Pausar até amanhã (06:00)");
                p2.Click += (_, _) => _app.PauseSchedule(DateTime.Today.AddDays(DateTime.Now.Hour < 6 ? 0 : 1).AddHours(6));
                agenda.DropDownItems.Add(p1);
                agenda.DropDownItems.Add(p2);
            }
            agenda.DropDownItems.Add(new WinForms.ToolStripSeparator());
            bool forced = Core.Automation.IsForced(s, DateTime.Now);
            var todays = Core.Automation.DayOwner(s, Core.ScheduleBlock.DayIndex(DateTime.Now.DayOfWeek));
            var autoItem = new WinForms.ToolStripMenuItem($"Perfil pelo dia (hoje: {todays?.Name ?? "sem perfil"})") { Checked = !forced };
            autoItem.Click += (_, _) => _app.ForceProfile(null);
            agenda.DropDownItems.Add(autoItem);
            var use = new WinForms.ToolStripMenuItem("Usar hoje, até meia-noite");
            foreach (var prof in s.Profiles)
            {
                var pp = prof;
                var it = new WinForms.ToolStripMenuItem(prof.Name) { Checked = forced && s.ForcedProfile == prof.Id };
                it.Click += (_, _) => _app.ForceProfile(pp);
                use.DropDownItems.Add(it);
            }
            agenda.DropDownItems.Add(use);
            m.Items.Add(agenda);
            m.Items.Add(new WinForms.ToolStripSeparator());
            var reset = new WinForms.ToolStripMenuItem("Restaurar tela") { ShortcutKeyDisplayString = "Ctrl+Alt+Shift+R" };
            reset.Click += (_, _) => _app.PanicReset();
            m.Items.Add(reset);
            m.Items.Add("Sair", null, (_, _) => _app.ExitApp());
        }

        /// <summary>Tray tooltip shows what's on screen (max 127 chars).</summary>
        public void UpdateText()
        {
            try
            {
                var t = "ScreenMaster · " + (_app.Settings.MasterEnabled ? _app.Stack.Describe() : "desligado");
                _icon.Text = t.Length > 127 ? t.Substring(0, 124) + "…" : t;
            }
            catch { }
        }

        public void Notify(string text)
        {
            try { _icon.ShowBalloonTip(2500, "ScreenMaster", text, WinForms.ToolTipIcon.None); } catch { }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }

    public enum HotkeyAction { Reset = 1, Toggle = 2, Next = 3, Bypass = 4, Scale = 5 }

    /// <summary>Global hotkeys on a message-only window.</summary>
    public sealed class HotkeyWindow : IDisposable
    {
        readonly HwndSource _src;
        public event Action<HotkeyAction>? Pressed;
        public List<string> Failed { get; } = new();

        public HotkeyWindow()
        {
            var p = new HwndSourceParameters("ScreenMasterHotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
            _src = new HwndSource(p);
            _src.AddHook(Hook);
            const uint mods = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT;
            Register(HotkeyAction.Reset, mods, 0x52, "Ctrl+Alt+Shift+R");
            Register(HotkeyAction.Toggle, mods, 0x53, "Ctrl+Alt+Shift+S");
            Register(HotkeyAction.Next, mods, 0x50, "Ctrl+Alt+Shift+P");
            Register(HotkeyAction.Bypass, mods, 0x42, "Ctrl+Alt+Shift+B");
            Register(HotkeyAction.Scale, mods, 0x45, "Ctrl+Alt+Shift+E");
        }

        void Register(HotkeyAction a, uint mods, uint vk, string label)
        {
            if (!Win32.RegisterHotKey(_src.Handle, (int)a, mods, vk))
            {
                Failed.Add(label);
                Log.Write("hotkey failed: " + label);
            }
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == (int)Win32.WM_HOTKEY)
            {
                Pressed?.Invoke((HotkeyAction)w.ToInt32());
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (HotkeyAction a in Enum.GetValues(typeof(HotkeyAction))) Win32.UnregisterHotKey(_src.Handle, (int)a);
            _src.Dispose();
        }
    }

    /// <summary>Reports the process name of the foreground window (excluding our own).</summary>
    public sealed class ForegroundWatcher : IDisposable
    {
        readonly WinEventDelegate _proc;
        readonly IntPtr _hook;
        readonly Action<string> _changed;
        readonly int _ownPid = Environment.ProcessId;

        public ForegroundWatcher(Action<string> changed)
        {
            _changed = changed;
            _proc = OnEvent;
            _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT);
        }

        void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            try
            {
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == _ownPid) return;
                using var p = Process.GetProcessById((int)pid);
                _changed(p.ProcessName);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        }
    }
}
