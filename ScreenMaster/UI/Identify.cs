using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenMaster.Native;

namespace ScreenMaster.UI
{
    /// <summary>Shows the number and name of each monitor on that monitor for 2 seconds.</summary>
    public static class Identify
    {
        public static void Show(IEnumerable<MonitorInfo> monitors)
        {
            foreach (var m in monitors)
            {
                var w = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight, Left = -10000, Top = -10000,
                };
                var panel = new StackPanel { Margin = new Thickness(28, 18, 28, 22) };
                panel.Children.Add(new TextBlock { Text = m.Number, FontSize = 96, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new TextBlock { Text = m.Name, FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new TextBlock { Text = $"{m.Bounds.Width}×{m.Bounds.Height}", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)), HorizontalAlignment = HorizontalAlignment.Center });
                w.Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x4A, 0x7A)), CornerRadius = new CornerRadius(14), Child = panel };
                var mon = m;
                w.SourceInitialized += (_, _) =>
                {
                    var h = new WindowInteropHelper(w).Handle;
                    Win32.SetWindowPos(h, Win32.HWND_TOPMOST, mon.Bounds.Left + 48, mon.Bounds.Top + 48, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                };
                w.Show();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, _) => { t.Stop(); w.Close(); };
                t.Start();
            }
        }
    }
}
