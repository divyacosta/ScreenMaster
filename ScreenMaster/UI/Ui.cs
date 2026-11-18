using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>Small factory for the code-built panels (properties, monitors page).</summary>
    public static class Ui
    {
        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        /// <summary>Icon font: Material Symbols Rounded, embedded.</summary>
        public static FontFamily Icons => (FontFamily)Application.Current.FindResource("Icons");

        public static Brush Hex(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
            b.Freeze();
            return b;
        }

        /// <summary>What an adjustment runs on and what it costs: "GPU" (no cost: a table in the video card or Windows), "CPU + GPU" (animated grain), the monitor or Windows.</summary>
        public static (string[] parts, string cost) Resource(LayerKind kind) => LayerDefs.Get(kind).Engine switch
        {
            EngineKind.Gamma or EngineKind.Matrix or EngineKind.Driver => (new[] { "GPU" }, "sem custo"),
            EngineKind.Overlay when kind == LayerKind.Grain => (new[] { "CPU", "GPU" }, "leve, por ser animada"),
            EngineKind.Overlay => (new[] { "GPU" }, "quase sem custo"),
            EngineKind.Hardware => (new[] { "Monitor" }, "sem custo"),
            EngineKind.Shader => (new[] { "GPU" }, "moderado: processa cada quadro"),
            _ => (new[] { "Windows" }, "sem custo"),
        };

        /// <summary>Small chips: a square in the brand color (Intel blue, AMD red, NVIDIA green) + CPU/GPU.</summary>
        public static FrameworkElement ResourceChips(LayerKind kind)
        {
            var (parts, cost) = Resource(kind);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var p in parts)
            {
                var brand = p == "CPU" ? Native.Hardware.Cpu : p == "GPU" ? Native.Hardware.Gpu : null;
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
                if (brand != null)
                    chip.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(1), Background = Hex(brand.Color), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                chip.Children.Add(new TextBlock { Text = p, FontSize = 11, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(chip);
            }
            sp.ToolTip = ResourceText(kind);
            return sp;
        }

        public static string ResourceText(LayerKind kind)
        {
            var (parts, cost) = Resource(kind);
            string Who(string p) => p switch
            {
                "CPU" => "CPU" + (Native.Hardware.Cpu.Name.Length > 0 ? $" ({Native.Hardware.Cpu.Name})" : ""),
                "GPU" => "GPU" + (Native.Hardware.GpuName.Length > 0 ? $" ({Native.Hardware.GpuName})" : ""),
                "Monitor" => "o próprio monitor",
                _ => "o Windows",
            };
            return $"Usa {string.Join(" e ", parts.Select(Who))} · {cost}";
        }

        public static TextBlock Caption(string text, double top = 0)
            => new() { Text = text, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, top, 0, 8) };

        public static TextBlock Note(string text, Brush? color = null)
            => new() { Text = text, Foreground = color ?? Res("Dim"), FontSize = 11, TextWrapping = TextWrapping.Wrap };

        public static Grid Row(string label, FrameworkElement control, double labelWidth = 150)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Dim"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        /// <summary>
        /// Label + numeric box + slider. <paramref name="set"/> is called while dragging;
        /// <paramref name="commit"/> (optional) when a drag ends or a value is typed/clicked.
        /// </summary>
        public static (Grid row, Slider slider, CheckBox? check) Slider(string label, double min, double max, double step, double value, double def, string unit,
            Action<double> set, Action<double>? commit = null, bool? optionalOn = null, Action<bool>? optionalChanged = null)
        {
            // Inspector row: quiet label left, the value as a large tabular number right (Lunar-style), hairline slider below.
            string fmt = step < 0.01 ? "0.000" : step < 1 ? "0.00" : "0";
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());

            FrameworkElement title;
            CheckBox? check = null;
            if (optionalOn != null)
            {
                check = new CheckBox { Content = label, IsChecked = optionalOn, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
                title = check;
            }
            else title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Bottom, Foreground = Res("Dim"), Margin = new Thickness(0, 0, 0, 4), ToolTip = "Duplo clique para voltar ao padrão", Cursor = Cursors.Hand };

            var box = new TextBox { Style = (Style)Application.Current.FindResource("ValueBox"), MinWidth = 56, Text = value.ToString(fmt, CultureInfo.CurrentCulture) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            right.Children.Add(box);
            right.Children.Add(new TextBlock { Text = unit, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 5), MinWidth = 12 });
            var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10, Margin = new Thickness(0, 2, 0, 0) };
            if (step >= 1) { s.TickFrequency = step; s.IsSnapToTickEnabled = true; }

            bool dragging = false, inner = false;
            s.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
            s.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; commit?.Invoke(s.Value); }));
            s.ValueChanged += (_, e) =>
            {
                if (inner) return;
                inner = true;
                var v = Math.Round(e.NewValue / step) * step;
                box.Text = v.ToString(fmt, CultureInfo.CurrentCulture);
                set(v);
                if (!dragging) commit?.Invoke(v);
                inner = false;
            };
            void Commit()
            {
                if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    v = Math.Clamp(v, min, max);
                    s.Value = v;
                    box.Text = v.ToString(fmt, CultureInfo.CurrentCulture);
                }
                else box.Text = s.Value.ToString(fmt, CultureInfo.CurrentCulture);
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            if (title is TextBlock tb) tb.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) s.Value = def; };

            if (check != null)
            {
                void Sync(bool on) { s.IsEnabled = on; box.IsEnabled = on; s.Opacity = box.Opacity = on ? 1 : 0.4; }
                Sync(optionalOn == true);
                check.Checked += (_, _) => { Sync(true); optionalChanged?.Invoke(true); };
                check.Unchecked += (_, _) => { Sync(false); optionalChanged?.Invoke(false); };
            }

            g.Children.Add(title);
            Grid.SetColumn(right, 1); g.Children.Add(right);
            Grid.SetRow(s, 1); Grid.SetColumnSpan(s, 2); g.Children.Add(s);
            return (g, s, check);
        }

        /// <summary>A section separated by a hairline above — the replacement for nested cards.</summary>
        public static Border Section(UIElement child, double top = 14) => new()
        {
            BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, top, 0, 0), Margin = new Thickness(0, top, 0, 0), Child = child,
        };

        public static Border Badge(string text, Brush bg) => new()
        {
            Background = bg, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.White },
        };
    }
}
