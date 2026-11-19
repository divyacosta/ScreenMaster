using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenMaster.Core;
using ScreenMaster.Native;
using ScreenMaster.UI;
using WinForms = System.Windows.Forms;

namespace ScreenMaster
{
    public partial class MainWindow : Window
    {
        static App Ctl => App.Ctl;

        readonly ObservableCollection<PresetVM> _presets = new();
        readonly ObservableCollection<LayerVM> _layers = new();
        bool _suppress;
        Layer? _propsLayer;
        AgendaTrack? _propsTrack;
        TextBlock? _propsError;
        bool _toldAboutTray;
        HelpInfo? _lastHelp;
        bool _bypassKey;
        AgendaTrack? _agendaTrack;      // selected in the Agenda tab
        bool _agendaTrackLinked;
        LaneControl? _pointCtl;
        Keyframe? _point;
        Border? _toast;
        System.Windows.Threading.DispatcherTimer? _toastTimer;

        AgendaPage Agenda => AgendaPageControl;
        bool InAgendaTab => TabAgenda.IsChecked == true;

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
            Icon = AppIcon.Image(32);
            LogoImage.Source = AppIcon.Image(44);
            PresetList.ItemsSource = _presets;
            LayerList.ItemsSource = _layers;

            // agenda tab
            Agenda.Hover = ShowHelp;
            Agenda.TrackSelected += (tr, linked) =>
            {
                _agendaTrack = tr;
                _agendaTrackLinked = linked;
                _pointCtl = null; _point = null;
                if (InAgendaTab) ShowAgendaProps();
            };
            Agenda.PointSelected += (ctl, k) =>
            {
                _pointCtl = k != null ? ctl : null;
                _point = k;
                if (k != null && (_agendaTrack == null || _agendaTrack.Id != ctl.Track.Id))
                {
                    _agendaTrack = ctl.Track;
                    _agendaTrackLinked = Ctl.EditingProfile.Tracks.All(t => t.Id != ctl.Track.Id);
                }
                if (InAgendaTab) ShowAgendaProps();
            };
            MiniAgendaStrip.Clicked += () => TabAgenda.IsChecked = true;
            Help.Set(MiniAgendaStrip, new HelpInfo("Agenda de hoje", "Onde a automação age hoje: áreas claras = ajustes da agenda agindo; tracinhos = pontos das automações do preset local; linha âmbar = agora. Clique para abrir a aba Agenda.", null));
            Help.Set(TabAgenda, new HelpInfo("Agenda", "Automação ao longo do dia (como no Ableton): perfis com seus dias da semana, ajustes, automações e pontos. Na tela Camadas, passe o mouse num valor e clique em “+ agenda” para mandá-lo para cá.", null));

            // help
            Help.Set(MasterSwitch, "master");
            Help.Set(ResetButton, "reset");
            Help.Set(SettingsButton, "settings");
            Help.Set(HelpToggle, "helpBox");
            Help.Set(TabLayers, "tabLayers");
            Help.Set(PresetList, "presets");
            Help.Set(BtnNewPreset, "presetNew");
            Help.Set(LayersSwitch, new HelpInfo("Ligar/desligar camadas", "Liga ou desliga os ajustes do preset local sem mexer na agenda, do mesmo jeito que a chave da Agenda liga ou desliga só a agenda. Para desligar tudo de uma vez, use “Ajustes” no topo.", null));
            Help.Set(BtnNewPresetBelow, "presetNew");
            Help.Set(BtnDupPreset, "presetDup");
            Help.Set(BtnRenPreset, "presetRename");
            Help.Set(BtnDelPreset, "presetDelete");
            Help.Set(AutoSwitchCheck, "autoSwitch");
            Help.Set(RulesButton, "apps");
            Help.Set(AddLayerButton, "addLayer");
            Help.Set(BtnLayerUp, "layerUp");
            Help.Set(BtnLayerDown, "layerDown");
            Help.Set(BtnLayerDup, "layerDup");
            Help.Set(BtnLayerDel, "layerDelete");
            Help.Set(NowPanel, "timelineNow");
            Help.Set(UndoButton, new HelpInfo("Desfazer", "Volta a última alteração em presets, ajustes, agenda ou regras.", "Atalho: Ctrl+Z"));
            Help.Set(RedoButton, new HelpInfo("Refazer", "Refaz o que foi desfeito.", "Atalho: Ctrl+Y"));
            Help.Set(BypassButton, new HelpInfo("Antes/depois", "Segure para ver a tela sem os ajustes; solte para voltar. O brilho físico do monitor não muda (é lento e grava no monitor).",
                "Atalho: segure \\ nesta janela"));
            PreviewMouseMove += OnHelpMouseMove;
            ShowHelp(null);

