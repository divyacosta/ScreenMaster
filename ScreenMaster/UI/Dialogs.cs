using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenMaster.Core;
using ScreenMaster.Native;

namespace ScreenMaster
{
    static class DarkTitleBar
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(Window w)
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = 0x001D1B1B; // COLORREF (0x00BBGGRR) of #1B1B1D
                DwmSetWindowAttribute(h, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Win11)
            }
            catch { }
        }
    }

    static class DialogStyle
    {
        public static void Apply(Window w, Window? owner, string title, double width)
        {
            w.Title = title;
            w.Owner = owner;
            w.Width = width;
            w.SizeToContent = SizeToContent.Height;
            w.ResizeMode = ResizeMode.NoResize;
            w.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            w.Background = (Brush)Application.Current.FindResource("Bg");
            w.Foreground = (Brush)Application.Current.FindResource("Text");
            w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            w.FontSize = 13;
            w.ShowInTaskbar = false;
            w.SourceInitialized += (_, _) => DarkTitleBar.Apply(w);
        }

        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }

    public sealed class InputDialog : Window
    {
        readonly TextBox _box;

        InputDialog(Window owner, string title, string prompt, string value)
        {
            DialogStyle.Apply(this, owner, title, 380);
            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
            _box = new TextBox { Text = value };
            root.Children.Add(_box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary"), MinWidth = 70 };
            ok.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }

        public static string? Ask(Window owner, string title, string prompt, string value)
        {
            var d = new InputDialog(owner, title, prompt, value);
            return d.ShowDialog() == true ? d._box.Text : null;
        }
    }

    public sealed class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            DialogStyle.Apply(this, null, "Configurações", 520);
            var s = App.Ctl.Settings;
            var root = new StackPanel { Margin = new Thickness(20) };

            TextBlock Caption(string t) => new() { Text = t, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 14, 0, 8) };
            CheckBox Check(string text, bool value, Action<bool> set, string? tip = null)
            {
                var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 0, 10), ToolTip = tip };
                cb.Checked += (_, _) => set(true);
                cb.Unchecked += (_, _) => set(false);
                return cb;
            }
            TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = DialogStyle.Res("Dim"), FontSize = 11, Margin = new Thickness(26, -6, 0, 10) };

            root.Children.Add(new TextBlock { Text = "ScreenMaster", FontSize = 18, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock { Text = $"Versão {typeof(App).Assembly.GetName().Version?.ToString(3)}", Foreground = DialogStyle.Res("Dim") });

            root.Children.Add(Caption("Inicialização"));
            root.Children.Add(Check("Iniciar com o Windows", App.StartsWithWindows(), v => App.SetStartWithWindows(v)));
            root.Children.Add(Check("Abrir minimizado na bandeja", s.StartMinimized, v => { s.StartMinimized = v; App.Ctl.ScheduleSave(); }));
            root.Children.Add(Check("Fechar a janela deixa rodando na bandeja", s.CloseToTray, v => { s.CloseToTray = v; App.Ctl.ScheduleSave(); }));

            root.Children.Add(Caption("Sobreposição (granulação, cor sólida, degradê, vinheta, guias)"));
            root.Children.Add(Check("Esconder de prints e gravações", s.HideOverlayFromCapture, v =>
            {
                s.HideOverlayFromCapture = v; App.Ctl.ScheduleApply();
            }));
            root.Children.Add(Note("Com isso ligado, a sobreposição aparece só para você: Print Screen, OBS e Discord capturam a tela sem ela."));

            root.Children.Add(Caption("Modo escala (Ctrl+Alt+Shift+E · experimental)"));
            var aspect = new ComboBox { ItemsSource = Engines.ScaleMath.Aspects, SelectedIndex = Math.Clamp(s.ScaleAspect, 0, Engines.ScaleMath.Aspects.Length - 1), Margin = new Thickness(0, 0, 0, 8) };
            aspect.SelectionChanged += (_, _) => { s.ScaleAspect = aspect.SelectedIndex; App.Ctl.ScheduleSave(); };
            root.Children.Add(new TextBlock { Text = "Proporção", Foreground = DialogStyle.Res("Dim"), Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(aspect);
            var filter = new ComboBox { ItemsSource = Engines.ScaleMath.Filters, SelectedIndex = Math.Clamp(s.ScaleFilter, 0, Engines.ScaleMath.Filters.Length - 1), Margin = new Thickness(0, 0, 0, 8) };
            filter.SelectionChanged += (_, _) => { s.ScaleFilter = filter.SelectedIndex; App.Ctl.ScheduleSave(); };
            root.Children.Add(new TextBlock { Text = "Ampliação", Foreground = DialogStyle.Res("Dim"), Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(filter);
            var sharpLabel = new TextBlock { Foreground = DialogStyle.Res("Dim"), Margin = new Thickness(0, 0, 0, 4) };
            void SharpText() => sharpLabel.Text = $"Nitidez depois de ampliar (RCAS): {s.ScaleSharpen:0}%";
            SharpText();
            var sharp = new Slider { Minimum = 0, Maximum = 100, Value = s.ScaleSharpen, SmallChange = 5, LargeChange = 10, Margin = new Thickness(0, 0, 0, 8) };
            sharp.ValueChanged += (_, e) => { s.ScaleSharpen = Math.Round(e.NewValue); SharpText(); App.Ctl.ScheduleSave(); };
            root.Children.Add(sharpLabel);
            root.Children.Add(sharp);
            root.Children.Add(Check("Escala inteira (2×, 3×…, sem borrão; bom para pixel art; só com “Manter a da janela”)", s.ScaleInteger, v => { s.ScaleInteger = v; App.Ctl.ScheduleSave(); }));
            root.Children.Add(Note("Deixe o jogo em janela numa resolução menor e aperte o atalho com ele em primeiro plano. O mouse fica preso na janela e anda mais rápido na imagem ampliada. Alt+Tab solta o mouse."));

            root.Children.Add(Caption("Localização para nascer e pôr do sol"));
            var place = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var find = new Button { Content = "Buscar pela internet", Margin = new Thickness(10, 0, 0, 0) };
            var clear = new Button { Style = (Style)Application.Current.FindResource("Quiet"), Content = "Remover", Margin = new Thickness(4, 0, 0, 0) };
            var placeRow = new DockPanel();
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(find, Dock.Right);
            placeRow.Children.Add(clear);
            placeRow.Children.Add(find);
            placeRow.Children.Add(place);
            var sunNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = DialogStyle.Res("Dim"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
            var geoError = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = DialogStyle.Res("Danger"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            void ShowSun()
            {
                var l = s.Location;
                var t = l != null ? Sun.Times(DateTime.Today, l.Lat, l.Lon) : null;
                place.Text = l?.Name ?? "Nenhuma";
                place.Foreground = DialogStyle.Res(l != null ? "Text" : "Dim");
                clear.Visibility = l != null ? Visibility.Visible : Visibility.Collapsed;
                find.Content = l != null ? "Buscar de novo" : "Buscar pela internet";
                sunNote.Text = l == null
                    ? "Usa o seu endereço de internet (IP) para estimar a cidade, num serviço externo (ipinfo.io). Só busca quando você clica. Com VPN pode apontar outra cidade."
                    : t == null ? "Sem nascer/pôr do sol hoje nessa latitude."
                    : $"Hoje: nascer {UI.ArrangementView.Fmt(t.Value.rise)} · pôr {UI.ArrangementView.Fmt(t.Value.set)}. O cálculo do sol funciona sem internet.";
            }
            find.Click += async (_, _) =>
            {
                find.IsEnabled = false;
                find.Content = "Buscando…";
                geoError.Visibility = Visibility.Collapsed;
                var err = await App.Ctl.LocateByInternet();
                find.IsEnabled = true;
                if (err != null) { geoError.Text = err; geoError.Visibility = Visibility.Visible; }
                ShowSun();
            };
            clear.Click += (_, _) => { s.Location = null; App.Ctl.ScheduleApply(); ShowSun(); };
            root.Children.Add(placeRow);
            root.Children.Add(geoError);
            root.Children.Add(sunNote);
            ShowSun();

            root.Children.Add(Caption("Faixa de gama do Windows"));
            bool unlocked = Diagnostics.GammaRangeUnlocked();
            root.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = unlocked
                    ? "Liberada. Curvas, Níveis, Posterizar e Limiar podem usar a faixa completa."
                    : "Padrão do Windows. Se um ajuste de Tons mostrar “Windows recusou”, libere a faixa completa. " +
                      "Isso cria a chave GdiIcmGammaRange=256 no registro (pede permissão de administrador) e precisa reiniciar o PC.",
            });
            if (!unlocked)
            {
                var unlock = new Button { Content = "Liberar faixa completa…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
                unlock.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, "Vou pedir permissão de administrador para criar a chave de registro:\n\n" +
                                              @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM" + "\nGdiIcmGammaRange = 256\n\nDepois é preciso reiniciar o PC. Continuar?",
                            "ScreenMaster", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo("reg.exe",
                            "add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f")
                        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                        p?.WaitForExit();
                        MessageBox.Show(this, Diagnostics.GammaRangeUnlocked() ? "Pronto. Reinicie o PC para valer." : "Não foi possível criar a chave.", "ScreenMaster");
                    }
                    catch (Exception ex) { Log.Write("unlock gamma: " + ex.Message); }
                };
                root.Children.Add(unlock);
            }

            root.Children.Add(Caption("Dados"));
            var folder = new Button { Content = "Abrir pasta de presets e log", HorizontalAlignment = HorizontalAlignment.Left };
            folder.Click += (_, _) => { Paths.Ensure(); Process.Start(new ProcessStartInfo("explorer.exe", Paths.Dir) { UseShellExecute = true }); };
            root.Children.Add(folder);

            // Factory reset: destructive, so it's separated, red, and asks first.
            root.Children.Add(Caption("Restaurar"));
            root.Children.Add(UI.Ui.Note("Volta ao estado de quando o ScreenMaster foi instalado: presets de exemplo, agenda de exemplo, sem regras por programa e preferências padrão. A tela volta ao original."));
            var reset = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), BorderBrush = DialogStyle.Res("Danger") };
            reset.Content = new TextBlock { Text = "Restaurar configurações de fábrica…", Foreground = DialogStyle.Res("Danger") };
            reset.Click += (_, _) =>
            {
                if (MessageBox.Show(this,
                        "Restaurar configurações de fábrica?\n\nSeus presets, perfis da agenda, automações e regras por programa serão substituídos pelo conteúdo de exemplo. " +
                        "Logo depois, Ctrl+Z ainda traz de volta os presets, a agenda e as regras.\n\n“Iniciar com o Windows” não é alterado.",
                        "ScreenMaster", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                App.Ctl.FactoryReset();
                Close();
            };
            root.Children.Add(reset);

            var close = new Button { Content = "Fechar", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80, Margin = new Thickness(0, 20, 0, 0) };
            close.Click += (_, _) => Close();
            root.Children.Add(close);
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
