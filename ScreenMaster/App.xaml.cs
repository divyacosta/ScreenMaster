using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenMaster.Core;
using ScreenMaster.Engines;
using ScreenMaster.UI;

namespace ScreenMaster
{
    public partial class App : Application
    {
        public static App Ctl => (App)Current;

        public Engine Engine { get; private set; } = null!;
        public Settings Settings { get; private set; } = null!;

        /// <summary>The local preset you picked (base of the stack, edited in the Layers column).</summary>
        public Preset Manual { get; private set; } = null!;
        /// <summary>Preset switched in by a program rule while that program is in front.</summary>
        public Preset? AppPreset { get; private set; }
        public string? AppName { get; private set; }
        /// <summary>Program whose rule pauses the agenda while it's in front.</summary>
        public string? PausedByApp { get; private set; }
        /// <summary>Agenda profile a program rule switched to while that program is in front.</summary>
        public Guid? AppProfile { get; private set; }
        /// <summary>Scrubbed time while dragging the agenda playhead.</summary>
        public DateTime? Simulated { get; private set; }
        /// <summary>Before/after: everything except monitor hardware switched off momentarily.</summary>
        public bool Bypass { get; private set; }
        /// <summary>Day profile shown in the agenda editor.</summary>
        public DayProfile EditingProfile { get; set; } = null!;
        public StackState Stack { get; private set; } = new();
        public List<string> Warnings { get; private set; } = new();
        public UndoStack Undo { get; } = new();

        /// <summary>Preset list / selection / switches changed (rebuild UI).</summary>
        public event Action? StateChanged;
        /// <summary>Engines applied; stack text and layer errors may have changed.</summary>
        public event Action? Applied;
        /// <summary>Clock tick (agenda playhead).</summary>
        public event Action? Tick;

        // Compat for older call sites
        public Preset Editing => Manual;

        MainWindow? _win;
        TrayIcon? _tray;
        HotkeyWindow? _hotkeys;
        ForegroundWatcher? _fg;
        DispatcherTimer? _applyTimer, _saveTimer, _verifyTimer, _warnTimer, _clockTimer;
        bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Write($"--- start v{typeof(App).Assembly.GetName().Version}");
            UI.Access.Init(this);   // Windows text size, high contrast, animations on/off
            UI.Motion.HookMenus();  // menus grow from where they hang

            // Crash safety: restore the screen whatever happens.
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            {
                Log.Write("FATAL: " + a.ExceptionObject);
                try { Engine?.Shutdown(); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Engine?.Shutdown(); } catch { } };
            DispatcherUnhandledException += (_, a) =>
            {
                Log.Write("UI error: " + a.Exception);
                a.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, a) => { Log.Write("task error: " + a.Exception); a.SetObserved(); };
            SessionEnding += (_, _) => { try { Engine?.Shutdown(); } catch { } };

            Engine = new Engine();
            Engine.Start();
            Engine.Hardware.Ready += () => Dispatcher.BeginInvoke(() => { ApplyNow(); StateChanged?.Invoke(); });
            // Keep the Run key pointing at this exe — but never let a dev build (bin\...) take it over.
            if (StartsWithWindows() && Environment.ProcessPath?.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) != true)
                SetStartWithWindows(true);

            Settings = Json.Load<Settings>(Paths.Settings) ?? DefaultSettings();
            Migrate(Settings);
            Manual = Settings.Presets.FirstOrDefault(p => p.Name == Settings.ActivePreset) ?? Settings.Presets[0];
            EditingProfile = Automation.ProfileFor(Settings, DateTime.Now) ?? Settings.Profiles[0];
            Undo.Capture(Settings);