            Ctl.StateChanged += RefreshAll;
            Ctl.Applied += OnApplied;
            Ctl.Tick += OnTick;
            Closed += (_, _) => { Ctl.StateChanged -= RefreshAll; Ctl.Applied -= OnApplied; Ctl.Tick -= OnTick; Ctl.SimulationChanged -= OnSimulationChanged; };
            // A pinned time stays while you look at other programs; it ends when the window is minimized or hidden.
            Deactivated += (_, _) =>
            {
                if (_bypassKey) { _bypassKey = false; Ctl.SetBypass(false); }
            };
            StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && Ctl.Simulated != null) Ctl.Simulate(null); };
            IsVisibleChanged += (_, _) => { if (!IsVisible && Ctl.Simulated != null) Ctl.Simulate(null); };
            Ctl.SimulationChanged += OnSimulationChanged;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape && Ctl.Simulated != null && Keyboard.FocusedElement is not TextBox) { Ctl.Simulate(null); e.Handled = true; }
            };
            RefreshAll();
            OnApplied();
            BuildUsage();
        }

        // ------------------------------------------------------------------ the app's own CPU/GPU use (footer)

        Native.Usage? _usage;
        TextBlock? _cpuText, _gpuText;

        void BuildUsage()
        {
            if (EngineStatus.Parent is not DockPanel footer) return;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock Part(Native.Hardware.Brand brand, string label)
            {
                panel.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(1), Background = Ui.Hex(brand.Color), Margin = new Thickness(panel.Children.Count == 0 ? 0 : 12, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                var t = new TextBlock { Text = label + " …", FontSize = 11, Foreground = Ui.Res("Dim"), VerticalAlignment = VerticalAlignment.Center };
                t.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
                panel.Children.Add(t);
                return t;
            }
            _cpuText = Part(Native.Hardware.Cpu, "CPU");
            _gpuText = Part(Native.Hardware.Gpu, "GPU");
            panel.Children.Add(new Border { Width = 1, Height = 10, Background = Ui.Res("Line"), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var cpuName = Native.Hardware.Cpu.Name.Length > 0 ? $"CPU {Native.Hardware.Cpu.Name}" : "CPU";
            var gpuName = Native.Hardware.GpuName.Length > 0 ? Native.Hardware.GpuName : "GPU";
            Help.Set(panel, new HelpInfo("Consumo do ScreenMaster", "Quanto o próprio ScreenMaster usa agora, como no Gerenciador de Tarefas. Os ajustes de Tons, Cor, NVIDIA e Monitor não custam nada; o que pesa é a Granulação animada. Fica âmbar quando passa do normal.",
                $"{cpuName} · {gpuName}"));
            DockPanel.SetDock(panel, Dock.Right);
            footer.Children.Insert(footer.Children.IndexOf(EngineStatus) + 1, panel);

            _usage = new Native.Usage();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                if (!IsVisible || WindowState == WindowState.Minimized) return;
                var (cpu, gpu) = _usage.Sample();
                string Pct(double v) => v < 10 ? v.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) + "%" : Math.Round(v) + "%";
                _cpuText!.Text = "CPU " + Pct(cpu);
                _cpuText.Foreground = Ui.Res(cpu >= 5 ? "Warn" : "Dim");
                _cpuText.FontWeight = cpu >= 5 ? FontWeights.SemiBold : FontWeights.Normal;
                _gpuText!.Text = "GPU " + (gpu is double g ? Pct(g) : "—");
                _gpuText.Foreground = Ui.Res(gpu >= 10 ? "Warn" : "Dim");
                _gpuText.FontWeight = gpu >= 10 ? FontWeights.SemiBold : FontWeights.Normal;
            };
            timer.Start();
            Closed += (_, _) => { timer.Stop(); _usage?.Dispose(); };
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (Ctl.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                if (!_toldAboutTray)
                {
                    _toldAboutTray = true;
                    Ctl.Notify("ScreenMaster continua rodando na bandeja. Para fechar de vez: clique direito no ícone › Sair.");
                }
            }
            else Ctl.Dispatcher.BeginInvoke(Ctl.ExitApp);
        }

        static bool IsBackslash(Key k) => k is Key.Oem5 or Key.OemBackslash or Key.Oem102;

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            bool typing = Keyboard.FocusedElement is TextBox;
            if (e.Key == Key.F1) { ToggleHelp(); e.Handled = true; }
            else if (!typing && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Ctl.DoUndo(); e.Handled = true; }
            else if (!typing && (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)) { Ctl.DoRedo(); e.Handled = true; }
            else if (!typing && IsBackslash(e.Key)) { if (!_bypassKey) { _bypassKey = true; Ctl.SetBypass(true); } e.Handled = true; }
            else if (e.Key == Key.Delete && LayerList.IsKeyboardFocusWithin) { DeleteLayer_Click(this, e); e.Handled = true; }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (IsBackslash(e.Key) && _bypassKey) { _bypassKey = false; Ctl.SetBypass(false); e.Handled = true; }
        }

        public void ShowWarnings(List<string> warnings) => WarningsList.ItemsSource = warnings.ToList();

        // ------------------------------------------------------------------ agenda layout

        // ------------------------------------------------------------------ toast

        void ShowToast(string text, string? action = null, Action? onAction = null)
        {
            if (_toast == null)
            {
                _toast = new Border
                {
                    Background = Ui.Res("Raised"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 8, 8, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 24), Visibility = Visibility.Collapsed,
                };
                Grid.SetRow(_toast, 2);
                RootGrid.Children.Add(_toast);
                _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Motion.Rise(_toast, false); };
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            if (action != null && onAction != null)
            {
                var b = new Button { Content = action, Style = (Style)FindResource("Primary"), Padding = new Thickness(10, 3, 10, 3) };
                b.Click += (_, _) => { Motion.Rise(_toast, false); onAction(); };
                sp.Children.Add(b);
            }
            _toast.Child = sp;
            if (_toast.Visibility != Visibility.Visible) Motion.Rise(_toast, true); // rises from the bottom edge, leaves the same way
            _toastTimer!.Stop();
            _toastTimer.Start();
        }

        // ------------------------------------------------------------------ automation from sliders (⏱)

        /// <summary>Profile that ⏱ writes to: the edited one in the Agenda tab, today's one elsewhere.</summary>
        DayProfile AutomationProfile => InAgendaTab ? Ctl.EditingProfile : Automation.ProfileFor(Ctl.Settings, DateTime.Now, Ctl.AppProfile) ?? Ctl.EditingProfile;

        /// <summary>The lane of a parameter that runs today (a parameter may have one lane per set of weekdays).</summary>
        static Lane? TodayLane(AgendaTrack tr, string key)
        {
            var lanes = tr.Lanes.Where(x => x.Param == key).ToList();
            return lanes.LastOrDefault(x => x.OnDay(DateTime.Now.DayOfWeek)) ?? lanes.FirstOrDefault();
        }

        Lane? LaneOf(Layer l, string key, AgendaTrack? ownTrack)
            => ownTrack != null && !_agendaTrackLinked && InAgendaTab ? TodayLane(ownTrack, key)
                                                                      : AutomationProfile.LaneFor(l.Id, key);

        bool IsAutomated(Layer l, string key, AgendaTrack? ownTrack) => LaneOf(l, key, ownTrack)?.Points.Count > 0;

        void Automate(Layer l, string key, AgendaTrack? ownTrack)
        {
            var now = DateTime.Now;
            double cur = key == "opacity" ? l.Opacity : l.Get(key);
            Lane lane;
            if (ownTrack != null && !_agendaTrackLinked && InAgendaTab)
            {
                lane = TodayLane(ownTrack, key) ?? new Lane { Param = key };
                if (!ownTrack.Lanes.Contains(lane)) ownTrack.Lanes.Add(lane);
            }
            else lane = AutomationProfile.AddLinked(l.Id, key);
            if (lane.Points.Count == 0) lane.Points.Add(new Keyframe { T = Math.Round((now.Hour * 60 + now.Minute) / 15.0) * 15 % 1440, V = cur });
            lane.Overridden = false;
            var prof = AutomationProfile;
            Ctl.EditingProfile = prof;
            Ctl.ScheduleApply();
            Agenda.Rebuild();
            if (InAgendaTab) { Agenda.Reveal(l.Id, key); return; }
            ShowToast($"Automação criada: {Automation.Label(l.Kind, key)} · perfil “{prof.Name}”", "Ver na agenda", () => RevealInAgenda(l, key));
            ShowProps(_propsLayer);
        }

        void RevealInAgenda(Layer l, string key)
        {
            TabAgenda.IsChecked = true;
            Agenda.Reveal(l.Id, key);
        }

        void RemoveAutomation(Layer l, string key, AgendaTrack? ownTrack)
        {
            if (ownTrack != null && !_agendaTrackLinked && InAgendaTab) ownTrack.Lanes.RemoveAll(x => x.Param == key);
            else AutomationProfile.RemoveLinked(l.Id, key);
            Ctl.ScheduleApply();
            Agenda.Rebuild();
            RefreshPropsAfterAutomation();
        }

        void RefreshPropsAfterAutomation()
        {
            if (InAgendaTab) ShowAgendaProps(); else ShowProps(_propsLayer);
        }

        /// <summary>
        /// The automation pill of a parameter row (right-click: menu).
        /// "+ agenda" (dashed, shown on hover) creates the automation; "∿ agenda ›" (amber, with the day's curve)
        /// means it follows the agenda and jumps to its lane; "à mão · ↺ voltar" means you moved it by hand.
        /// </summary>
        Button ClockButton(Layer l, string key, AgendaTrack? ownTrack, FrameworkElement row)
        {
            bool on = IsAutomated(l, key, ownTrack);
            var lane = LaneOf(l, key, ownTrack);
            bool over = on && lane?.Overridden == true;

            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (on && !over && lane != null) content.Children.Add(Sparkline(l, key, lane));
            content.Children.Add(new TextBlock
            {
                Text = !on ? "+ agenda" : over ? "à mão · ↺ voltar" : "agenda ›", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = on && !over ? Ui.Res("Text") : Ui.Res("Dim"),
            });
            var b = new Button
            {
                Content = content, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0, MinWidth = 0, Height = 19, Padding = new Thickness(0),
                Template = PillTemplate(dashed: !on || over),
                Background = Brushes.Transparent,
                BorderBrush = on && !over ? Ui.Res("Outline") : Ui.Res("LineStrong"),
                ToolTip = !on ? "Automatizar este valor na agenda (cria uma automação com o valor de agora)"
                        : over ? "Você mexeu à mão: a automação está pausada. Clique para ela voltar a mandar."
                        : "Este valor segue a agenda. Clique para ver a automação.",
            };
            if (!on)
            {
                // quiet until you point at the row, so a long inspector doesn't fill with buttons
                b.Opacity = 0;
                row.MouseEnter += (_, _) => b.Opacity = 1;
                row.MouseLeave += (_, _) => { if (!b.IsKeyboardFocused) b.Opacity = 0; };
                b.GotKeyboardFocus += (_, _) => b.Opacity = 1;
                b.LostKeyboardFocus += (_, _) => { if (!row.IsMouseOver) b.Opacity = 0; };
            }
            void Back() { lane!.Overridden = false; Ctl.ScheduleApply(); Agenda.RefreshValues(); RefreshPropsAfterAutomation(); }
            void Show() { if (InAgendaTab) Agenda.Reveal(l.Id, key); else RevealInAgenda(l, key); }
            b.Click += (_, _) =>
            {
                if (!on) Automate(l, key, ownTrack);
                else if (over) Back();
                else Show();
            };
            void Menu()
            {
                var m = new ContextMenu { PlacementTarget = b };
                MenuItem It(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); m.Items.Add(mi); return mi; }
                if (!on) It("Automatizar na agenda", () => Automate(l, key, ownTrack));
                else
                {
                    It("Mostrar na agenda", Show);
                    if (over) It("↺ Voltar à automação", Back);
                    It("Remover automação", () => RemoveAutomation(l, key, ownTrack));
                }
                m.IsOpen = true;
            }
            b.MouseRightButtonUp += (_, e) => { Menu(); e.Handled = true; };
            row.MouseRightButtonUp += (_, e) => { if (!e.Handled) { Menu(); e.Handled = true; } };
            return b;
        }

        /// <summary>Rounded pill: a border (dashed when it only offers to create something) around the content.</summary>
        static ControlTemplate PillTemplate(bool dashed)
        {
            var t = new ControlTemplate(typeof(Button));
            var grid = new FrameworkElementFactory(typeof(Grid));
            var rect = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle), "r");
            rect.SetValue(System.Windows.Shapes.Shape.FillProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            rect.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            rect.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 9.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 9.0);
            if (dashed) rect.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection { 3, 2 });
            grid.AppendChild(rect);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 1, 8, 2));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.AppendChild(cp);
            t.VisualTree = grid;
            var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6, "r"));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "r"));
            hover.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Application.Current.FindResource("Text"), "r"));
            t.Triggers.Add(hover);
            t.Triggers.Add(press); // after hover, so pressing wins
            return t;
        }

        /// <summary>The lane's 24 hours as a tiny line (dark, drawn on the amber pill).</summary>
        FrameworkElement Sparkline(Layer l, string key, Lane lane)
        {
            const double w = 30, h = 10;
            var line = new System.Windows.Shapes.Polyline { Stroke = Ui.Res("Text"), StrokeThickness = 1.3, Width = w, Height = h, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var r = Automation.Range(l.Kind, key);
            if (r == null) return line;
            var day = (Ctl.Simulated ?? DateTime.Now).Date;
            var vals = Enumerable.Range(0, 25)
                .Select(i => Automation.Eval(lane, i * 60.0, day, Ctl.Settings.Location, r.Value.stepped) ?? (key == "opacity" ? l.Opacity : l.Get(key))).ToList();
            // scale to the curve's own swing so small changes still read; a flat lane stays a flat line in the middle
            double lo = vals.Min(), hi = vals.Max(), span = hi - lo;
            for (int i = 0; i <= 24; i++)
                line.Points.Add(new Point(i / 24.0 * w, span < 1e-6 ? h / 2 : h - 1 - (vals[i] - lo) / span * (h - 2)));
            return line;
        }

        // ------------------------------------------------------------------ refresh

        void RefreshAll()
        {
            _suppress = true;
            try
            {
                var s = Ctl.Settings;
                _presets.Clear();
                foreach (var p in s.Presets) _presets.Add(new PresetVM(p));
                PresetList.SelectedItem = _presets.FirstOrDefault(v => v.Model == Ctl.Manual);
                MasterSwitch.IsChecked = s.MasterEnabled;
                MasterSwitch.Content = "Ajustes";
                LayersSwitch.IsChecked = s.LayersEnabled;
                LayersSwitch.Content = s.LayersEnabled ? "ligadas" : "desligadas";
                AutoSwitchCheck.IsChecked = s.AutoSwitch;
                RulesButton.Content = s.AppRules.Count == 0 ? "Regras por programa…" : $"Regras por programa ({s.AppRules.Count})";
                HelpBox.Visibility = s.HelpVisible ? Visibility.Visible : Visibility.Collapsed;
                UndoButton.IsEnabled = true;
                EditingName.Text = Ctl.Manual.Name;

                // an agenda track that no longer exists (undo, profile switch) closes its properties
                if (_agendaTrack != null && !_agendaTrackLinked && !Ctl.EditingProfile.Tracks.Contains(_agendaTrack)) { _agendaTrack = null; _point = null; _pointCtl = null; }
                if (_agendaTrack != null && _agendaTrackLinked && Ctl.EditingProfile.LinkedFor(_agendaTrack.Layer.Id) is { } la) _agendaTrack.Lanes = la.Lanes;
                Agenda.Rebuild();
                if (InAgendaTab) { BuildLayers(_propsLayer?.Id, showProps: false); ShowAgendaProps(); }
                else BuildLayers(_propsLayer?.Id);
                UpdateStatus();
            }
            finally { _suppress = false; }
            RefreshNow();
        }

        /// <summary>Pinned or back to live: the inspector shows the values of that time.</summary>
        void OnSimulationChanged()
        {
            if (Ctl.Scrubbing) return;
            Agenda.RefreshValues();
            if (InAgendaTab) ShowAgendaProps();
        }

        void OnApplied()
        {
            RefreshErrors();
            RefreshNow();
            RefreshPresetSummaries();
            Agenda.RefreshValues();
            MiniAgendaStrip.InvalidateVisual();
        }

        void OnTick()
        {
            Agenda.RefreshValues();
            MiniAgendaStrip.InvalidateVisual();
            RefreshNow();
        }

        void RefreshPresetSummaries()
        {
            foreach (var p in _presets) p.RaiseAll();
        }

        /// <summary>The header focal line: what's on the screen right now, as one sentence.</summary>
        void RefreshNow()
        {
            var t = NowChips;
            t.Inlines.Clear();
            var st = Ctl.Stack;
            var now = Ctl.Simulated ?? DateTime.Now;
            NowTime.Text = (Ctl.Simulated != null ? "simulando " : "") + now.ToString("HH:mm");
            if (st.Local == null) return;
            System.Windows.Documents.Run R(string s, string brush, bool strong = false)
                => new(s) { Foreground = Ui.Res(brush), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
            if (!Ctl.Settings.MasterEnabled)
            {
                t.Inlines.Add(R("Ajustes desligados", "Dim"));
                return;
            }
            if (st.Bypass) { t.Inlines.Add(R("Antes: ", "Danger")); t.Inlines.Add(R("ajustes pausados enquanto segura", "Dim")); return; }
            if (st.LayersOff) t.Inlines.Add(R("Camadas desligadas", "Dim"));
            else t.Inlines.Add(R(st.Local.Name, "Text", strong: true));
            if (st.LocalApp != null) t.Inlines.Add(R($" por {st.LocalApp}", "Dim"));
            if (st.AgendaOff) t.Inlines.Add(R("  ·  agenda desligada", "Dim"));
            else if (st.AgendaPausedReason != null) t.Inlines.Add(R($"  ·  agenda pausada ({st.AgendaPausedReason})", "Dim"));
            else if (st.Profile != null)
            {
                int active = st.Agenda.Count(a => a.layer.Opacity > 0.5);
                t.Inlines.Add(R("  +  agenda ", "Dim"));
                // the profile name switches the use profile (a menu of all profiles)
                var link = new System.Windows.Documents.Hyperlink(R(st.Profile.Name, "Text")) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Trocar o perfil da agenda" };
                link.Click += (_, _) => ProfileMenu(t);
                t.Inlines.Add(link);
                if (Ctl.AppProfile == st.Profile.Id && Ctl.AppName != null) t.Inlines.Add(R($" por {Ctl.AppName}", "Dim"));
                else if (Automation.IsForced(Ctl.Settings, now)) t.Inlines.Add(R(" até 00:00", "Dim"));
                if (active > 0) t.Inlines.Add(R($" · {active} agindo", "Dim"));
            }
        }

        void ProfileMenu(FrameworkElement target)
        {
            var s = Ctl.Settings;
            var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            bool forced = Automation.IsForced(s, DateTime.Now);
            var today = Automation.DayOwner(s, ScheduleBlock.DayIndex(DateTime.Now.DayOfWeek));
            var auto = new MenuItem { Header = $"Automático pelo dia (hoje: {today?.Name ?? "sem perfil"})", IsChecked = !forced };
            auto.Click += (_, _) => Ctl.ForceProfile(null);
            m.Items.Add(auto);
            m.Items.Add(new Separator());
            m.Items.Add(new MenuItem { Header = "Usar hoje, até meia-noite:", IsEnabled = false });
            foreach (var p in s.Profiles)
            {
                var pp = p;
                var mi = new MenuItem { Header = p.Name, IsChecked = forced && s.ForcedProfile == p.Id };
                mi.Click += (_, _) => Ctl.ForceProfile(pp);
                m.Items.Add(mi);
            }
            if (Ctl.AppProfile != null)
                m.Items.Add(new MenuItem { Header = $"Agora escolhido por regra ({Ctl.AppName})", IsEnabled = false });
            m.IsOpen = true;
        }

        void BuildLayers(Guid? select, bool showProps = true)
        {
            bool old = _suppress;
            _suppress = true;
            try
            {
                _layers.Clear();
                var layers = Ctl.Manual.Layers;
                for (int i = layers.Count - 1; i >= 0; i--)
                    _layers.Add(new LayerVM(layers[i], OnLayerToggled));
                var sel = _layers.FirstOrDefault(v => v.Model.Id == select) ?? _layers.FirstOrDefault();
                LayerList.SelectedItem = sel;
                if (showProps && !InAgendaTab)
                {
                    ShowProps(sel?.Model);
                }
            }
            finally { _suppress = old; }
            RefreshErrors();
        }

        void OnLayerToggled()
        {
            RefreshPresetSummaries();
            Ctl.ScheduleApply();
        }

        void RefreshErrors()
        {
            foreach (var vm in _layers)
                vm.Error = Ctl.Engine.LayerErrors.TryGetValue(vm.Model.Id, out var e) ? e : null;
            if (_propsError != null && _propsLayer != null)
            {
                var err = Ctl.Engine.LayerErrors.TryGetValue(_propsLayer.Id, out var e) ? e : null;
                _propsError.Text = err ?? "";
                _propsError.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
            }
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var e = Ctl.Engine;
            string ok(bool b) => b ? "ok" : "indisponível";
            int ddc = e.Hardware.IsReady ? e.Hardware.Monitors.Count(m => m.Ok) : 0;
            EngineStatus.Text = $"Cor: {ok(e.Matrix.Ready)} · Tons: {e.Monitors.Count} · NVIDIA: {ok(e.Vibrance.Ready)} · DDC/CI: {(e.Hardware.IsReady ? $"{ddc}" : "…")}";
            UndoButton.IsEnabled = Ctl.Undo.CanUndo;
            RedoButton.IsEnabled = Ctl.Undo.CanRedo;
        }

        // ------------------------------------------------------------------ help box

        void OnHelpMouseMove(object sender, MouseEventArgs e)
        {
            if (!Ctl.Settings.HelpVisible) return;
            var src = e.OriginalSource as DependencyObject;
            HelpInfo? h = null;
            for (var d = src; d != null && h == null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                var v = d.GetValue(Help.InfoProperty);
                if (v is HelpInfo hi) h = hi;
                else if (v is Func<HelpInfo?> f) h = f();
                else if (d is FrameworkElement fe && fe.Tag is string tag && HelpTexts.Ui.TryGetValue(tag, out var th)) h = th;
                else if (d is ListBoxItem li && li.DataContext is LayerVM lvm) h = LayerHelp(lvm.Model);
                else if (d is ListBoxItem pi && pi.DataContext is PresetVM pvm) h = PresetHelp(pvm.Model);
                if (d == this) break;
            }
            ShowHelp(h);
        }

        void ShowHelp(HelpInfo? h)
        {
            h ??= HelpTexts.Idle;
            if (h == _lastHelp) return;
            _lastHelp = h;
            HelpTitle.Text = h.Title;
            HelpBody.Text = h.Body;
            HelpAffects.Text = h.Affects ?? "";
            HelpAffects.Visibility = string.IsNullOrEmpty(h.Affects) ? Visibility.Collapsed : Visibility.Visible;
        }

        void ToggleHelp()
        {
            Ctl.Settings.HelpVisible = !Ctl.Settings.HelpVisible;
            HelpBox.Visibility = Ctl.Settings.HelpVisible ? Visibility.Visible : Visibility.Collapsed;
            Ctl.ScheduleSave();
        }

        void HelpToggle_Click(object sender, RoutedEventArgs e) => ToggleHelp();

        string Affects(Layer l)
        {
            var def = LayerDefs.Get(l.Kind);
            var mons = Ctl.Engine.Monitors.Where(m => l.AppliesTo(m.DeviceName)).Select(m => m.Short).ToList();
            var where = def.PerMonitor ? (mons.Count == Ctl.Engine.Monitors.Count ? "todos os monitores" : string.Join(", ", mons)) : "todos os monitores";
            return $"Afeta: {where} · {HelpTexts.EngineFacts(def.Engine, Ctl.Settings.HideOverlayFromCapture)} · {Ui.ResourceText(l.Kind)}";
        }

        HelpInfo LayerHelp(Layer l)
        {
            var cat = Categories.Of(l.Kind);
            return new HelpInfo($"{l.DisplayName} · {cat.Name}", HelpTexts.Layer.TryGetValue(l.Kind, out var b) ? b : "", Affects(l));
        }

        HelpInfo PresetHelp(Preset p)
        {
            var parts = new List<string>();
            if (Ctl.Stack.Local == p) parts.Add("é o preset local agora");
            var rules = Ctl.Settings.AppRules.Where(r => r.Preset == p.Name).Select(r => r.Process).ToList();
            if (rules.Count > 0) parts.Add("programas: " + string.Join(", ", rules));
            return new HelpInfo(p.Name, "Clique para usá-lo como preset local (a base da tela) e editar seus ajustes. A agenda soma por cima.",
                parts.Count > 0 ? string.Join(" · ", parts) : null);
        }

        // ------------------------------------------------------------------ header

        Grid? _helpHome, _propsHome;

        static void Detach(FrameworkElement el)
        {
            switch (el.Parent)
            {
                case Panel p: p.Children.Remove(el); break;
                case Decorator d: d.Child = null; break;
            }
        }

        /// <summary>The help box and the properties panel are shared: they move into the Agenda tab while it's open.</summary>
        void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (LayersPage == null || AgendaPageControl == null) return;
            _helpHome ??= (Grid)HelpBox.Parent;
            _propsHome ??= (Grid)PropsHost.Parent;
            bool layers = TabLayers.IsChecked == true, agenda = TabAgenda.IsChecked == true;
            if (!agenda && Ctl.Simulated != null) Ctl.Simulate(null); // leaving the Agenda returns to live
            LayersPage.Visibility = layers ? Visibility.Visible : Visibility.Collapsed;
            AgendaPageControl.Visibility = agenda ? Visibility.Visible : Visibility.Collapsed;

            Detach(HelpBox);
            Detach(PropsHost);
            if (agenda)
            {
                Agenda.HelpHost.Child = HelpBox;
                Agenda.RightHost.Child = PropsHost;
                Agenda.Rebuild();
                ShowAgendaProps();
            }
            else
            {
                _helpHome.Children.Add(HelpBox);
                _propsHome.Children.Add(PropsHost);
                ShowProps(SelectedLayer);
            }
        }

        void ClearAgendaSelection()
        {
            _agendaTrack = null; _point = null; _pointCtl = null;
            Agenda.ClearSelection();
        }

        void ShowAgendaProps()
        {
            if (_agendaTrack == null)
            {
                ShowProps(null);
                EmptyHint.Text = "Selecione um ajuste ou um ponto da agenda.";
                return;
            }
            ShowProps(_agendaTrack.Layer, _agendaTrack, _agendaTrackLinked);
            if (_pointCtl != null && _point != null && _pointCtl.Track.Id == _agendaTrack.Id) PropsPanel.Children.Insert(0, BuildPointEditor(_pointCtl, _point));
        }

        /// <summary>Numeric editor for the selected keyframe: time, value, curve and sun anchor.</summary>
        FrameworkElement BuildPointEditor(LaneControl ctl, Keyframe k)
        {
            var kind = ctl.Track.Layer.Kind;
            var range = Automation.Range(kind, ctl.Lane.Param) ?? (0, 100, false, "");
            var loc = Ctl.Settings.Location;
            // A section closed by a hairline below — not a card inside the inspector.
            var card = new Border { BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Margin = new Thickness(0, 0, 0, 18) };
            var sp = new StackPanel();
            var ttl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            ttl.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Ui.Res("Dim"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            ttl.Children.Add(new TextBlock { Text = $"Ponto de {Automation.Label(kind, ctl.Lane.Param).ToLowerInvariant()}", FontWeight = FontWeights.SemiBold });
            sp.Children.Add(ttl);
            void Changed() { ctl.InvalidateVisual(); Agenda.RefreshValues(); Ctl.ScheduleApply(); }

            // anchor
            var anchors = new List<Choice> { new("0", "Horário fixo"), new("1", "Nascer do sol"), new("2", "Pôr do sol") };
            var anchor = new ComboBox { ItemsSource = anchors, SelectedIndex = (int)k.Anchor, IsEnabled = loc != null || k.Anchor != Anchor.None };
            if (loc == null) anchor.ToolTip = "Escolha sua cidade em Configurações";
            var timeBox = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            var timeLabel = new TextBlock { Foreground = Ui.Res("Dim"), VerticalAlignment = VerticalAlignment.Center };
            void SyncTime()
            {
                double t = Automation.TimeOf(k, (Ctl.Simulated ?? DateTime.Now).Date, loc);
                timeBox.Text = k.Anchor == Anchor.None ? ArrangementView.Fmt(t) : ((k.Offset >= 0 ? "+" : "") + k.Offset.ToString("0"));
                timeLabel.Text = k.Anchor == Anchor.None ? "" : $"min  (hoje {ArrangementView.Fmt(t)})";
            }
            SyncTime();
            anchor.SelectionChanged += (_, _) =>
            {
                double t = Automation.TimeOf(k, DateTime.Today, loc);
                k.Anchor = (Anchor)anchor.SelectedIndex;
                if (k.Anchor == Anchor.None) { k.T = t; k.Offset = 0; }
                else k.Offset = Math.Round(t - Automation.TimeOf(new Keyframe { Anchor = k.Anchor }, DateTime.Today, loc));
                SyncTime(); Changed();
            };
            sp.Children.Add(Ui.Row("Referência", anchor, 110));
            void CommitTime()
            {
                var txt = timeBox.Text.Trim();
                if (k.Anchor == Anchor.None)
                {
                    var parts = txt.Replace('h', ':').Split(':');
                    if (int.TryParse(parts[0], out var h) && h is >= 0 and <= 24)
                    {
                        int m = parts.Length > 1 && int.TryParse(parts[1], out var mm) ? Math.Clamp(mm, 0, 59) : 0;
                        k.T = (h * 60 + m) % 1440;
                    }
                }
                else if (double.TryParse(txt.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var off)) k.Offset = Math.Clamp(off, -720, 720);
                SyncTime(); Changed();
            }
            timeBox.LostKeyboardFocus += (_, _) => CommitTime();
            timeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitTime(); };
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
            timeRow.Children.Add(timeBox);
            timeRow.Children.Add(timeLabel);
            sp.Children.Add(Ui.Row("Horário", timeRow, 110));

            var step = range.stepped ? 1 : (range.max - range.min) <= 10 ? 0.01 : 1;
            var (valRow, _, _) = Ui.Slider("Valor", range.min, range.max, step, k.V, k.V, range.unit, v => { k.V = v; Changed(); });
            sp.Children.Add(valRow);
            if (!range.stepped)
            {
                var (curveRow, _, _) = Ui.Slider("Curva até o próximo ponto", -1, 1, 0.05, k.Curve, 0, "", v => { k.Curve = v; Changed(); });
                Help.Set(curveRow, new HelpInfo("Curva", "0 = reta. Positivo: muda rápido no começo e devagar no fim; negativo: o contrário. Também dá para Alt+arrastar o trecho na automação.", null));
                sp.Children.Add(curveRow);
            }
            var del = new Button { Content = "Excluir ponto", Foreground = Ui.Res("Danger"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            del.Click += (_, _) => { ctl.Lane.Points.Remove(k); ctl.Select(null); _point = null; _pointCtl = null; Changed(); ShowAgendaProps(); };
            sp.Children.Add(del);
            card.Child = sp;
            return card;
        }

        void MasterSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            Ctl.SetMaster(MasterSwitch.IsChecked == true);
        }

        void ResetButton_Click(object sender, RoutedEventArgs e) => Ctl.PanicReset();
        void Undo_Click(object sender, RoutedEventArgs e) => Ctl.DoUndo();
        void Redo_Click(object sender, RoutedEventArgs e) => Ctl.DoRedo();
        void Bypass_Down(object sender, MouseButtonEventArgs e) => Ctl.SetBypass(true);
        void Bypass_Up(object sender, MouseButtonEventArgs e) => Ctl.SetBypass(false);
        void Bypass_Leave(object sender, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Released && !_bypassKey) Ctl.SetBypass(false); }

        void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this }.ShowDialog();
            Ctl.RefreshWarnings();
            Ctl.ApplyNow();
            Agenda.RefreshValues();
        }

        // ------------------------------------------------------------------ presets

        /// <summary>The preset list sits in an outer scroller (so “+ Novo preset” hugs the last item): pass the wheel up to it.</summary>
        void PresetList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (e.Handled || ((FrameworkElement)sender).Parent is not FrameworkElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
        }

        void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || PresetList.SelectedItem is not PresetVM vm) return;
            _propsLayer = null;
            ClearAgendaSelection();
            Ctl.SetManual(vm.Model);
        }

        string UniqueName(string baseName)
        {
            var name = baseName;
            int n = 2;
            while (Ctl.Settings.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} {n++}";
            return name;
        }

        void NewPreset_Click(object sender, RoutedEventArgs e)
        {
            var name = InputDialog.Ask(this, "Novo preset", "Nome do preset:", UniqueName("Novo preset"));
            if (string.IsNullOrWhiteSpace(name)) return;
            var p = new Preset { Name = UniqueName(name.Trim()) };
            Ctl.Settings.Presets.Add(p);
            Ctl.SetManual(p);
        }

        void DuplicatePreset_Click(object sender, RoutedEventArgs e)
        {
            var src = Ctl.Manual;
            var p = new Preset { Name = UniqueName(src.Name + " cópia"), Layers = src.Layers.Select(l => l.Clone()).ToList() };
            Ctl.Settings.Presets.Insert(Ctl.Settings.Presets.IndexOf(src) + 1, p);
            Ctl.SetManual(p);
        }

        void RenamePreset_Click(object sender, RoutedEventArgs e)
        {
            var p = Ctl.Manual;
            var name = InputDialog.Ask(this, "Renomear preset", "Novo nome:", p.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == p.Name) return;
            Ctl.RenamePreset(p, UniqueName(name.Trim()));
        }

        void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (Ctl.Settings.Presets.Count <= 1) { MessageBox.Show(this, "É preciso ter pelo menos um preset.", "ScreenMaster"); return; }
            var p = Ctl.Manual;
            if (MessageBox.Show(this, $"Excluir o preset “{p.Name}”? (Ctrl+Z desfaz)", "ScreenMaster", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _propsLayer = null;
            Ctl.DeletePreset(p);
        }

        // ------------------------------------------------------------------ program rules

        void RulesButton_Click(object sender, RoutedEventArgs e)
        {
            BuildRulesPanel();
            RulesPopup.IsOpen = true;
            if (RulesPopup.Child is FrameworkElement rc) { Access.Scale(rc); Motion.Enter(rc, new Point(0, 0)); }
        }

        void BuildRulesPanel()
        {
            var p = RulesPanel;
            p.Children.Clear();
            p.Children.Add(new TextBlock { Text = "Regras por programa", Style = (Style)FindResource("Caption") });
            p.Children.Add(Ui.Note("Quando o programa estiver na frente: usar outro preset local, outro perfil da agenda e/ou pausar a agenda (útil em jogos). Ao trocar de programa, volta ao normal."));

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proc in Process.GetProcesses())
            {
                try { if (proc.MainWindowHandle != IntPtr.Zero && proc.Id != Environment.ProcessId) names.Add(proc.ProcessName); }
                catch { }
                finally { proc.Dispose(); }
            }
            foreach (var r in Ctl.Settings.AppRules) names.Remove(r.Process);
            var picker = new ComboBox { ItemsSource = names.ToList() };
            var add = new Button { Content = "Adicionar", Margin = new Thickness(6, 0, 0, 0) };
            add.Click += (_, _) =>
            {
                if (picker.SelectedItem is not string name) return;
                Ctl.Settings.AppRules.Add(new AppRule { Process = name, Preset = null, PauseAgenda = true });
                RulesChanged();
                BuildRulesPanel();
            };
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);
            row.Children.Add(picker);
            p.Children.Add(row);

            var presetChoices = new List<Choice> { new("", "(manter o local)") };
            presetChoices.AddRange(Ctl.Settings.Presets.Select(x => new Choice(x.Name, x.Name)));
            var profileChoices = new List<Choice> { new("", "(manter o perfil)") };
            profileChoices.AddRange(Ctl.Settings.Profiles.Select(x => new Choice(x.Id.ToString(), "perfil " + x.Name)));
            foreach (var rule in Ctl.Settings.AppRules.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = rule.Process, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = rule.Process });
                var combo = new ComboBox { ItemsSource = presetChoices, SelectedItem = presetChoices.FirstOrDefault(c => c.Key == (rule.Preset ?? "")) ?? presetChoices[0] };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is Choice c) { rule.Preset = c.Key.Length == 0 ? null : c.Key; RulesChanged(); } };
                Grid.SetColumn(combo, 1);
                g.Children.Add(combo);
                var pause = new CheckBox { Content = "pausar agenda", IsChecked = rule.PauseAgenda, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pause.Checked += (_, _) => { rule.PauseAgenda = true; RulesChanged(); };
                pause.Unchecked += (_, _) => { rule.PauseAgenda = false; RulesChanged(); };
                Grid.SetColumn(pause, 2);
                g.Children.Add(pause);
                var rm = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 22, Height = 20, FontSize = 13, Margin = new Thickness(6, 0, 0, 0) };
                rm.Click += (_, _) => { Ctl.Settings.AppRules.Remove(rule); RulesChanged(); BuildRulesPanel(); };
                Grid.SetColumn(rm, 3);
                g.Children.Add(rm);
                p.Children.Add(g);
                var profCombo = new ComboBox { ItemsSource = profileChoices, SelectedItem = profileChoices.FirstOrDefault(c => c.Key == (rule.Profile?.ToString() ?? "")) ?? profileChoices[0] };
                profCombo.SelectionChanged += (_, _) =>
                {
                    if (profCombo.SelectedItem is Choice c) { rule.Profile = c.Key.Length == 0 ? null : Guid.Parse(c.Key); RulesChanged(); }
                };
                var g2 = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                g2.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(profCombo, 1);
                g2.Children.Add(profCombo);
                p.Children.Add(g2);
            }
            if (Ctl.Settings.AppRules.Count == 0) p.Children.Add(Ui.Note("Nenhuma regra ainda. Abra o programa (ou jogo) e escolha-o na lista acima."));
        }

        void RulesChanged()
        {
            Ctl.ReevaluateRules();
            Ctl.NotifyPresetsChanged();
        }

        void LayersSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            Ctl.SetLayers(LayersSwitch.IsChecked == true);
        }

        void AutoSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            Ctl.Settings.AutoSwitch = AutoSwitchCheck.IsChecked == true;
            Ctl.ReevaluateRules();
            Ctl.ScheduleSave();
        }

        // ------------------------------------------------------------------ layers

        Layer? SelectedLayer => (LayerList.SelectedItem as LayerVM)?.Model;

        void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            ClearAgendaSelection();
            ShowProps(SelectedLayer);
        }

        void AddLayer_Click(object sender, RoutedEventArgs e) => LayerPicker.Show(AddLayerButton, AddLayer, ShowHelp, k => LayerSetup.Blocked(k, Ctl.Manual.Layers));

        void AddLayer(LayerKind kind)
        {
            var layers = Ctl.Manual.Layers;
            var l = Layer.Create(kind);
            LayerSetup.Prepare(l, layers, () => { if (_propsLayer == l) ShowProps(l); });
            var sel = SelectedLayer;
            int idx = sel != null ? layers.IndexOf(sel) + 1 : layers.Count;
            layers.Insert(idx, l);
            _propsLayer = l;
            ClearAgendaSelection();
            BuildLayers(l.Id);
            RefreshPresetSummaries();
            Ctl.ScheduleApply();
        }

        void MoveLayer(int delta)
        {
            var sel = SelectedLayer;
            if (sel == null) return;
            var layers = Ctl.Manual.Layers;
            int i = layers.IndexOf(sel), j = i + delta;
            if (j < 0 || j >= layers.Count) return;
            layers.RemoveAt(i);
            layers.Insert(j, sel);
            BuildLayers(sel.Id);
            Ctl.ScheduleApply();
        }

        void LayerUp_Click(object sender, RoutedEventArgs e) => MoveLayer(+1);
        void LayerDown_Click(object sender, RoutedEventArgs e) => MoveLayer(-1);

        void DuplicateLayer_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedLayer;
            if (sel == null) return;
            var c = sel.Clone();
            var layers = Ctl.Manual.Layers;
            layers.Insert(layers.IndexOf(sel) + 1, c);
            BuildLayers(c.Id);
            RefreshPresetSummaries();
            Ctl.ScheduleApply();
        }

        void DeleteLayer_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedLayer;
            if (sel == null) return;
            var layers = Ctl.Manual.Layers;
            int i = layers.IndexOf(sel);
            layers.Remove(sel);
            var next = layers.Count == 0 ? null : layers[Math.Clamp(i - 1, 0, layers.Count - 1)];
            _propsLayer = next;
            BuildLayers(next?.Id);
            RefreshPresetSummaries();
            Ctl.ScheduleApply();
        }

        // ------------------------------------------------------------------ properties panel

        void LayerEdited(Layer l)
        {
            _layers.FirstOrDefault(v => v.Model == l)?.Refresh();
            Ctl.ScheduleApply();
        }

        /// <summary>A value moved by hand on an automated agenda param pauses that lane (Ableton override).</summary>
        void TrackParamEdited(string param)
        {
            if (_propsTrack != null && Ctl.Simulated != null)
            {
                // pinned time: the slider writes the automation at that time (Ableton "write"), screen shows the result
                var src = _propsTrack.Layer;
                if (AgendaPage.WriteAtPinned(_propsTrack, param, param == "opacity" ? src.Opacity : src.Get(param))) { Agenda.RefreshValues(); return; }
            }
            if (_propsTrack != null && AgendaPage.Override(_propsTrack, param)) Agenda.Rebuild();
            else Agenda.RefreshValues();
        }

        /// <summary>Puts the automation pill right after a slider row's label.</summary>
        static void AttachClock(Grid sliderRow, Button clock)
        {
            var title = sliderRow.Children.OfType<FrameworkElement>().FirstOrDefault(c => Grid.GetRow(c) == 0 && Grid.GetColumn(c) == 0);
            if (title == null) return;
            sliderRow.Children.Remove(title);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
            sp.Children.Add(title);
            sp.Children.Add(clock);
            sliderRow.Children.Add(sp);
        }

        static FrameworkElement WithClock(FrameworkElement row, Button clock)
        {
            var d = new DockPanel();
            clock.VerticalAlignment = VerticalAlignment.Top;
            clock.Margin = new Thickness(8, 4, 0, 0);
            DockPanel.SetDock(clock, Dock.Right);
            d.Children.Add(clock);
            d.Children.Add(row);
            return d;
        }

        /// <param name="track">Own agenda track, or a wrapper (preset layer + linked lanes) when <paramref name="linked"/>.</param>
        void ShowProps(Layer? l, AgendaTrack? track = null, bool linked = false)
        {
            // A local layer automated by today's agenda: show it through its linked lanes.
            if (l != null && track == null && !InAgendaTab && AutomationProfile.LinkedFor(l.Id) is { } la && la.Lanes.Count > 0)
            {
                track = new AgendaTrack { Id = l.Id, Layer = l, Lanes = la.Lanes };
                linked = true;
            }
            _propsLayer = l;
            _propsTrack = track;
            PropsPanel.Children.Clear();
            _propsError = null;
            EmptyHint.Visibility = l == null ? Visibility.Visible : Visibility.Collapsed;
            PropsScroll.Visibility = l == null ? Visibility.Collapsed : Visibility.Visible;
            if (!InAgendaTab) EmptyHint.Text = "Selecione um ajuste do preset local.";
            if (l == null) return;

            var def = LayerDefs.Get(l.Kind);
            var cat = Categories.Of(l.Kind);
            Func<HelpInfo?> layerHelp = () => LayerHelp(l);
            var ownTrack = linked ? null : track;
            var automatable = Automation.Automatable(l.Kind).Select(a => a.key).ToHashSet();

            // Evaluated values for automated params, so sliders show what's on screen now.
            Layer? evaluated = track != null ? Automation.EvalTrack(track, Ctl.Simulated ?? DateTime.Now, Ctl.Settings.Location, false) : null;
            bool Automated(string key) => track?.Lanes.Any(x => x.Param == key && x.Points.Count > 0 && !x.Overridden) == true;
            double Val(string key) => key == "opacity" ? (Automated(key) && evaluated != null ? evaluated.Opacity : l.Opacity)
                                                       : (Automated(key) && evaluated != null ? evaluated.Get(key) : l.Get(key));
            string Lbl(string key, string label) => label;

            // header: the layer name is the title (editable in place); category as a small tick + quiet line
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            var titles = head;
            var nameBox = new TextBox { Text = l.DisplayName, FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(-4, 0, 0, 0) };
            var catLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            catLine.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(1), Background = Ui.Hex(cat.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            catLine.Children.Add(new TextBlock { Text = $"{cat.Name} · {cat.Subtitle}", Foreground = Ui.Res("Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            catLine.Children.Add(new Border { Width = 1, Height = 10, Background = Ui.Res("Line"), Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
            catLine.Children.Add(Ui.ResourceChips(l.Kind));
            nameBox.TextChanged += (_, _) =>
            {
                var t = nameBox.Text.Trim();
                l.Name = t.Length == 0 || t == def.Name || t == l.AutoName ? null : t;
                _layers.FirstOrDefault(v => v.Model == l)?.Refresh();
                Ctl.ScheduleSave();
            };
            nameBox.LostKeyboardFocus += (_, _) => { if (track != null) Agenda.Rebuild(); };
            titles.Children.Add(nameBox);
            titles.Children.Add(catLine);
            Help.Set(head, layerHelp);
            PropsPanel.Children.Add(head);

            if (!string.IsNullOrEmpty(def.Hint))
                PropsPanel.Children.Add(new TextBlock { Text = def.Hint, Foreground = Ui.Res("Dim"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

            if (track != null)
            {
                // agenda note: an amber dot (= automated/live) and one quiet line, not a tinted box
                var prof = InAgendaTab ? Ctl.EditingProfile : AutomationProfile;
                var note = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Res("Dim"), Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(dot, Dock.Left);
                note.Children.Add(dot);
                note.Children.Add(Ui.Note(linked
                    ? $"Valores com “agenda ›” seguem a agenda (perfil “{prof.Name}”). Mexer à mão pausa a automação até “↺ voltar”."
                    : $"Ajuste da agenda, perfil “{prof.Name}”. “+ agenda” automatiza um valor; mexer à mão pausa até “↺ voltar”."));
                PropsPanel.Children.Add(note);
            }

            _propsError = new TextBlock { Foreground = Ui.Res("Danger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            PropsPanel.Children.Add(_propsError);
            PropsPanel.Children.Add(new Border { Height = 1, Background = Ui.Res("Line"), Margin = new Thickness(0, 16, 0, 16) });

            PropsPanel.Children.Add(BuildMonitorPicker(l, def, track));

            var (opRow, _, _) = Ui.Slider(Lbl("opacity", "Opacidade"), 0, 100, 1, Val("opacity"), 100, "%", v => { l.Opacity = v; if (track != null) TrackParamEdited("opacity"); LayerEdited(l); });
            AttachClock(opRow, ClockButton(l, "opacity", ownTrack, opRow));
            Help.Set(opRow, () => ParamHelp(l, "opacity", "Opacidade"));
            PropsPanel.Children.Add(opRow);

            CurveEditor? curve = null;
            foreach (var p in def.Params)
            {
                FrameworkElement row;
                var key = p.Key;
                if (!LayerSetup.Supports(l, key)) continue;
                void Edited() { if (track != null) TrackParamEdited(key); LayerEdited(l); }
                switch (p.Type)
                {
                    case ParamType.Slider when p.Optional:
                        row = Ui.Slider(Lbl(key, p.Label), p.Min, p.Max, p.Step, Val(key), p.Default, p.Unit, v => { l.P[key] = v; Edited(); },
                            optionalOn: l.IsOn(key) || Automated(key), optionalChanged: on => { l.P[key + "_on"] = on ? 1 : 0; Edited(); }).row;
                        break;
                    case ParamType.Slider when l.Kind == LayerKind.Grain && key == "fps":
                    {
                        // grain speed goes up to the fastest refresh rate among the layer's monitors (144 Hz → 144)
                        int maxFps = LayerSetup.MaxFps(l);
                        row = Ui.Slider(Lbl(key, p.Label), 0, maxFps, 1, Math.Min(Val(key), maxFps), maxFps, p.Unit, v => { l.P[key] = v; Edited(); }).row;
                        break;
                    }
                    case ParamType.Slider:
                        row = Ui.Slider(Lbl(key, p.Label), p.Min, p.Max, p.Step, Val(key), p.Default, p.Unit, v => { l.P[key] = v; Edited(); }).row;
                        break;
                    case ParamType.Toggle:
                    {
                        var cb = new CheckBox { Content = Lbl(key, p.Label), IsChecked = Val(key) > 0.5, Margin = new Thickness(0, 4, 0, 10) };
                        cb.Checked += (_, _) => { l.P[key] = 1; Edited(); };
                        cb.Unchecked += (_, _) => { l.P[key] = 0; Edited(); };
                        row = cb;
                        break;
                    }
                    case ParamType.Color:
                        row = ColorRow(p.Label, l.GetS(key), hex => { l.S[key] = hex; LayerEdited(l); });
                        break;
                    default: // Choice
                    {
                        var combo = new ComboBox { ItemsSource = p.Choices, SelectedIndex = (int)Math.Round(Val(key)) };
                        combo.SelectionChanged += (_, _) =>
                        {
                            l.P[key] = combo.SelectedIndex;
                            if (def.HasCurve && curve != null) { curve.Channel = combo.SelectedIndex; Ctl.ScheduleSave(); }
                            else Edited();
                        };
                        if (p.Optional)
                        {
                            var check = new CheckBox { Content = Lbl(key, p.Label), IsChecked = l.IsOn(key), Foreground = Ui.Res("Dim"), VerticalAlignment = VerticalAlignment.Center };
                            combo.IsEnabled = l.IsOn(key);
                            check.Checked += (_, _) => { combo.IsEnabled = true; l.P[key + "_on"] = 1; Edited(); };
                            check.Unchecked += (_, _) => { combo.IsEnabled = false; l.P[key + "_on"] = 0; Edited(); };
                            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                            g.ColumnDefinitions.Add(new ColumnDefinition());
                            g.Children.Add(check);
                            Grid.SetColumn(combo, 1);
                            g.Children.Add(combo);
                            row = g;
                        }
                        else row = Ui.Row(Lbl(key, p.Label), combo);
                        break;
                    }
                }
                if (automatable.Contains(key))
                {
                    var clock = ClockButton(l, key, ownTrack, row);
                    if (p.Type == ParamType.Slider && row is Grid sg) AttachClock(sg, clock);
                    else row = WithClock(row, clock);
                }
                var label = p.Label;
                Help.Set(row, () => ParamHelp(l, key, label));
                PropsPanel.Children.Add(row);
            }

            if (def.HasCurve)
            {
                curve = new CurveEditor { Margin = new Thickness(0, 4, 0, 8) };
                curve.Bind(l, (int)l.Get("channel"));
                curve.Changed += () => LayerEdited(l);
                Help.Set(curve, new HelpInfo("Editor de curva", "Eixo horizontal = brilho original, vertical = brilho final. Clique para adicionar ponto, arraste para mover, clique direito para remover.", null));
                PropsPanel.Children.Add(curve);

                var bar2 = new DockPanel { Margin = new Thickness(0, 4, 0, 12) };
                var group = new Border { BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
                var shapes = new StackPanel { Orientation = Orientation.Horizontal };
                group.Child = shapes;
                var restores = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                void Apply(Action a) { a(); curve.InvalidateVisual(); LayerEdited(l); }
                void Shape(string text, Action a)
                {
                    var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), BorderThickness = new Thickness(0), MinHeight = 26,
                                         Style = (Style)FindResource("Quiet"), Foreground = Ui.Res("Text") };
                    b.Click += (_, _) => Apply(a);
                    if (shapes.Children.Count > 0) shapes.Children.Add(new Border { Width = 1, Background = Ui.Res("Line") });
                    shapes.Children.Add(b);
                }
                void Restore(string text, Action a)
                {
                    var b = new Button { Content = text, Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(8, 3, 8, 3), MinHeight = 26 };
                    b.Click += (_, _) => Apply(a);
                    restores.Children.Add(b);
                }
                List<double[]> P(params double[] xy) { var r = new List<double[]>(); for (int i = 0; i < xy.Length; i += 2) r.Add(new[] { xy[i], xy[i + 1] }); return r; }
                Shape("Contraste", () => l.Curves[curve.Channel.ToString()] = P(0, 0, 64, 52, 192, 204, 255, 255));
                Shape("Clarear", () => l.Curves[curve.Channel.ToString()] = P(0, 0, 128, 150, 255, 255));
                Shape("Escurecer", () => l.Curves[curve.Channel.ToString()] = P(0, 0, 128, 106, 255, 255));
                Shape("Fosco", () => l.Curves[curve.Channel.ToString()] = P(0, 24, 64, 66, 192, 196, 255, 240));
                Restore("Restaurar canal", () => l.Curves[curve.Channel.ToString()] = P(0, 0, 255, 255));
                Restore("Restaurar tudo", () => l.Curves.Clear());
                Help.Set(group, new HelpInfo("Formas prontas", "Substituem a curva do canal escolhido por uma forma comum: Contraste (curva em S), Clarear, Escurecer ou Fosco (pretos levantados). Ctrl+Z desfaz.", null));
                DockPanel.SetDock(restores, Dock.Right);
                bar2.Children.Add(restores);
                bar2.Children.Add(group);
                group.HorizontalAlignment = HorizontalAlignment.Left;
                PropsPanel.Children.Add(bar2);
            }

            var reset = new Button { Content = "Restaurar padrões deste ajuste", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            reset.Click += (_, _) =>
            {
                var fresh = Layer.Create(l.Kind);
                l.P = fresh.P; l.S = fresh.S; l.Curves = fresh.Curves; l.Opacity = fresh.Opacity;
                LayerEdited(l);
                ShowProps(l, track);
            };
            PropsPanel.Children.Add(reset);
            RefreshErrors();
        }

        HelpInfo ParamHelp(Layer l, string key, string label)
        {
            var body = key == "opacity" ? HelpTexts.Param["opacity"] : HelpTexts.Param.TryGetValue(key, out var t) ? t : (HelpTexts.Layer.TryGetValue(l.Kind, out var lt) ? lt : "");
            if (_propsTrack != null && _propsTrack.Layer == l && _propsTrack.Lanes.Any(x => x.Param == key && x.Points.Count > 0))
                body += " Este valor tem automação na agenda: mexer aqui à mão pausa a automação até “↺ voltar”.";
            return new HelpInfo($"{label} · {l.DisplayName}", body, Affects(l));
        }

        FrameworkElement BuildMonitorPicker(Layer l, LayerDef def, AgendaTrack? track)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var id = new Button { Content = "Identificar", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            DockPanel.SetDock(id, Dock.Right);
            id.Click += (_, _) => Identify.Show(Ctl.Engine.Monitors);
            Help.Set(id, "identify");
            head.Children.Add(id);
            head.Children.Add(new TextBlock { Text = def.SingleMonitor ? "Monitor" : "Monitores", Style = (Style)FindResource("Caption"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(head);

            var mons = Ctl.Engine.Monitors;

            // Monitor adjustment: exactly one monitor, chosen here; another monitor = another adjustment.
            if (def.SingleMonitor)
            {
                var items = mons.Select(m => new Choice(m.DeviceName, m.Label)).ToList();
                var current = l.Monitors.FirstOrDefault();
                var combo = new ComboBox { ItemsSource = items, SelectedItem = items.FirstOrDefault(i => i.Key == current) ?? items.FirstOrDefault() };
                IEnumerable<Layer> owner = track != null && !_agendaTrackLinked && InAgendaTab ? Ctl.EditingProfile.Tracks.Select(t => t.Layer) : Ctl.Manual.Layers;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not Choice ch || l.Monitors.FirstOrDefault() == ch.Key) return;
                    var other = owner.FirstOrDefault(x => x != l && x.Kind == l.Kind && x.AppliesTo(ch.Key));
                    if (other != null)
                    {
                        ShowToast($"{ch.Label.Split('—')[0].Trim()} já tem um ajuste Monitor aqui.");
                        combo.SelectedItem = items.FirstOrDefault(i => i.Key == l.Monitors.FirstOrDefault());
                        return;
                    }
                    l.Monitors = new List<string> { ch.Key };
                    LayerEdited(l);
                    _layers.FirstOrDefault(v => v.Model == l)?.Refresh(); // the automatic name carries the monitor
                    Agenda.Rebuild();
                    LayerSetup.LoadMonitorValues(l, () => { if (_propsLayer == l) ShowProps(l, track, InAgendaTab && _agendaTrackLinked); });
                    ShowProps(l, track, InAgendaTab && _agendaTrackLinked);
                };
                if (l.Monitors.Count != 1 && combo.SelectedItem is Choice first) l.Monitors = new List<string> { first.Key };
                panel.Children.Add(combo);
                var ddc = l.Monitors.Count == 1 && Ctl.Engine.Hardware.IsReady ? Ctl.Engine.Hardware.Get(l.Monitors[0]) : null;
                if (Ctl.Engine.Hardware.IsReady && ddc == null)
                    panel.Children.Add(new TextBlock { Text = "Este monitor não respondeu a DDC/CI. Ative “DDC/CI” no menu do monitor.", Foreground = Ui.Res("Danger"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
                else panel.Children.Add(Ui.Note("Marque o que este ajuste muda. Desmarcado, o monitor fica como está."));
                Help.Set(panel, () => new HelpInfo("Monitor", HelpTexts.Param["monitor"], Affects(l)));
                return panel;
            }
            if (def.Engine == EngineKind.System)
            {
                panel.Children.Add(Ui.Note("Vale para o Windows inteiro, não para um monitor."));
                return panel;
            }
            IEnumerable<Layer> siblings = track != null && !_agendaTrackLinked && InAgendaTab ? Ctl.EditingProfile.Tracks.Select(t => t.Layer) : Ctl.Manual.Layers;
            var checks = new List<(CheckBox cb, MonitorInfo m)>();
            foreach (var m in mons)
            {
                var cb = new CheckBox { Content = m.Label, IsChecked = l.AppliesTo(m.DeviceName), Margin = new Thickness(0, 0, 0, 6) };
                if (!def.PerMonitor) { cb.IsEnabled = false; cb.IsChecked = true; }
                checks.Add((cb, m));
                panel.Children.Add(cb);
            }
            var note = Ui.Note(def.PerMonitor ? "" : "Ajustes de Cor valem para todos os monitores (limite do Windows).");
            panel.Children.Add(note);

            if (def.PerMonitor)
            {
                bool busy = false;
                foreach (var (cb, m) in checks)
                {
                    void Changed(object? s, RoutedEventArgs e)
                    {
                        if (busy) return;
                        if (def.Unique && cb.IsChecked == true && siblings.Any(x => x != l && x.Kind == l.Kind && x.AppliesTo(m.DeviceName)))
                        {
                            busy = true; cb.IsChecked = false; busy = false;
                            note.Text = $"{m.Short} já tem outro ajuste “{def.Name}” aqui. Só pode haver um por monitor.";
                            return;
                        }
                        var on = checks.Where(c => c.cb.IsChecked == true).Select(c => c.m.DeviceName).ToList();
                        if (on.Count == 0)
                        {
                            busy = true; cb.IsChecked = true; busy = false;
                            note.Text = "Pelo menos um monitor precisa ficar marcado.";
                            return;
                        }
                        note.Text = "";
                        l.Monitors = on.Count == mons.Count ? new List<string>() : on;
                        if (l.Kind == LayerKind.Grain)
                        {
                            // the speed limit follows the monitors: only the 60 Hz one checked → at most 60
                            int maxFps = LayerSetup.MaxFps(l);
                            if (l.Get("fps") > maxFps) l.P["fps"] = maxFps;
                            Dispatcher.BeginInvoke(() => { if (_propsLayer == l) ShowProps(l, track, InAgendaTab && _agendaTrackLinked); });
                        }
                        LayerEdited(l);
                        if (track != null) Agenda.Rebuild();
                    }
                    cb.Checked += Changed;
                    cb.Unchecked += Changed;
                }
            }
            Help.Set(panel, () => new HelpInfo("Monitores", HelpTexts.Param["monitors"] + (def.PerMonitor ? "" : " Este ajuste é de Cor: o Windows sempre aplica em todos."), Affects(l)));
            return panel;
        }

        FrameworkElement ColorRow(string label, string hex, Action<string> set)
        {
            var swatch = new Border { Width = 34, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1) };
            var text = new TextBox { Width = 90, Margin = new Thickness(8, 0, 0, 0), Text = hex.ToUpperInvariant() };
            void Show(string h) { try { swatch.Background = (Brush)new BrushConverter().ConvertFromString(h)!; } catch { } }
            Show(hex);
            var btn = new Button { Content = swatch, Padding = new Thickness(3), ToolTip = "Escolher cor" };
            btn.Click += (_, _) =>
            {
                var (r, g, b) = ColorMath.ParseHex(text.Text);
                using var dlg = new WinForms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255)) };
                if (dlg.ShowDialog() == WinForms.DialogResult.OK) text.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            };
            text.TextChanged += (_, _) =>
            {
                var h = text.Text.Trim();
                if (!h.StartsWith("#")) h = "#" + h;
                if (h.Length == 7 && int.TryParse(h.AsSpan(1), NumberStyles.HexNumber, null, out _)) { Show(h); set(h.ToUpperInvariant()); }
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(btn);
            panel.Children.Add(text);
            return Ui.Row(label, panel);
        }
    }
}
