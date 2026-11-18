using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>
    /// "+ Ajuste" picker: categories collapsed by default (click the title to expand),
    /// colored bar on the left, and a search box that filters across all categories.
    /// </summary>
    public static class LayerPicker
    {
        sealed record Entry(string Label, LayerKind? Kind, string? Why = null, string? Alias = null);
        sealed record Section(string Name, string Sub, string Color, Entry[] Items);

        static Section[] Sections()
        {
            Section S(EngineKind e, params Entry[] items) { var c = Categories.Of(e); return new Section(c.Name, c.Subtitle, c.Color, items); }
            return new[]
            {
                S(EngineKind.Gamma,
                    new Entry("Brilho/Contraste", LayerKind.BrightnessContrast, Alias: "Brightness Contrast light"), new Entry("Níveis", LayerKind.Levels, Alias: "Levels"),
                    new Entry("Curvas", LayerKind.Curves, Alias: "Curves"), new Entry("Exposição", LayerKind.Exposure, Alias: "Exposure"),
                    new Entry("Temperatura", LayerKind.Temperature, Alias: "Temperature Kelvin"), new Entry("Lift / Gamma / Gain", LayerKind.LiftGammaGain),
                    new Entry("Posterizar", LayerKind.Posterize, Alias: "Posterize"), new Entry("Limiar", LayerKind.Threshold, Alias: "Threshold"),
                    new Entry("Mapa de degradê", LayerKind.GradientMap, Alias: "Gradient Map duotone")),
                S(EngineKind.Matrix,
                    new Entry("Matiz/Saturação", LayerKind.HueSaturation, Alias: "Hue Saturation"), new Entry("Equilíbrio de cores", LayerKind.ColorBalance, Alias: "Color Balance"),
                    new Entry("Preto e branco", LayerKind.BlackWhite, Alias: "Black White"), new Entry("Filtro de fotos", LayerKind.PhotoFilter, Alias: "Photo Filter"),
                    new Entry("Misturador de canais", LayerKind.ChannelMixer, Alias: "Channel Mixer"), new Entry("Simular daltonismo", LayerKind.ColorBlind, Alias: "Color blind"),
                    new Entry("Inverter", LayerKind.Invert, Alias: "Invert")),
                S(EngineKind.Driver, new Entry("Vibração", LayerKind.Vibrance, Alias: "Vibrance color")),
                S(EngineKind.Overlay,
                    new Entry("Granulação", LayerKind.Grain, Alias: "Grain"), new Entry("Cor sólida", LayerKind.SolidColor, Alias: "Solid Color"),
                    new Entry("Degradê", LayerKind.Gradient, Alias: "Gradient"), new Entry("Vinheta", LayerKind.Vignette, Alias: "Vignette"),
                    new Entry("Guias e grade", LayerKind.Guides, Alias: "Guides grid"), new Entry("Padrão", LayerKind.Pattern, Alias: "Pattern scanlines dots")),
                S(EngineKind.Hardware, new Entry("Monitor (brilho, contraste, cor…)", LayerKind.MonitorHardware, Alias: "Monitor DDC hardware")),
                S(EngineKind.System, new Entry("Tema do Windows (claro/escuro)", LayerKind.WindowsTheme, Alias: "Windows theme dark light")),
                S(EngineKind.Shader,
                    new Entry("Pesquisa de cores (LUT .cube)", LayerKind.ColorLookup, Alias: "Color Lookup LUT cube"),
                    new Entry("Cor seletiva", LayerKind.SelectiveColor, Alias: "Selective Color"),
                    new Entry("Nitidez e remover névoa", LayerKind.Sharpen, Alias: "Sharpen Clarity dehaze")),
            };
        }

        static string Fold(string s)
        {
            var n = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in n) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <param name="blocked">Returns a reason when a kind can't be added again here (shown disabled with that reason).</param>
        public static void Show(FrameworkElement target, Action<LayerKind> pick, Action<HelpInfo?>? hover = null, Func<LayerKind, string?>? blocked = null)
        {
            var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
            // leaves the way it came: shrinks back toward the button, then closes
            void Close() { if (popup.Child is FrameworkElement c && popup.IsOpen) Motion.Exit(c, () => popup.IsOpen = false); else popup.IsOpen = false; }
            var root = new StackPanel();
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            var hint = new TextBlock { Text = "Buscar ajuste…", Foreground = Ui.Res("Dim"), IsHitTestVisible = false, Margin = new Thickness(8, 4, 0, 0) };
            var searchHost = new Grid();
            searchHost.Children.Add(search);
            searchHost.Children.Add(hint);
            root.Children.Add(searchHost);

            var list = new StackPanel();
            var heads = new Dictionary<Section, FrameworkElement>();
            var rows = new List<(FrameworkElement row, Entry e, Section s, StackPanel items, Action<bool> expand)>();
            Button? firstVisible() => rows.Where(r => r.row.Visibility == Visibility.Visible && r.e.Kind != null && r.items.Visibility == Visibility.Visible)
                                          .Select(r => r.row as Button).FirstOrDefault();

            foreach (var sec in Sections())
            {
                var color = Ui.Hex(sec.Color);
                var items = new StackPanel { Visibility = Visibility.Collapsed };
                var chevron = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 14, Foreground = Ui.Res("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                void Expand(bool on) { items.Visibility = on ? Visibility.Visible : Visibility.Collapsed; chevron.Text = on ? "" : ""; }

                var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 0) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                head.ColumnDefinitions.Add(new ColumnDefinition());
                head.Children.Add(new Border { Background = color, CornerRadius = new CornerRadius(1.5) });
                var hp = new DockPanel { Margin = new Thickness(10, 7, 8, 7) };
                var count = new TextBlock { Text = sec.Items.Length.ToString(), Foreground = Ui.Res("Dim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(count, Dock.Right);
                hp.Children.Add(count);
                hp.Children.Add(chevron);
                hp.Children.Add(new TextBlock { Text = sec.Name, FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                hp.Children.Add(new TextBlock { Text = "  " + sec.Sub, FontSize = 11, Foreground = Ui.Res("Dim"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(hp, 1);
                head.Children.Add(hp);
                var hoverBg = Ui.Res("Hover");
                head.MouseEnter += (_, _) => hp.Background = hoverBg;
                head.MouseLeave += (_, _) => hp.Background = Brushes.Transparent;
                head.MouseLeftButtonUp += (_, _) => Expand(items.Visibility != Visibility.Visible);
                heads[sec] = head;
                list.Children.Add(head);

                foreach (var e in sec.Items)
                {
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.Children.Add(new Border { Background = color, Opacity = 0.55 });
                    var label = new TextBlock { Text = e.Label + (e.Kind == LayerKind.Invert ? "" : "..."), Margin = new Thickness(28, 0, 0, 0) };
                    Grid.SetColumn(label, 1);
                    g.Children.Add(label);
                    if (e.Kind is LayerKind rk)
                    {
                        var chips = Ui.ResourceChips(rk);
                        chips.HorizontalAlignment = HorizontalAlignment.Right;
                        chips.Margin = new Thickness(0, 0, 10, 0);
                        Grid.SetColumn(chips, 1);
                        g.Children.Add(chips);
                    }
                    var btn = new Button
                    {
                        Content = g, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 7, 0, 7),
                        Background = Brushes.Transparent, BorderThickness = new Thickness(0), BorderBrush = Brushes.Transparent,
                    };
                    btn.Template = FlatTemplate();
                    if (e.Kind is LayerKind bk && blocked?.Invoke(bk) is string why)
                    {
                        btn.IsEnabled = false;
                        btn.ToolTip = why;
                        ToolTipService.SetShowOnDisabled(btn, true);
                    }
                    else if (e.Kind is LayerKind k)
                    {
                        btn.Click += (_, _) => { Close(); pick(k); };
                        var info = new HelpInfo(e.Label, HelpTexts.Layer.TryGetValue(k, out var t) ? t : "", HelpTexts.EngineFacts(LayerDefs.Get(k).Engine, App.Ctl.Settings.HideOverlayFromCapture));
                        btn.MouseEnter += (_, _) => hover?.Invoke(info);
                    }
                    else
                    {
                        btn.IsEnabled = false;
                        btn.ToolTip = e.Why ?? "Precisa de captura de tela (modo shader). Fica para uma próxima versão.";
                        ToolTipService.SetShowOnDisabled(btn, true);
                    }
                    items.Children.Add(btn);
                    rows.Add((btn, e, sec, items, Expand));
                }
                list.Children.Add(items);
            }

            search.TextChanged += (_, _) =>
            {
                var q = Fold(search.Text.Trim());
                hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var grp in rows.GroupBy(r => r.s))
                {
                    int shown = 0;
                    foreach (var r in grp)
                    {
                        bool match = q.Length == 0 || Fold(r.e.Label).Contains(q) || (r.e.Alias != null && Fold(r.e.Alias).Contains(q)) || Fold(grp.Key.Name).Contains(q);
                        r.row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        if (match) shown++;
                    }
                    grp.First().expand(q.Length > 0 && shown > 0);
                    heads[grp.Key].Visibility = q.Length > 0 && shown == 0 ? Visibility.Collapsed : Visibility.Visible;
                }
            };
            search.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && firstVisible() is Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
                if (e.Key == Key.Escape) Close();
            };

            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            popup.Child = new Border
            {
                Background = Ui.Res("Raised"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8), Width = 320, Child = root,
            };
            // popups don't inherit the window's text color
            if (popup.Child is Border pb) TextElement.SetForeground(pb, Ui.Res("Text"));
            popup.Opened += (_, _) =>
            {
                search.Focus();
                if (popup.Child is FrameworkElement c) { Access.Scale(c); Motion.Enter(c, new Point(0, 0)); } // grows out of the button
            };
            popup.IsOpen = true;
        }

        static ControlTemplate? _flat;
        static ControlTemplate FlatTemplate()
        {
            if (_flat != null) return _flat;
            var t = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(cp);
            t.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Application.Current.FindResource("Hover"), "b"));
            t.Triggers.Add(hover);
            var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(Border.BackgroundProperty, Application.Current.FindResource("Hover"), "b"));
            t.Triggers.Add(press);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            t.Triggers.Add(dis);
            t.Seal();
            return _flat = t;
        }
    }
}