            _applyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Input, (_, _) => { _applyTimer!.Stop(); ApplyNow(); }, Dispatcher);
            _applyTimer.Stop();
            _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); SaveNow(); }, Dispatcher);
            _saveTimer.Stop();
            _verifyTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
            {
                if (Settings.MasterEnabled) Engine.Gamma.Verify();
            }, Dispatcher);
            _warnTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => RefreshWarnings(), Dispatcher);
            // Agenda clock: re-evaluate the automation; only re-apply when a value actually changed.
            _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => OnClock(), Dispatcher);

            SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                Log.Write("display settings changed");
                Engine.RefreshMonitors();
                Engine.Hardware.Reopen();
                RefreshWarnings();
                StateChanged?.Invoke();
                ApplyNow();
            });
            SystemEvents.PowerModeChanged += (_, a) =>
            {
                if (a.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => { Engine.RefreshMonitors(); Engine.Hardware.Reopen(); ApplyNow(); });
            };
            SystemEvents.SessionSwitch += (_, a) =>
            {
                if (a.Reason == SessionSwitchReason.SessionUnlock || a.Reason == SessionSwitchReason.ConsoleConnect)
                    Dispatcher.BeginInvoke(ApplyNow);
            };
            SystemEvents.TimeChanged += (_, _) => Dispatcher.BeginInvoke(OnClock);

            _hotkeys = new HotkeyWindow();
            _hotkeys.Pressed += OnHotkey;
            _tray = new TrayIcon(this);
            _fg = new ForegroundWatcher(OnForegroundChanged);
            Ipc.Listen(() => Dispatcher.BeginInvoke(ShowMain), () => Dispatcher.BeginInvoke(ExitApp));

            RefreshWarnings();
            ApplyNow();
            _verifyTimer.Start();
            _warnTimer.Start();
            _clockTimer.Start();

            bool minimized = e.Args.Contains("--minimized") || Settings.StartMinimized;
            if (!minimized) ShowMain();
            else _tray.Notify("ScreenMaster está rodando na bandeja.");

            if (Engine.RecoveredFromCrash)
                _tray.Notify("A última sessão fechou de forma inesperada. A tela foi restaurada.");
        }

        /// <summary>Brings older settings files up to the current shape.</summary>
        public static void Migrate(Settings s)
        {
            if (s.Presets.Count == 0) s.Presets.AddRange(DefaultSettings().Presets);
            foreach (var p in s.Presets) foreach (var l in p.Layers) l.Migrate();

            if (s.Version < 3)
            {
                // v2 → v3: program lists become rules; timeline blocks become automated tracks.
                foreach (var p in s.Presets)
                {
                    foreach (var app in p.Apps)
                        if (!s.AppRules.Any(r => r.Process.Equals(app, StringComparison.OrdinalIgnoreCase)))
                            s.AppRules.Add(new AppRule { Process = app, Preset = p.Name });
                    p.Apps.Clear();
                }
                if (s.Profiles.Count == 0)
                {
                    var prof = new DayProfile { Name = "Todos os dias" };
                    foreach (var b in s.Schedule)
                    {
                        var preset = s.Presets.FirstOrDefault(p => p.Name == b.Preset);
                        if (preset == null) continue;
                        foreach (var tr in Automation.FromBlock(b, preset))
                        {
                            tr.Layer.Enabled = tr.Layer.Enabled && b.Enabled;
                            prof.Tracks.Add(tr);
                        }
                    }
                    if (prof.Tracks.Count == 0) prof.Tracks.AddRange(ExampleNight());
                    s.Profiles.Add(prof);
                }
                s.Schedule.Clear();
                s.Version = 3;
            }
            if (s.Profiles.Count == 0) s.Profiles.Add(new DayProfile());
            if (s.Week == null || s.Week.Length != 7) s.Week = new Guid[7];
            if (s.Version < 5)
            {
                // v4 → v5: weekdays move from the profile (Week row) into each lane; profiles become use presets.
                for (int i = 0; i < 7; i++)
                    if (!s.Profiles.Any(p => p.Id == s.Week[i])) s.Week[i] = s.Profiles[0].Id;
                foreach (var prof in s.Profiles)
                {
                    int mask = 0;
                    for (int i = 0; i < 7; i++) if (s.Week[i] == prof.Id) mask |= 1 << i;
                    if (mask == 0) mask = 0x7F;
                    foreach (var lane in prof.Tracks.SelectMany(t => t.Lanes).Concat(prof.Linked.SelectMany(l => l.Lanes))) lane.Days = mask;
                }
                s.ActiveProfile = s.Week[ScheduleBlock.DayIndex(DateTime.Now.DayOfWeek)];
                s.Version = 5;
            }
            if (s.Version < 6)
            {
                // v5 → v6: weekdays belong to profiles again (one profile per day), shown as toggles on each profile.
                // If no day has a profile yet: the hand-chosen one takes every day, and a profile named like
                // "fim de semana" takes Saturday and Sunday.
                if (!s.Week.Any(id => s.Profiles.Any(p => p.Id == id)))
                {
                    var main = s.Profiles.FirstOrDefault(p => p.Id == s.ActiveProfile) ?? s.Profiles[0];
                    for (int i = 0; i < 7; i++) s.Week[i] = main.Id;
                    var weekend = s.Profiles.FirstOrDefault(p => p != main && p.Name.Contains("fim de semana", StringComparison.OrdinalIgnoreCase));
                    if (weekend != null) { s.Week[5] = weekend.Id; s.Week[6] = weekend.Id; }
                }
                // lanes limited to exactly their profile's days (from the v5 migration) go back to "all of the profile's days"
                foreach (var p in s.Profiles)
                {
                    int mask = Automation.DaysOf(s, p);
                    foreach (var lane in p.Tracks.SelectMany(t => t.Lanes).Concat(p.Linked.SelectMany(l => l.Lanes)))
                        if (lane.Days == mask) lane.Days = 0x7F;
                }
                // amber is reserved for "live / now"
                foreach (var p in s.Profiles.Where(p => p.Color.Equals("#E8B44C", StringComparison.OrdinalIgnoreCase)))
                    p.Color = Automation.ProfileColors.FirstOrDefault(c => s.Profiles.All(o => !o.Color.Equals(c, StringComparison.OrdinalIgnoreCase))) ?? Automation.ProfileColors[^1];
                s.Version = 6;
            }
            if (s.Version < 7)
            {
                // v6 → v7: the theme color is a profile color and the default; the first profile takes it if nobody has it
                if (s.Profiles.Count > 0 && s.Profiles.All(p => !p.Color.Equals(Automation.ThemeColor, StringComparison.OrdinalIgnoreCase)))
                    s.Profiles[0].Color = Automation.ThemeColor;
                s.Version = 7;
            }
            if (s.Version < 8)
            {
                // v7 → v8: software-only presets (no DDC/CI, no vendor driver) — added only if the name is free
                foreach (var p in CompatPresets())
                    if (!s.Presets.Any(o => o.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) s.Presets.Add(p);
                s.Version = 8;
            }
            for (int i = 0; i < 7; i++) if (s.Week[i] != Guid.Empty && !s.Profiles.Any(p => p.Id == s.Week[i])) s.Week[i] = Guid.Empty;
        }

        /// <summary>Example agenda (switched off): warm filter + lower monitor brightness at night.</summary>
        static IEnumerable<AgendaTrack> ExampleNight()
        {
            var pf = Layer.Create(LayerKind.PhotoFilter);
            pf.Enabled = false;
            var op = new Lane { Param = "opacity" };
            foreach (var (t, v) in new[] { (7 * 60.0, 0.0), (19 * 60.0, 0.0), (22 * 60.0, 100.0), (6 * 60.0, 100.0) }) op.Points.Add(new Keyframe { T = t, V = v });
            yield return new AgendaTrack { Layer = pf, Lanes = { op } };

            var hw = Layer.Create(LayerKind.MonitorHardware);
            hw.Enabled = false;
            hw.P["brightness_on"] = 1;
            var br = new Lane { Param = "brightness" };
            foreach (var (t, v) in new[] { (7 * 60.0, 100.0), (19 * 60.0, 100.0), (22 * 60.0, 40.0), (6 * 60.0, 40.0) }) br.Points.Add(new Keyframe { T = t, V = v });
            yield return new AgendaTrack { Layer = hw, Lanes = { br } };
        }

        /// <summary>Built-in presets that work on any PC: only matrix, gamma, overlay and Windows layers
        /// (no monitor DDC/CI, no NVIDIA vibrance).</summary>
        static IEnumerable<Preset> CompatPresets()
        {
            var fps = new Preset { Name = "FPS competitivo" };
            var fc = Layer.Create(LayerKind.Curves);
            fc.Curves["0"] = new() { new double[] { 0, 0 }, new double[] { 40, 64 }, new double[] { 128, 150 }, new double[] { 255, 255 } };
            fps.Layers.Add(fc);
            var fs = Layer.Create(LayerKind.HueSaturation); fs.P["saturation"] = 25; fps.Layers.Add(fs);
            yield return fps;

            var sleep = new Preset { Name = "Noite / sono" };
            var t = Layer.Create(LayerKind.Temperature); t.P["kelvin"] = 2700; sleep.Layers.Add(t);
            var ex = Layer.Create(LayerKind.Exposure); ex.P["exposure"] = -0.7; sleep.Layers.Add(ex);
            var th = Layer.Create(LayerKind.WindowsTheme); th.P["theme"] = 0; sleep.Layers.Add(th);
            yield return sleep;

            yield return new Preset { Name = "Foto / referência" };

            var design = new Preset { Name = "Design / UI" };
            design.Layers.Add(Layer.Create(LayerKind.Guides));
            yield return design;

            var cb = new Preset { Name = "Simular daltonismo" };
            var c = Layer.Create(LayerKind.ColorBlind); c.P["type"] = 1; cb.Layers.Add(c);
            yield return cb;

            var focus = new Preset { Name = "Foco / P&B" };
            focus.Layers.Add(Layer.Create(LayerKind.BlackWhite));
            yield return focus;

            var call = new Preset { Name = "Apresentação / call" };
            var bc = Layer.Create(LayerKind.BrightnessContrast); bc.P["brightness"] = 10; bc.P["contrast"] = 15; call.Layers.Add(bc);
            yield return call;
        }

        static Settings DefaultSettings()
        {
            var neutral = new Preset { Name = "Neutro" };

            var game = new Preset { Name = "Jogo – cores vivas" };
            var gsat = Layer.Create(LayerKind.HueSaturation); gsat.P["saturation"] = 30; game.Layers.Add(gsat);
            var curve = Layer.Create(LayerKind.Curves);
            curve.Curves["0"] = new() { new double[] { 0, 0 }, new double[] { 64, 56 }, new double[] { 192, 200 }, new double[] { 255, 255 } };
            game.Layers.Add(curve);

            var night = new Preset { Name = "Leitura noturna" };
            var pf = Layer.Create(LayerKind.PhotoFilter); pf.P["density"] = 35; night.Layers.Add(pf);

            var film = new Preset { Name = "Filme" };
            var hs = Layer.Create(LayerKind.HueSaturation); hs.P["saturation"] = -15; film.Layers.Add(hs);
            var fc = Layer.Create(LayerKind.Curves);
            fc.Curves["0"] = new() { new double[] { 0, 14 }, new double[] { 70, 62 }, new double[] { 190, 196 }, new double[] { 255, 245 } };
            film.Layers.Add(fc);
            var vg = Layer.Create(LayerKind.Vignette); vg.P["amount"] = 35; film.Layers.Add(vg);
            var gr = Layer.Create(LayerKind.Grain); gr.P["amount"] = 12; film.Layers.Add(gr);

            var prof = new DayProfile { Name = "Todos os dias", Color = Automation.ThemeColor };
            prof.Tracks.AddRange(ExampleNight());
            var presets = new List<Preset> { neutral, game, night, film };
            presets.AddRange(CompatPresets());
            return new Settings { Presets = presets, ActivePreset = neutral.Name, Version = 8, Profiles = { prof }, Week = Enumerable.Repeat(prof.Id, 7).ToArray() };
        }

        // ------------------------------------------------------------------ apply / save

        public void ScheduleApply()
        {
            _applyTimer?.Stop();
            _applyTimer?.Start();
            ScheduleSave();
        }

        StackState Resolve() => Core.Stack.Resolve(Settings, AppPreset ?? Manual, AppPreset != null ? AppName : null, PausedByApp,
            Simulated ?? DateTime.Now, Simulated != null, Bypass, AppProfile, hardwareWhen: _pinnedAt);

        public void ApplyNow()
        {
            if (_exiting) return;
            try
            {
                Stack = Resolve();
                Engine.Apply(Stack.Effective, Settings.MasterEnabled, Settings.HideOverlayFromCapture);
            }
            catch (Exception ex) { Log.Write("apply: " + ex); }
            Applied?.Invoke();
            _tray?.UpdateText();
        }

        void OnClock()
        {
            if (Settings.SchedulePausedUntil is DateTime p && p <= DateTime.Now)
            {
                Settings.SchedulePausedUntil = null;
                ScheduleSave();
                StateChanged?.Invoke();
            }
            if (Simulated == null && Resolve().Signature != Stack.Signature) ApplyNow();
            Tick?.Invoke();
        }

        public void ScheduleSave()
        {
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        public void SaveNow()
        {
            _saveTimer?.Stop();
            Settings.ActivePreset = Manual.Name;
            Undo.Capture(Settings);
            Json.Save(Paths.Settings, Settings);
        }

        public void RefreshWarnings()
        {
            Warnings = Native.Diagnostics.Warnings(Engine.Monitors);
            if (!Engine.Matrix.Ready) Warnings.Add("Ajustes de Cor indisponíveis: " + Engine.Matrix.Status);
            if (!Engine.Vibrance.Ready) Warnings.Add("Vibração NVIDIA indisponível: " + Engine.Vibrance.Status);
            if (_hotkeys != null && _hotkeys.Failed.Count > 0)
                Warnings.Add("Atalhos já usados por outro programa: " + string.Join(", ", _hotkeys.Failed));
            _win?.ShowWarnings(Warnings);
        }

        // ------------------------------------------------------------------ undo

        public void DoUndo() => UndoRedo(true);
        public void DoRedo() => UndoRedo(false);

        void UndoRedo(bool undo)
        {
            SaveNow(); // capture the gesture in progress first
            bool ok = undo ? Undo.Undo(Settings) : Undo.Redo(Settings);
            if (!ok) return;
            Manual = Settings.Presets.FirstOrDefault(p => p.Name == Manual.Name) ?? Settings.Presets[0];
            EditingProfile = Settings.Profiles.FirstOrDefault(p => p.Id == EditingProfile.Id) ?? Settings.Profiles[0];
            if (AppPreset != null) AppPreset = Settings.Presets.FirstOrDefault(p => p.Name == AppPreset.Name);
            Json.Save(Paths.Settings, Settings);
            StateChanged?.Invoke();
            ApplyNow();
        }

        // ------------------------------------------------------------------ state

        public void SetManual(Preset p)
        {
            Manual = p;
            StateChanged?.Invoke();
            ApplyNow();
            ScheduleSave();
        }

        /// <summary>Use a profile instead of the weekday's one until midnight; null = back to automatic (by weekday).</summary>
        public void ForceProfile(DayProfile? p)
        {
            Settings.ForcedProfile = p?.Id;
            Settings.ForcedUntil = p != null ? DateTime.Today.AddDays(1) : null;
            AppProfile = null; // a hand choice wins over the program rule until the next program switch
            if (p != null) EditingProfile = p;
            StateChanged?.Invoke();
            ApplyNow();
            ScheduleSave();
        }

        /// <summary>Give a weekday (0 = Monday) to a profile, taking it from whichever profile had it; null frees the day.</summary>
        public void SetDayOwner(int day, DayProfile? p)
        {
            Settings.Week[day] = p?.Id ?? Guid.Empty;
            ScheduleApply();
            StateChanged?.Invoke();
        }

        public void SetMaster(bool on)
        {
            if (!on && Scaling) StopScale();
            if (Settings.MasterEnabled == on) return;
            Settings.MasterEnabled = on;
            StateChanged?.Invoke();
            ApplyNow();
            ScheduleSave();
        }

        /// <summary>Camadas switch: the local preset's layers on/off (the agenda keeps running).</summary>
        public void SetLayers(bool on)
        {
            Settings.LayersEnabled = on;
            StateChanged?.Invoke();
            ApplyNow();
            ScheduleSave();
        }

        public void SetSchedule(bool on)
        {
            Settings.ScheduleEnabled = on;
            if (on) Settings.SchedulePausedUntil = null;
            StateChanged?.Invoke();
            ApplyNow();
            ScheduleSave();
        }

        public void PauseSchedule(DateTime? until)
        {
            Settings.SchedulePausedUntil = until;
            StateChanged?.Invoke();
            ApplyNow();
            ScheduleSave();
            if (until != null) _tray?.Notify($"Agenda pausada até {until:dd/MM HH:mm}.");
        }

        /// <summary>Looks the city up by internet address and keeps it. Returns an error message, or null when it worked.</summary>
        public async Task<string?> LocateByInternet()
        {
            try
            {
                var loc = await Core.GeoIp.Lookup();
                Settings.Location = loc;
                Log.Write($"location by internet: {loc.Name} ({loc.Lat}, {loc.Lon})");
                ScheduleApply();
                StateChanged?.Invoke();
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>True while the ruler is being dragged; false once the simulated time is pinned (released).</summary>
        public bool Scrubbing { get; private set; }
        DateTime? _pinnedAt;

        /// <summary>
        /// Simulate a time of day: while scrubbing only the fast engines follow; once pinned (scrubbing = false)
        /// the monitor's own hardware follows too. null returns to live (the real time).
        /// </summary>
        public void Simulate(DateTime? when, bool scrubbing = false)
        {
            Simulated = when;
            Scrubbing = when != null && scrubbing;
            // the monitor's own hardware is slow: it follows only pinned times, and keeps the last one while you drag
            if (when == null) _pinnedAt = null;
            else if (!scrubbing) _pinnedAt = when;
            ApplyNow();
            SimulationChanged?.Invoke();
        }

        /// <summary>Raised when the simulated time is pinned, moved after a drag, or cleared (not on every scrub step).</summary>
        public event Action? SimulationChanged;

        public void SetBypass(bool on)
        {
            if (Bypass == on) return;
            Bypass = on;
            ApplyNow();
        }

        public void PanicReset()
        {
            _scaleHwnd = IntPtr.Zero; _scaleTimer?.Stop();
            Settings.MasterEnabled = false;
            Simulated = null; _pinnedAt = null; Scrubbing = false;
            Bypass = false;
            Engine.RestoreAll();
            StateChanged?.Invoke();
            Applied?.Invoke();
            ScheduleSave();
            _tray?.Notify("Tela restaurada. Ajustes desligados.");
        }

        /// <summary>
        /// Back to a fresh install: example presets + example agenda (off), no program rules, default preferences.
        /// The screen is restored first. "Start with Windows" (a system setting) is left alone.
        /// Presets/agenda/rules stay undoable with Ctrl+Z (the undo stack keeps the previous snapshot).
        /// </summary>
        public void FactoryReset()
        {
            Log.Write("factory reset");
            SaveNow(); // capture the current state so Ctrl+Z can bring it back
            Simulated = null; _pinnedAt = null; Scrubbing = false;
            Bypass = false;
            AppPreset = null; AppName = null; PausedByApp = null; AppProfile = null;
            Engine.RestoreAll();

            var fresh = DefaultSettings();
            Migrate(fresh);
            Settings.Presets = fresh.Presets;
            Settings.Profiles = fresh.Profiles;
            Settings.Week = fresh.Week;
            Settings.ForcedProfile = null;
            Settings.ForcedUntil = null;
            Settings.AppRules = fresh.AppRules;
            Settings.Location = null;
            Settings.Schedule = new();
            Settings.ActivePreset = fresh.ActivePreset;
            Settings.MasterEnabled = true;
            Settings.ScheduleEnabled = true;
            Settings.SchedulePausedUntil = null;
            Settings.AutoSwitch = true;
            Settings.HideOverlayFromCapture = true;
            Settings.StartMinimized = false;
            Settings.CloseToTray = true;
            Settings.HelpVisible = true;
            Settings.TimelineExpanded = true;
            Settings.Version = fresh.Version;

            Manual = Settings.Presets[0];
            EditingProfile = Settings.Profiles[0];
            SaveNow();
            StateChanged?.Invoke();
            ApplyNow();
            _tray?.Notify("Configurações de fábrica restauradas. Ctrl+Z traz de volta seus presets e a agenda.");
        }

        public void NextPreset()
        {
            var list = Settings.Presets;
            int i = list.IndexOf(Manual);
            SetManual(list[(i + 1) % list.Count]);
            if (!Settings.MasterEnabled) SetMaster(true);
            _tray?.Notify("Preset local: " + Manual.Name);
        }

        public void Notify(string text) => _tray?.Notify(text);

        public void NotifyPresetsChanged()
        {
            StateChanged?.Invoke();
            ScheduleApply();
        }

        public void RenamePreset(Preset p, string name)
        {
            foreach (var r in Settings.AppRules.Where(r => r.Preset == p.Name)) r.Preset = name;
            p.Name = name;
            NotifyPresetsChanged();
        }

        public void DeletePreset(Preset p)
        {
            int i = Settings.Presets.IndexOf(p);
            Settings.Presets.Remove(p);
            foreach (var r in Settings.AppRules.Where(r => r.Preset == p.Name)) r.Preset = null;
            var fallback = Settings.Presets[Math.Min(i, Settings.Presets.Count - 1)];
            if (AppPreset == p) { AppPreset = null; AppName = null; }
            if (Manual == p) Manual = fallback;
            NotifyPresetsChanged();
        }

        void OnHotkey(HotkeyAction a)
        {
            switch (a)
            {
                case HotkeyAction.Reset: PanicReset(); break;
                case HotkeyAction.Toggle: SetMaster(!Settings.MasterEnabled); break;
                case HotkeyAction.Next: NextPreset(); break;
                case HotkeyAction.Scale: ToggleScale(); break;
                case HotkeyAction.Bypass:
                    SetBypass(!Bypass);
                    _tray?.Notify(Bypass ? "Antes/depois: ajustes desligados (Ctrl+Alt+Shift+B volta)." : "Ajustes de volta.");
                    break;
            }
        }

        // ------------------------------------------------------------------ scale mode

        /// <summary>The window being scaled (null = off). Scaling pauses while it is not in front.</summary>
        IntPtr _scaleHwnd;
        System.Windows.Threading.DispatcherTimer? _scaleTimer;
        public bool Scaling => _scaleHwnd != IntPtr.Zero;

        public void ToggleScale()
        {
            if (Scaling) { StopScale("Modo escala desligado."); return; }
            if (!Settings.MasterEnabled) { _tray?.Notify("Ligue os ajustes (Ctrl+Alt+Shift+S) antes de usar o modo escala."); return; }
            var h = Native.Win32.GetForegroundWindow();
            Native.Win32.GetWindowThreadProcessId(h, out var pid);
            if (h == IntPtr.Zero || pid == Environment.ProcessId) { _tray?.Notify("Deixe o jogo/programa em primeiro plano e aperte Ctrl+Alt+Shift+E."); return; }
            StartScale(h);
        }

        void StartScale(IntPtr h)
        {
            _scaleHwnd = h;
            _scaleTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => ScaleTick(), Dispatcher);
            _scaleTimer.Start();
            ScaleTick();
            _tray?.Notify("Modo escala ligado (Ctrl+Alt+Shift+E desliga). Deixe o jogo em janela.");
        }

        public void StopScale(string? note = null)
        {
            _scaleHwnd = IntPtr.Zero;
            _scaleTimer?.Stop();
            _scaleNote = "";
            Engine.SetScale(null);
            ApplyNow();
            if (note != null) _tray?.Notify(note);
        }

        /// <summary>Follows the scaled window: pauses when it leaves the front (mouse freed), resumes when it returns.</summary>
        void ScaleTick()
        {
            if (!Scaling) return;
            if (!Settings.MasterEnabled) { StopScale(); return; }
            if (!Native.Win32.IsWindow(_scaleHwnd)) { StopScale("A janela escalada foi fechada."); return; }
            bool front = Native.Win32.GetForegroundWindow() == _scaleHwnd;
            if (!front)
            {
                if (Engine.Scale != null) { Engine.SetScale(null); ApplyNow(); }
                return;
            }
            Native.Win32.GetWindowRect(_scaleHwnd, out var wr);
            int cx = wr.Left + wr.Width / 2, cy = wr.Top + wr.Height / 2;
            var mon = Engine.Monitors.FirstOrDefault(m => cx >= m.Bounds.Left && cx < m.Bounds.Left + m.Bounds.Width && cy >= m.Bounds.Top && cy < m.Bounds.Top + m.Bounds.Height);
            if (mon == null) return;
            var spec = new Engines.ScaleSpec(_scaleHwnd, mon.DeviceName, Math.Clamp(Settings.ScaleAspect, 0, Engines.ScaleMath.Aspects.Length - 1), Settings.ScaleInteger,
                Math.Clamp(Settings.ScaleFilter, 0, Engines.ScaleMath.Filters.Length - 1), Math.Clamp(Settings.ScaleSharpen, 0, 100));
            if (Engine.Scale != spec) { Engine.SetScale(spec); ApplyNow(); }
            Engine.SyncScaleCursor();
            // tell once why the scaled image is not showing (the mouse is already free)
            var err = Engine.Shader.ErrorOf(mon.DeviceName);
            if (!Engine.Shader.IsShowing(mon.DeviceName) && err.Length > 0 && err != _scaleNote) { _scaleNote = err; _tray?.Notify(err); }
            else if (Engine.Shader.IsShowing(mon.DeviceName)) _scaleNote = "";
        }
        string _scaleNote = "";

        string? _lastProcess;

        public void ReevaluateRules() { if (_lastProcess != null) OnForegroundChanged(_lastProcess); }

        void OnForegroundChanged(string process)
        {
            if (_exiting) return;
            if (Scaling) Dispatcher.BeginInvoke(ScaleTick); // Alt+Tab frees the mouse right away
            _lastProcess = process;
            var rule = Settings.AutoSwitch
                ? Settings.AppRules.FirstOrDefault(r => r.Process.Equals(process, StringComparison.OrdinalIgnoreCase))
                : null;
            var preset = rule?.Preset != null ? Settings.Presets.FirstOrDefault(p => p.Name == rule.Preset) : null;
            var paused = rule?.PauseAgenda == true ? process : null;
            var prof = rule?.Profile;
            if (preset == AppPreset && paused == PausedByApp && prof == AppProfile && ((preset == null && prof == null) || AppName == process) && rule?.Scale != true) return;
            if (rule != null) Log.Write($"program rule: {process} -> preset={preset?.Name ?? "-"} pause={paused != null}");
            AppPreset = preset;
            AppName = preset != null || prof != null ? process : null;
            PausedByApp = paused;
            AppProfile = prof;
            if (rule?.Scale == true && !Scaling)
            {
                var h = Native.Win32.GetForegroundWindow();
                if (h != IntPtr.Zero) StartScale(h);
            }
            ApplyNow();
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ window / exit

        public void ShowMain()
        {
            if (_win == null)
            {
                _win = new MainWindow();
                _win.Closed += (_, _) => _win = null;
            }
            _win.Show();
            if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
            _win.Activate();
            _win.ShowWarnings(Warnings);
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            SaveNow();
            _fg?.Dispose();
            _hotkeys?.Dispose();
            Engine.Shutdown();
            _tray?.Dispose();
            Shutdown();
        }

        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (k == null) return;
                if (on) k.SetValue("ScreenMaster", $"\"{Environment.ProcessPath}\" --minimized");
                else k.DeleteValue("ScreenMaster", false);
            }
            catch (Exception ex) { Log.Write("startup reg: " + ex.Message); }
        }

        public static bool StartsWithWindows()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return k?.GetValue("ScreenMaster") is string;
            }
            catch { return false; }
        }
    }
}
