using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>
    /// "Agenda" tab: profiles + week on the left, the arrangement (ruler + tracks) in the middle,
    /// properties of the selected track/point on the right (hosted by MainWindow).
    /// Tracks come in three groups: the agenda's own tracks (always on), automation of the
    /// local preset's layers, and automation of other presets (only acts when that preset is local).
    /// Visual language: docked panes split by hairlines, no cards; color only for category ticks,
    /// profile identity and the amber "now".
    /// </summary>
    public sealed class AgendaPage : Grid
    {
        static App Ctl => App.Ctl;
        public const double HeaderW = 230;

        readonly ArrangementView _view = new();
        readonly StackPanel _profiles = new();
        /// <summary>Day toggles of the profile being edited, beside its title.</summary>
        readonly ContentControl _profileDays = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        readonly TextBlock _freeDays = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        readonly StackPanel _tracks = new();
        readonly ToggleButton _switch;
        readonly TextBlock _sunNote;
        readonly Button _locate;
        readonly TextBlock _title;
        readonly Ruler _ruler;
        readonly TextBlock _rulerHint;
        readonly Button _live;
        readonly ScrollViewer _scroll;
        readonly List<(Lane lane, AgendaTrack tr, TextBlock value, LaneControl ctl, Button back)> _laneRows = new();
        bool _suppress;
        Guid? _selectedId;

        /// <summary>Right column, filled by MainWindow with the shared properties panel.</summary>
        public Decorator RightHost { get; } = new();
        /// <summary>Bottom-left, filled by MainWindow with the shared help box.</summary>
        public Decorator HelpHost { get; } = new();

        /// <summary>Selected track (own track, or a wrapper around a preset layer + its linked lanes).</summary>
        public event Action<AgendaTrack?, bool>? TrackSelected;
        public event Action<LaneControl, Keyframe?>? PointSelected;
        public Action<HelpInfo?>? Hover { get; set; }


        public AgendaPage()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });

            // ---- left pane: switch, profiles, week, sun, help
            var leftGrid = new Grid { Background = Ui.Res("Panel") };
            leftGrid.RowDefinitions.Add(new RowDefinition());
            leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var edge = new Border { BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, 0, 1, 0) };
            Grid.SetRowSpan(edge, 2);
            leftGrid.Children.Add(edge);

            var left = new StackPanel { Margin = new Thickness(12, 12, 12, 12) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            _switch = new ToggleButton { Style = (Style)Application.Current.FindResource("Switch"), Content = "ligada" };
            _switch.Checked += (_, _) => { if (!_suppress) Ctl.SetSchedule(true); };
            _switch.Unchecked += (_, _) => { if (!_suppress) Ctl.SetSchedule(false); };
            Help.Set(_switch, "timelineSwitch");
            DockPanel.SetDock(_switch, Dock.Right);
            head.Children.Add(_switch);
            head.Children.Add(new TextBlock { Text = "Agenda", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(head);

            left.Children.Add(Ui.Caption("Perfis"));
            left.Children.Add(_profiles);
            var newProfile = new Button { Style = (Style)Application.Current.FindResource("Quiet"), Content = "+ Novo perfil", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 2, 0, 0) };
            newProfile.Click += (_, _) =>
            {
                var m = new ContextMenu { PlacementTarget = newProfile };
                var empty = new MenuItem { Header = "Perfil vazio" };
                empty.Click += (_, _) => NewProfile(null);
                var dup = new MenuItem { Header = $"Duplicar “{Profile.Name}”" };
                dup.Click += (_, _) => NewProfile(Profile);
                m.Items.Add(empty); m.Items.Add(dup);
                m.IsOpen = true;
            };
            Help.Set(newProfile, new HelpInfo("Novo perfil", "Crie um perfil para cada uso, como “Dias úteis”, “Fim de semana” ou “Férias”, cada um com sua lista de ajustes, e dê a ele os dias da semana em que vale.", null));
            left.Children.Add(newProfile);
            left.Children.Add(_freeDays);
            left.Children.Add(Ui.Note("Cada dia da semana pertence a um perfil. Clique numa letra para dar o dia a um perfil (sai do outro). Para usar outro perfil hoje, clique no nome do perfil no topo da janela."));

            left.Children.Add(Ui.Caption("Sol", 20));
            _sunNote = Ui.Note("");
            left.Children.Add(_sunNote);
            _locate = new Button { Style = (Style)Application.Current.FindResource("Quiet"), Content = "Buscar localização pela internet", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 2, 0, 0) };
            _locate.Click += async (_, _) =>
            {
                _locate.IsEnabled = false;
                _locate.Content = "Buscando…";
                var err = await Ctl.LocateByInternet();
                _locate.IsEnabled = true;
                _locate.Content = "Buscar localização pela internet";
                if (err != null) _sunNote.Text = err;
            };
            Help.Set(_locate, new HelpInfo("Buscar localização", "Estima a sua cidade pelo endereço de internet (IP), num serviço externo (ipinfo.io). Só busca quando você clica. Com VPN pode apontar outra cidade.", null));
            left.Children.Add(_locate);

            leftGrid.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 1, 0) });
            Grid.SetRow(HelpHost, 1);
            leftGrid.Children.Add(HelpHost);
            Children.Add(leftGrid);

            // ---- center pane: toolbar + ruler + tracks
            var center = new DockPanel { Margin = new Thickness(16, 12, 12, 0) };
            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            var add = new Button { Content = "+ Ajuste", Style = (Style)Application.Current.FindResource("Primary"), Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
            add.Click += (_, _) => LayerPicker.Show(add, AddTrack, Hover, k => LayerSetup.Blocked(k, Profile.Tracks.Select(t => t.Layer)));
            Help.Set(add, new HelpInfo("Adicionar ajuste à agenda", "Adiciona um ajuste que vale sempre (independente do preset local). Começa com Opacidade 0: desenhe na automação quando e quanto ele age.", null));
            var insert = new Button { Content = "Copiar de um preset", Margin = new Thickness(6, 0, 0, 0) };
            insert.Click += (_, _) => InsertPreset(insert);
            Help.Set(insert, new HelpInfo("Copiar de um preset", "Copia os ajustes de um preset para a agenda (começam com Opacidade 0).", null));
            var fit = new Button { Content = "24h" };
            fit.Click += (_, _) => _view.Fit();
            Help.Set(fit, new HelpInfo("Ver o dia inteiro", "Volta o zoom para 24 horas. Ctrl+roda dá zoom; Shift+roda anda no tempo.", null));
            tools.Children.Add(fit);
            tools.Children.Add(insert);
            tools.Children.Add(add);
            DockPanel.SetDock(tools, Dock.Right);
            toolbar.Children.Add(tools);
            _title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(_title);
            titleRow.Children.Add(_profileDays);
            toolbar.Children.Add(titleRow);
            DockPanel.SetDock(toolbar, Dock.Top);
            center.Children.Add(toolbar);


            var rulerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            rulerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderW) });
            rulerRow.ColumnDefinitions.Add(new ColumnDefinition());
            _rulerHint = Ui.Note("Clique na régua para ver um horário");
            _rulerHint.VerticalAlignment = VerticalAlignment.Bottom;
            rulerRow.Children.Add(_rulerHint);
            _live = new Button { Style = (Style)Application.Current.FindResource("Quiet"), Content = "↺ voltar ao vivo", FontSize = 11, Padding = new Thickness(6, 0, 6, 0), MinHeight = 20,
                Foreground = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-6, 0, 0, 0), Visibility = Visibility.Collapsed };
            _live.Click += (_, _) => Ctl.Simulate(null);
            Help.Set(_live, new HelpInfo("Voltar ao vivo", "A tela está mostrando o horário escolhido na régua. Volta ao vivo (também com duplo clique na régua, Esc ou saindo da Agenda).", null));
            rulerRow.Children.Add(_live);
            _ruler = new Ruler(_view);
            Grid.SetColumn(_ruler, 1);
            Help.Set(_ruler, new HelpInfo("Régua do dia", "Clique ou arraste para ver um horário: a tela mostra como ficará naquela hora e fica parada ali. Ao soltar, o brilho físico do monitor também acompanha. Duplo clique volta ao vivo.", null));
            rulerRow.Children.Add(_ruler);
            DockPanel.SetDock(rulerRow, Dock.Top);
            center.Children.Add(rulerRow);

            _scroll = new ScrollViewer { Content = _tracks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            center.Children.Add(_scroll);
            Grid.SetColumn(center, 1);
            Children.Add(center);

            var right = new Border { BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1, 0, 0, 0), Child = RightHost };
            Grid.SetColumn(right, 2);
            Children.Add(right);
        }

        DayProfile Profile => Ctl.EditingProfile;

        public void ClearSelection()
        {
            if (_selectedId == null) return;
            _selectedId = null;
            BuildTracks();
        }

        // ------------------------------------------------------------------ build

        public void Rebuild()
        {
            _suppress = true;
            var s = Ctl.Settings;
            _switch.IsChecked = s.ScheduleEnabled;
            _switch.Content = !s.ScheduleEnabled ? "desligada" : s.SchedulePausedUntil is DateTime p && p > DateTime.Now ? $"pausada até {p:HH:mm}"
                            : Ctl.PausedByApp != null ? $"pausada ({Ctl.PausedByApp})" : "ligada";
            _suppress = false;
            _title.Text = Profile.Name;
            var loc = s.Location;
            var sun = loc != null ? Sun.Times(DateTime.Today, loc.Lat, loc.Lon) : null;
            _locate.Visibility = loc == null ? Visibility.Visible : Visibility.Collapsed;
            _sunNote.Text = loc == null ? "Sem localização: os pontos não podem ser presos ao nascer e ao pôr do sol."
                          : sun == null ? $"{loc.Name}: sem nascer ou pôr do sol hoje."
                          : $"{loc.Name} hoje: nasce {ArrangementView.Fmt(sun.Value.rise)}, se põe {ArrangementView.Fmt(sun.Value.set)}. Clique direito num ponto para prendê-lo ao sol.";
            BuildProfiles();
            _profileDays.Content = ProfileDays(Profile, large: true);
            var free = Enumerable.Range(0, 7).Where(d => Automation.DayOwner(s, d) == null).ToList();
            _freeDays.Text = free.Count == 0 ? "" : $"Sem perfil: {string.Join(", ", free.Select(d => Automation.DayNames[d].Substring(0, 3).ToLowerInvariant()))} (sem agenda nesses dias).";
            _freeDays.Foreground = Ui.Res("Dim");
            _freeDays.Visibility = free.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            BuildTracks();
        }

        void BuildProfiles()
        {
            _profiles.Children.Clear();
            var s = Ctl.Settings;
            var acting = Automation.ProfileFor(s, DateTime.Now, Ctl.AppProfile);
            foreach (var p in s.Profiles)
            {
                var c = (Color)ColorConverter.ConvertFromString(p.Color);
                bool sel = p == Profile;
                bool active = p == acting;
                var row = new DockPanel();
                var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                dot.Margin = new Thickness(0, 5, 10, 0);
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                var info = new StackPanel();
                var nameLine = new DockPanel();
                if (active)
                {
                    // amber = live: this profile is acting right now
                    var now = new TextBlock { Text = ActingWhy(p), FontSize = 11, Foreground = Ui.Res("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                    DockPanel.SetDock(now, Dock.Right);
                    nameLine.Children.Add(now);
                }
                nameLine.Children.Add(new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(nameLine);
                info.Children.Add(new TextBlock { Text = Summary(p), FontSize = 11, Foreground = Ui.Res("Dim") });
                var days = ProfileDays(p, large: false);
                days.Margin = new Thickness(-2, 5, 0, 1);
                info.Children.Add(days);
                row.Children.Add(info);
                var b = new Button
                {
                    Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(-12, 0, -12, 0),
                    Background = sel ? Ui.Res("AccentDim") : Brushes.Transparent, BorderBrush = Brushes.Transparent,
                };
                b.Template = RowTemplate(sel);
                var prof = p;
                b.Click += (_, _) => { Ctl.EditingProfile = prof; _selectedId = null; TrackSelected?.Invoke(null, false); Rebuild(); };
                b.MouseRightButtonUp += (_, e) => { ProfileMenu(prof, b); e.Handled = true; };
                Help.Set(b, new HelpInfo($"Perfil “{p.Name}”", "Clique para editar a lista de ajustes deste perfil. As letras são os dias em que ele vale. Clique direito: usar hoje, renomear, duplicar, trocar cor ou excluir.",
                    (active ? ActingWhy(p) + " · " : "") + "vale em: " + Automation.DaysText(Automation.DaysOf(s, p))));
                _profiles.Children.Add(b);
            }
        }

        /// <summary>Why a profile is acting now: by weekday, forced until midnight, or a program rule.</summary>
        static string ActingWhy(DayProfile p)
        {
            var s = Ctl.Settings;
            if (Ctl.AppProfile == p.Id) return "agora, por regra";
            if (Automation.IsForced(s, DateTime.Now) && s.ForcedProfile == p.Id) return "agora, até 00:00";
            return "agora";
        }

        /// <summary>
        /// S T Q Q S S D toggles of a profile: filled in the profile color = the profile runs that day.
        /// A day belongs to one profile only, so turning it on here takes it from the other profile.
        /// </summary>
        FrameworkElement ProfileDays(DayProfile p, bool large)
        {
            var s = Ctl.Settings;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var c = (Color)ColorConverter.ConvertFromString(p.Color);
            int today = ScheduleBlock.DayIndex(DateTime.Now.DayOfWeek);
            double w = large ? 22 : 18, h = large ? 22 : 18;
            for (int d = 0; d < 7; d++)
            {
                int day = d;
                var owner = Automation.DayOwner(s, d);
                bool on = owner == p;
                var cell = new StackPanel { Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                cell.Children.Add(new Border
                {
                    Width = w, Height = h, CornerRadius = new CornerRadius(3),
                    Background = on ? new SolidColorBrush(Color.FromArgb(0xD0, c.R, c.G, c.B)) : Brushes.Transparent,
                    BorderBrush = on ? Brushes.Transparent : Ui.Res("Line"), BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = Automation.DayLetters[d], FontSize = large ? 11 : 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = on ? (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 150 ? Ui.Res("Bg") : Brushes.White) : Ui.Res("Dim"),
                        FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    },
                });
                // today: a short amber tick under the letter (amber = now)
                cell.Children.Add(new Border { Height = 2, Width = w - 8, Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(1), Background = d == today ? Ui.Res("Accent") : Brushes.Transparent });
                cell.ToolTip = Automation.DayNames[d] + (d == today ? " (hoje)" : "") + ": " +
                    (on ? $"usa “{p.Name}”. Clique para deixar sem perfil." : owner != null ? $"usa “{owner.Name}”. Clique para passar para “{p.Name}”." : $"sem perfil. Clique para usar “{p.Name}”.");
                // Preview + Handled: inside the profile row (a Button) a click on a day must not also select the row
                cell.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    Ctl.SetDayOwner(day, on ? null : p);
                    Rebuild();
                };
                sp.Children.Add(cell);
            }
            Help.Set(sp, () => new HelpInfo($"Dias de “{p.Name}”", "Letra preenchida = o perfil vale nesse dia. Clique para ligar ou desligar. Cada dia pertence a um perfil só: ligar aqui tira o dia do outro perfil.",
                "vale em: " + Automation.DaysText(Automation.DaysOf(Ctl.Settings, p))));
            return sp;
        }

        /// <summary>Flat list row: neutral fill when selected, with the same 2px light bar as the other lists.</summary>
        static ControlTemplate RowTemplate(bool selected)
        {
            var t = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, selected ? Ui.Res("Accent") : Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(cp);
            t.VisualTree = border;
            if (!selected)
            {
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty, Ui.Res("Hover"), "b"));
                t.Triggers.Add(hover);
            }
            var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(Border.BackgroundProperty, Ui.Res("Raised"), "b"));
            t.Triggers.Add(press);
            return t;
        }

        static string Summary(DayProfile p)
        {
            int n = p.Tracks.Count, a = p.Linked.Sum(x => x.Lanes.Count);
            if (n == 0 && a == 0) return "vazio";
            var parts = new List<string>();
            if (n > 0) parts.Add(n == 1 ? "1 ajuste" : $"{n} ajustes");
            if (a > 0) parts.Add(a == 1 ? "1 automação" : $"{a} automações");
            return string.Join(" · ", parts);
        }

        void NewProfile(DayProfile? from)
        {
            var name = InputDialog.Ask(Window.GetWindow(this)!, "Novo perfil", "Nome do perfil:", from == null ? "Fim de semana" : from.Name + " cópia");
            if (string.IsNullOrWhiteSpace(name)) return;
            var color = Automation.NextProfileColor(Ctl.Settings.Profiles);
            var p = new DayProfile { Name = name.Trim(), Color = color };
            if (from != null)
            {
                p.Tracks = System.Text.Json.JsonSerializer.Deserialize<List<AgendaTrack>>(System.Text.Json.JsonSerializer.Serialize(from.Tracks, Json.Opts), Json.Opts) ?? new();
                foreach (var t in p.Tracks) { t.Id = Guid.NewGuid(); t.Layer.Id = Guid.NewGuid(); }
                p.Linked = System.Text.Json.JsonSerializer.Deserialize<List<LinkedAutomation>>(System.Text.Json.JsonSerializer.Serialize(from.Linked, Json.Opts), Json.Opts) ?? new();
            }
            Ctl.Settings.Profiles.Add(p);
            Ctl.EditingProfile = p;
            Ctl.ScheduleSave();
            Rebuild();
        }

        void ProfileMenu(DayProfile p, FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target };
            void Item(string h, Action a, bool enabled = true) { var mi = new MenuItem { Header = h, IsEnabled = enabled }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
            var forcedNow = Automation.IsForced(Ctl.Settings, DateTime.Now) && Ctl.Settings.ForcedProfile == p.Id;
            Item(forcedNow ? "Voltar ao automático (pelo dia)" : "Usar este perfil hoje (até meia-noite)", () => { Ctl.ForceProfile(forcedNow ? null : p); Rebuild(); });
            m.Items.Add(new Separator());
            Item("Renomear…", () =>
            {
                var n = InputDialog.Ask(Window.GetWindow(this)!, "Renomear perfil", "Nome:", p.Name);
                if (!string.IsNullOrWhiteSpace(n)) { p.Name = n.Trim(); Ctl.ScheduleSave(); Rebuild(); }
            });
            Item("Duplicar", () => NewProfile(p));
            var colorMenu = new MenuItem { Header = "Cor" };
            for (int ci = 0; ci < Automation.ProfileColors.Length; ci++)
            {
                var hex = Automation.ProfileColors[ci];
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Ui.Hex(hex), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Text = ci < Automation.ProfileColorNames.Length ? Automation.ProfileColorNames[ci] : hex, VerticalAlignment = VerticalAlignment.Center });
                var mi = new MenuItem { Header = head, IsChecked = p.Color.Equals(hex, StringComparison.OrdinalIgnoreCase) };
                mi.Click += (_, _) => { p.Color = hex; Ctl.ScheduleSave(); Rebuild(); };
                colorMenu.Items.Add(mi);
            }
            m.Items.Add(colorMenu);
            Item("Excluir", () =>
            {
                var s = Ctl.Settings;
                s.Profiles.Remove(p);
                for (int d = 0; d < 7; d++) if (s.Week[d] == p.Id) s.Week[d] = Guid.Empty;
                if (s.ForcedProfile == p.Id) { s.ForcedProfile = null; s.ForcedUntil = null; }
                foreach (var r in s.AppRules) if (r.Profile == p.Id) r.Profile = null;
                if (Ctl.EditingProfile == p) Ctl.EditingProfile = Automation.ProfileFor(s, DateTime.Now) ?? s.Profiles[0];
                Ctl.ScheduleApply(); Rebuild();
            }, Ctl.Settings.Profiles.Count > 1);
            m.IsOpen = true;
        }

        /// <summary>Finds the preset that owns a layer id.</summary>
        static (Preset? preset, Layer? layer) Owner(Guid layerId)
        {
            foreach (var p in Ctl.Settings.Presets)
                foreach (var l in p.Layers)
                    if (l.Id == layerId) return (p, l);
            return (null, null);
        }

        void BuildTracks()
        {
            _tracks.Children.Clear();
            _laneRows.Clear();
            var local = Ctl.Stack.Local ?? Ctl.Manual;

            // 1) agenda's own tracks
            _tracks.Children.Add(GroupHeader("Ajustes da agenda", "valem sempre, com qualquer preset local"));
            if (Profile.Tracks.Count == 0)
                _tracks.Children.Add(Hint("Nenhum ainda. “+ Ajuste” adiciona um ajuste que segue o horário."));
            foreach (var tr in Profile.Tracks) _tracks.Children.Add(BuildTrack(tr, linked: false, dim: false, title: tr.Layer.DisplayName));

            // 2) automation of the local preset's layers, 3) other presets
            var linked = Profile.Linked.Select(la => (la, owner: Owner(la.LayerId))).Where(x => x.owner.layer != null).ToList();
            // shown only when something exists: an empty "preset" section read as if the agenda depended on the preset
            var mine = linked.Where(x => x.owner.preset == local).ToList();
            if (mine.Count > 0)
            {
                _tracks.Children.Add(GroupHeader($"Automações do preset {local.Name}", "só valem com ele na tela"));
                foreach (var (la, owner) in mine)
                    _tracks.Children.Add(BuildTrack(Wrap(la, owner.layer!), linked: true, dim: false, title: owner.layer!.DisplayName));
            }

            var others = linked.Where(x => x.owner.preset != local).ToList();
            if (others.Count > 0)
            {
                _tracks.Children.Add(GroupHeader("Automações de outros presets", "só valem quando o preset estiver na tela"));
                foreach (var (la, owner) in others)
                    _tracks.Children.Add(BuildTrack(Wrap(la, owner.layer!), linked: true, dim: true, title: $"{owner.preset!.Name} › {owner.layer!.DisplayName}"));
            }
        }

        static AgendaTrack Wrap(LinkedAutomation la, Layer layer) => new() { Id = layer.Id, Layer = layer, Lanes = la.Lanes };

        static FrameworkElement GroupHeader(string title, string sub)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 6) };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12 });
            sp.Children.Add(new TextBlock { Text = "  " + sub, FontSize = 12, Foreground = Ui.Res("Dim") });
            return sp;
        }

        static FrameworkElement Hint(string text) => new TextBlock { Text = text, Foreground = Ui.Res("Dim"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };

        FrameworkElement BuildTrack(AgendaTrack tr, bool linked, bool dim, string title)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4), Opacity = dim ? 0.55 : 1 };
            var cat = Categories.Of(tr.Layer.Kind);
            var catBrush = Ui.Hex(cat.Color);
            bool sel = _selectedId == tr.Id;

            var head = new Grid { Height = 30, Background = sel ? Ui.Res("AccentDim") : Ui.Res("Panel"), Cursor = Cursors.Hand };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderW) });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var hdr = new DockPanel { LastChildFill = true };
            var bar = new Border { Width = 2, Background = catBrush };
            DockPanel.SetDock(bar, Dock.Left);
            hdr.Children.Add(bar);
            if (!linked)
            {
                var eye = new ToggleButton { Style = (Style)Application.Current.FindResource("Eye"), IsChecked = tr.Layer.Enabled, Margin = new Thickness(4, 0, 0, 0) };
                eye.Checked += (_, _) => { tr.Layer.Enabled = true; Ctl.ScheduleApply(); };
                eye.Unchecked += (_, _) => { tr.Layer.Enabled = false; Ctl.ScheduleApply(); };
                Help.Set(eye, new HelpInfo("Ligar/desligar ajuste", "Desliga o ajuste inteiro sem perder a automação.", null));
                DockPanel.SetDock(eye, Dock.Left);
                hdr.Children.Add(eye);
            }
            else
            {
                var link = new TextBlock { Text = "", FontFamily = Ui.Icons, Foreground = Ui.Res("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), ToolTip = "Automação de um ajuste do preset" };
                DockPanel.SetDock(link, Dock.Left);
                hdr.Children.Add(link);
            }
            var collapse = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = "", Width = 22, Height = 22, FontSize = 13 };
            DockPanel.SetDock(collapse, Dock.Right);
            hdr.Children.Add(collapse);
            hdr.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0), FontWeight = FontWeights.SemiBold });
            head.Children.Add(hdr);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            foreach (var m in Ctl.Engine.Monitors)
            {
                bool on = tr.Layer.AppliesTo(m.DeviceName);
                tools.Children.Add(new TextBlock
                {
                    Text = m.Number, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = on ? Ui.Res("Text") : Ui.Res("Faint"), ToolTip = m.Short + (on ? ": age aqui" : ": não age aqui"),
                });
            }
            var addLane = new Button { Style = (Style)Application.Current.FindResource("Quiet"), Content = "+ automação", FontSize = 11, Padding = new Thickness(6, 0, 6, 0), MinHeight = 22, Margin = new Thickness(8, 0, 0, 0) };
            addLane.Click += (_, _) => AddLaneMenu(tr, linked, addLane);
            Help.Set(addLane, new HelpInfo("Automatizar outro valor", "Cria uma automação para mais um valor deste ajuste.", null));
            var del = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = "", Width = 24, Height = 22 };
            del.Click += (_, _) =>
            {
                if (linked) Profile.Linked.RemoveAll(l => l.LayerId == tr.Layer.Id);
                else Profile.Tracks.Remove(tr);
                if (_selectedId == tr.Id) { _selectedId = null; TrackSelected?.Invoke(null, false); }
                Ctl.ScheduleApply();
                BuildTracks();
            };
            Help.Set(del, new HelpInfo(linked ? "Remover automação" : "Excluir ajuste",
                linked ? "Apaga a automação deste ajuste neste perfil. O ajuste continua no preset, com valor fixo (Ctrl+Z desfaz)." : "Remove este ajuste da agenda deste perfil (Ctrl+Z desfaz).", null));
            tools.Children.Add(addLane);
            tools.Children.Add(del);
            Grid.SetColumn(tools, 1);
            head.Children.Add(tools);

            var lanes = new StackPanel();
            collapse.Click += (_, _) =>
            {
                lanes.Visibility = lanes.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                collapse.Content = lanes.Visibility == Visibility.Visible ? "" : "";
            };
            head.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && (FindParent<ButtonBase>(d) != null)) return;
                _selectedId = tr.Id;
                TrackSelected?.Invoke(tr, linked);
                BuildTracks();
            };
            Help.Set(head, () => new HelpInfo(title + " · " + cat.Name,
                (HelpTexts.Layer.TryGetValue(tr.Layer.Kind, out var t) ? t : "") + (linked ? " Automação de um ajuste do preset (vinculada)." : " Ajuste da agenda.") + " Clique para ver as propriedades à direita.",
                HelpTexts.EngineFacts(LayerDefs.Get(tr.Layer.Kind).Engine, Ctl.Settings.HideOverlayFromCapture)));
            panel.Children.Add(head);

            foreach (var lane in tr.Lanes.ToList())
            {
                lanes.Children.Add(BuildLane(tr, lane, catBrush, linked));
            }
            panel.Children.Add(lanes);
            return panel;
        }

        static T? FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null) { if (d is T t) return t; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
            return null;
        }

        FrameworkElement BuildLane(AgendaTrack tr, Lane lane, Brush catBrush, bool linked)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 0), Tag = lane.Id };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderW) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var hdr = new Grid { Background = Ui.Res("Bg") };
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition());
            hdr.Children.Add(new Border { Background = catBrush, Opacity = 0.45 });
            var info = new StackPanel { Margin = new Thickness(24, 4, 4, 4), VerticalAlignment = VerticalAlignment.Center };
            var top = new DockPanel();
            var rm = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = "", Width = 20, Height = 18, FontSize = 11 };
            rm.Click += (_, _) =>
            {
                tr.Lanes.Remove(lane);
                if (linked && tr.Lanes.Count == 0) Profile.Linked.RemoveAll(x => x.LayerId == tr.Layer.Id);
                Ctl.ScheduleApply();
                BuildTracks();
            };
            Help.Set(rm, new HelpInfo("Remover esta automação", "O valor volta a ser fixo, o do ajuste.", null));
            DockPanel.SetDock(rm, Dock.Right);
            top.Children.Add(rm);
            var chips = DayChips(tr, lane);
            DockPanel.SetDock(chips, Dock.Right);
            top.Children.Add(chips);
            top.Children.Add(new TextBlock { Text = Automation.Label(tr.Layer.Kind, lane.Param), Foreground = Ui.Res("Dim"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            info.Children.Add(top);
            var bottom = new DockPanel();
            var back = new Button { Style = (Style)Application.Current.FindResource("Quiet"), Content = "↺ voltar", FontSize = 11, Padding = new Thickness(5, 0, 5, 0), MinHeight = 20, Foreground = Ui.Res("Text"), Visibility = lane.Overridden ? Visibility.Visible : Visibility.Collapsed };
            back.Click += (_, _) => { lane.Overridden = false; Ctl.ScheduleApply(); RefreshValues(); if (_selectedId == tr.Id) TrackSelected?.Invoke(tr, linked); };
            Help.Set(back, new HelpInfo("Voltar à automação", "Você mexeu neste valor à mão, então a automação ficou pausada (como no Ableton). Clique para ela voltar a mandar.", null));
            DockPanel.SetDock(back, Dock.Right);
            bottom.Children.Add(back);
            var value = new TextBlock { FontSize = 15 };
            value.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            bottom.Children.Add(value);
            info.Children.Add(bottom);
            Grid.SetColumn(info, 1);
            hdr.Children.Add(info);
            hdr.MouseRightButtonUp += (_, e) => { LaneMenu(tr, lane, linked, hdr); e.Handled = true; };
            row.Children.Add(hdr);

            var ctl = new LaneControl(_view, tr, lane);
            ctl.Changed += _ =>
            {
                Ctl.ScheduleApply();
                RefreshValues();
            };
            ctl.PointSelected += (c, k) =>
            {
                foreach (var r in _laneRows) if (r.ctl != c) r.ctl.Select(null);
                PointSelected?.Invoke(c, k);
            };
            Help.Set(ctl, () => ctl.HelpAt(Mouse.GetPosition(ctl)));
            Grid.SetColumn(ctl, 1);
            row.Children.Add(ctl);
            _laneRows.Add((lane, tr, value, ctl, back));
            RefreshValue(lane, tr, value, back);
            return row;
        }

        /// <summary>Weekdays no lane of this parameter uses yet.</summary>
        static int FreeDays(AgendaTrack tr, string param, Lane? except = null)
            => 0x7F & ~tr.Lanes.Where(l => l.Param == param && l != except).Aggregate(0, (m, l) => m | l.Days);

        void AddLaneMenu(AgendaTrack tr, bool linked, FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target };
            foreach (var (key, label) in Automation.Automatable(tr.Layer.Kind))
            {
                bool exists = tr.Lanes.Any(l => l.Param == key);
                int free = FreeDays(tr, key);
                var mi = new MenuItem
                {
                    Header = !exists ? label : free == 0 ? $"{label}  (todos os dias já têm automação)" : $"{label}  + outra automação ({Automation.DaysText(free)})",
                    IsEnabled = free != 0,
                };
                mi.Click += (_, _) =>
                {
                    if (linked && !exists) Profile.AddLinked(tr.Layer.Id, key);
                    else tr.Lanes.Add(new Lane { Param = key, Days = free });
                    Ctl.ScheduleSave();
                    BuildTracks();
                };
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        }

        /// <summary>S T Q Q S S D toggles: which weekdays this lane acts on. Red = another lane of the same parameter also claims that day.</summary>
        FrameworkElement DayChips(AgendaTrack tr, Lane lane)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
            var others = Automation.Conflicts(tr.Lanes, lane).ToList();
            int profileDays = Automation.DaysOf(Ctl.Settings, Profile);
            for (int d = 0; d < 7; d++)
            {
                int bit = 1 << d;
                bool inProfile = (profileDays & bit) != 0;
                bool on = (lane.Days & bit) != 0;
                bool clash = on && inProfile && others.Any(o => (o.Days & bit) != 0);
                var chip = new Border
                {
                    Width = 14, Height = 17, Margin = new Thickness(1, 0, 0, 0), Cursor = inProfile ? Cursors.Hand : Cursors.Arrow,
                    Background = Brushes.Transparent, Opacity = inProfile ? 1 : 0.35,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    BorderBrush = clash ? Ui.Res("Danger") : on && inProfile ? Ui.Res("Dim") : Brushes.Transparent,
                    Child = new TextBlock
                    {
                        Text = Automation.DayLetters[d], FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = clash ? Ui.Res("Danger") : on ? Ui.Res("Text") : Ui.Res("Faint"),
                        FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    },
                    ToolTip = Automation.DayNames[d] + (!inProfile ? $": não é dia do perfil “{Profile.Name}” (só vale se você usar o perfil nesse dia)"
                        : clash ? ": outra automação deste valor também age neste dia (vale a de baixo)" : on ? ": ativa" : ": desligada"),
                };
                chip.MouseLeftButtonDown += (_, e) =>
                {
                    if (!inProfile) { e.Handled = true; return; }
                    lane.Days ^= bit;
                    Ctl.ScheduleApply();
                    BuildTracks();
                    e.Handled = true;
                };
                sp.Children.Add(chip);
            }
            Help.Set(sp, () => new HelpInfo("Dias desta automação", "Clique numa letra para ligar ou desligar o dia. Para horários diferentes em outros dias, clique direito na automação e use “Duplicar para outros dias”.",
                Automation.DaysText(lane.Days) + (others.Any(o => (o.Days & lane.Days) != 0) ? " · conflito com outra automação (vale a de baixo)" : "")));
            return sp;
        }

        void LaneMenu(AgendaTrack tr, Lane lane, bool linked, FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target };
            int free = FreeDays(tr, lane.Param, lane) & ~lane.Days;
            var dup = new MenuItem
            {
                Header = free == 0 ? "Duplicar para outros dias (nenhum dia livre)" : $"Duplicar para outros dias ({Automation.DaysText(free)})",
                IsEnabled = free != 0,
            };
            dup.Click += (_, _) =>
            {
                var copy = System.Text.Json.JsonSerializer.Deserialize<Lane>(System.Text.Json.JsonSerializer.Serialize(lane, Json.Opts), Json.Opts)!;
                copy.Id = Guid.NewGuid();
                copy.Days = free;
                copy.Overridden = false;
                tr.Lanes.Insert(tr.Lanes.IndexOf(lane) + 1, copy);
                Ctl.ScheduleApply();
                BuildTracks();
            };
            m.Items.Add(dup);
            m.Items.Add(new Separator());
            void Days(string h, int mask)
            {
                var mi = new MenuItem { Header = h, IsChecked = lane.Days == mask };
                mi.Click += (_, _) => { lane.Days = mask; Ctl.ScheduleApply(); BuildTracks(); };
                m.Items.Add(mi);
            }
            Days("Todos os dias", 0x7F);
            Days("Segunda a sexta", 0x1F);
            Days("Fim de semana", 0x60);
            m.IsOpen = true;
        }

        // ------------------------------------------------------------------ actions

        static AgendaTrack NewTrack(Layer l)
        {
            // Starts at opacity 0 so nothing changes on screen until you draw the automation.
            var op = new Lane { Param = "opacity", Points = { new Keyframe { T = 12 * 60, V = 0 } } };
            var tr = new AgendaTrack { Layer = l, Lanes = { op } };
            var main = Automation.Automatable(l.Kind).Skip(1).FirstOrDefault();
            if (main.key != null) tr.Lanes.Add(new Lane { Param = main.key });
            return tr;
        }

        void AddTrack(LayerKind kind)
        {
            var l = Layer.Create(kind);
            var tr = NewTrack(l);
            LayerSetup.Prepare(l, Profile.Tracks.Select(t => t.Layer), () => { if (_selectedId == tr.Id) TrackSelected?.Invoke(tr, false); });
            Profile.Tracks.Add(tr);
            _selectedId = tr.Id;
            Ctl.ScheduleApply();
            BuildTracks();
            TrackSelected?.Invoke(tr, false);
        }

        void InsertPreset(FrameworkElement target)
        {
            var m = new ContextMenu { PlacementTarget = target };
            foreach (var p in Ctl.Settings.Presets)
            {
                var pp = p;
                var mi = new MenuItem { Header = $"{p.Name}  ({p.Layers.Count} ajustes)", IsEnabled = p.Layers.Count > 0 };
                mi.Click += (_, _) =>
                {
                    foreach (var l in pp.Layers)
                    {
                        var c = l.Clone();
                        if (LayerSetup.Fit(c, Profile.Tracks.Select(t => t.Layer))) Profile.Tracks.Add(NewTrack(c));
                    }
                    Ctl.ScheduleApply();
                    BuildTracks();
                };
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        }

        /// <summary>Selects and scrolls to the lane automating a preset layer's parameter.</summary>
        public void Reveal(Guid layerId, string param)
        {
            _selectedId = layerId;
            BuildTracks();
            var row = _laneRows.FirstOrDefault(r => r.tr.Layer.Id == layerId && r.lane.Param == param);
            if (row.ctl == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                row.ctl.BringIntoView();
                row.ctl.Focus();
                TrackSelected?.Invoke(row.tr, true);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ------------------------------------------------------------------ live values

        void RefreshValue(Lane lane, AgendaTrack tr, TextBlock value, Button back)
        {
            var when = Ctl.Simulated ?? DateTime.Now;
            var r = Automation.Range(tr.Layer.Kind, lane.Param);
            double? v = lane.Overridden || r == null ? null : Automation.Eval(lane, when.Hour * 60 + when.Minute + when.Second / 60.0, when.Date, Ctl.Settings.Location, r.Value.stepped);
            v ??= lane.Param == "opacity" ? tr.Layer.Opacity : tr.Layer.Get(lane.Param);
            value.Text = Automation.Format(tr.Layer.Kind, lane.Param, v.Value) + (lane.Overridden ? "  à mão" : lane.Points.Count == 0 ? "  fixo" : "");
            value.Foreground = lane.Overridden ? Ui.Res("Dim") : Ui.Res("Text");
            back.Visibility = lane.Overridden ? Visibility.Visible : Visibility.Collapsed;
        }

        public void RefreshValues()
        {
            bool sim = Ctl.Simulated != null;
            _live.Visibility = sim ? Visibility.Visible : Visibility.Collapsed;
            _rulerHint.Visibility = sim ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (lane, tr, value, ctl, back) in _laneRows)
            {
                RefreshValue(lane, tr, value, back);
                ctl.InvalidateVisual();
            }
            _ruler.InvalidateVisual();
        }

        /// <summary>
        /// With the ruler pinned at a time, moving an automated value writes the automation there
        /// (a point at that time, 15-minute grid) instead of pausing it. False if the param isn't automated.
        /// </summary>
        public static bool WriteAtPinned(AgendaTrack tr, string param, double value)
        {
            if (App.Ctl.Simulated is not DateTime when || App.Ctl.Scrubbing) return false;
            var lane = Automation.ActiveLanes(tr.Lanes, when.DayOfWeek).FirstOrDefault(l => l.Param == param && l.Points.Count > 0);
            if (lane == null) return false;
            double minute = Math.Round((when.Hour * 60 + when.Minute) / 15.0) * 15 % 1440;
            var loc = App.Ctl.Settings.Location;
            var near = lane.Points.FirstOrDefault(k => Math.Abs(Automation.TimeOf(k, when.Date, loc) - minute) < 7.5);
            if (near != null) near.V = value;
            else lane.Points.Add(new Keyframe { T = minute, V = value });
            lane.Overridden = false;
            return true;
        }

        /// <summary>Mark the lane for this param as overridden (you moved its value by hand).</summary>
        public static bool Override(AgendaTrack tr, string param)
        {
            var lane = Automation.ActiveLanes(tr.Lanes, (App.Ctl.Simulated ?? DateTime.Now).DayOfWeek).FirstOrDefault(l => l.Param == param && l.Points.Count > 0);
            if (lane == null || lane.Overridden) return false;
            lane.Overridden = true;
            return true;
        }
    }

    /// <summary>Compact 24h strip in the header: where today's automation acts, with the amber now line. Click opens the Agenda tab.</summary>
    public sealed class MiniAgenda : FrameworkElement
    {
        public event Action? Clicked;

        public MiniAgenda()
        {
            Width = 180; Height = 12; Cursor = Cursors.Hand;
            ToolTip = "Agenda de hoje (clique para abrir)";
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRoundedRectangle(Ui.Res("Raised"), null, new Rect(0, 0, w, h), 2, 2);
            var s = App.Ctl.Settings;
            var today = DateTime.Today;
            var prof = Automation.ProfileFor(s, today, App.Ctl.AppProfile);
            if (prof != null && s.ScheduleEnabled)
            {
                // where own tracks act (opacity > 1%), as a neutral light fill; color is reserved for "now"
                var fill = new SolidColorBrush(Color.FromArgb(0x70, 0xE6, 0xE6, 0xE8));
                for (int m = 0; m < 1440; m += 10)
                {
                    double act = 0;
                    foreach (var tr in prof.Tracks.Where(t => t.Layer.Enabled && t.Lanes.Any(l => l.OnDay(today.DayOfWeek))))
                    {
                        var op = Automation.ActiveLanes(tr.Lanes, today.DayOfWeek).FirstOrDefault(l => l.Param == "opacity");
                        double v = op != null ? Automation.Eval(op, m, today, s.Location, false) ?? tr.Layer.Opacity : tr.Layer.Opacity;
                        act = Math.Max(act, v / 100);
                    }
                    if (act > 0.01) dc.DrawRectangle(fill, null, new Rect(m / 1440.0 * w, h * (1 - act), w / 144 + 0.5, h * act));
                }
                // keyframes of the local preset's linked automation: ticks
                var local = App.Ctl.Stack.Local ?? App.Ctl.Manual;
                var tick = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)), 1);
                foreach (var l in local.Layers)
                    foreach (var lane in Automation.ActiveLanes(prof.LinkedFor(l.Id)?.Lanes ?? new List<Lane>(), today.DayOfWeek))
                        foreach (var k in lane.Points)
                        {
                            double x = Automation.TimeOf(k, today, s.Location) / 1440 * w;
                            dc.DrawLine(tick, new Point(x, 2), new Point(x, h - 2));
                        }
            }
            var now = App.Ctl.Simulated ?? DateTime.Now;
            double nx = (now.Hour * 60 + now.Minute) / 1440.0 * w;
            dc.DrawRectangle(App.Ctl.Simulated != null ? LaneDraw.Sim : LaneDraw.Red, null, new Rect(nx - 1, -1, 2, h + 2));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Clicked?.Invoke();
    }
}
