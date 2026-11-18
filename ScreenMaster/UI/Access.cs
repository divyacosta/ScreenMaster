using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace ScreenMaster.UI
{
    /// <summary>
    /// Follows the Windows accessibility settings: text size (Settings › Accessibility › Text size),
    /// high contrast, and "Animation effects". Windows, popups, menus and tooltips scale together,
    /// so a bigger text never breaks the layout.
    /// </summary>
    public static class Access
    {
        static double _scale = 1;

        /// <summary>Windows text size (1.0 = 100%, up to 2.25).</summary>
        public static double TextScale => _scale;

        /// <summary>Short UI motion is on unless Windows has animations off or high contrast is on.</summary>
        public static bool Motion => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

        public static void Init(Application app)
        {
            _scale = ReadScale();
            if (SystemParameters.HighContrast) UseSystemColors(app);

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply(s as Window)));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Scale(s as FrameworkElement)));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => Scale(s as FrameworkElement)));

            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is not (UserPreferenceCategory.Accessibility or UserPreferenceCategory.General or UserPreferenceCategory.Window)) return;
                app.Dispatcher.BeginInvoke(() =>
                {
                    double s = ReadScale();
                    if (Math.Abs(s - _scale) < 0.001) return;
                    _scale = s;
                    foreach (Window w in app.Windows) Apply(w);
                });
            };
        }

        static double ReadScale()
        {
            // SCREENMASTER_TEXTSCALE=1.5 previews a text size without changing the Windows setting (tests)
            if (double.TryParse(Environment.GetEnvironmentVariable("SCREENMASTER_TEXTSCALE"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced is >= 1 and <= 2.25) return forced;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                if (k?.GetValue("TextScaleFactor") is int v && v >= 100 && v <= 225) return v / 100.0;
            }
            catch { }
            return 1;
        }

        static void Apply(Window? w)
        {
            if (w == null) return;
            Crisp(w);
            if (w.Content is FrameworkElement root) Scale(root);
        }

        /// <summary>No half-pixel edges or soft small text: layout snapped to pixels, text drawn for the screen.</summary>
        public static void Crisp(FrameworkElement el)
        {
            el.UseLayoutRounding = true;
            el.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(el, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(el, TextRenderingMode.Grayscale); // no colored ClearType fringes on the dark background
        }

        /// <summary>Scales a popup's or window's content by the Windows text size.</summary>
        public static void Scale(FrameworkElement? el)
        {
            if (el == null) return;
            Crisp(el);
            el.LayoutTransform = Math.Abs(_scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(_scale, _scale);
        }

        /// <summary>High contrast: the app's own palette gives way to the system colors the user chose.</summary>
        static void UseSystemColors(Application app)
        {
            void Set(string key, Brush b) => app.Resources[key] = b;
            Set("Bg", SystemColors.WindowBrush);
            Set("Panel", SystemColors.WindowBrush);
            Set("Raised", SystemColors.ControlBrush);
            Set("Hover", SystemColors.HighlightBrush);
            Set("AccentDim", SystemColors.HighlightBrush);
            Set("Line", SystemColors.WindowTextBrush);
            Set("LineStrong", SystemColors.WindowTextBrush);
            Set("Text", SystemColors.WindowTextBrush);
            Set("Dim", SystemColors.GrayTextBrush);
            Set("Accent", SystemColors.HotTrackBrush);
            Set("OnAccent", SystemColors.WindowBrush);
            Set("Danger", SystemColors.HotTrackBrush);
            Set("Warn", SystemColors.HotTrackBrush);
        }
    }

    /// <summary>
    /// Short, critically damped motion (no bounce) for things that appear: they grow out of the control
    /// that opened them and leave the same way. Off when Windows has animations off.
    /// </summary>
    public static class Motion
    {
        static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
        static readonly IEasingFunction In = new CubicEase { EasingMode = EasingMode.EaseIn };

        /// <summary>Grow from <paramref name="origin"/> (0..1 of the element) with a fade, ~150 ms.</summary>
        public static void Enter(FrameworkElement el, Point origin, double from = 0.96)
        {
            if (!Access.Motion) { el.Opacity = 1; return; }
            var st = new ScaleTransform(from, from);
            el.RenderTransformOrigin = origin;
            el.RenderTransform = st;
            var d = new Duration(TimeSpan.FromMilliseconds(150));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, d) { EasingFunction = Out });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, d) { EasingFunction = Out });
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110))));
        }

        /// <summary>Shrink back toward the origin and fade, then run <paramref name="done"/>.</summary>
        public static void Exit(FrameworkElement el, Action done, double to = 0.96)
        {
            if (!Access.Motion) { done(); return; }
            var st = el.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            el.RenderTransform = st;
            var d = new Duration(TimeSpan.FromMilliseconds(110));
            var fade = new DoubleAnimation(el.Opacity, 0, d) { EasingFunction = In };
            fade.Completed += (_, _) => { done(); el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = 1; };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(st.ScaleX, to, d) { EasingFunction = In });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(st.ScaleY, to, d) { EasingFunction = In });
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>Slide in from below by a few pixels with a fade (toast); Exit mirrors it.</summary>
        public static void Rise(FrameworkElement el, bool show, Action? done = null)
        {
            if (!Access.Motion) { el.Visibility = show ? Visibility.Visible : Visibility.Collapsed; done?.Invoke(); return; }
            var tt = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
            el.RenderTransform = tt;
            if (show) el.Visibility = Visibility.Visible;
            var d = new Duration(TimeSpan.FromMilliseconds(show ? 160 : 120));
            var ease = show ? Out : In;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(show ? 10 : 0, show ? 0 : 10, d) { EasingFunction = ease });
            var fade = new DoubleAnimation(show ? 0 : 1, show ? 1 : 0, d) { EasingFunction = ease };
            if (!show) fade.Completed += (_, _) => { el.Visibility = Visibility.Collapsed; el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = 1; done?.Invoke(); };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>Menus grow from their top-left corner (where they hang from the control or the mouse).</summary>
        public static void HookMenus()
        {
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
            {
                if (s is ContextMenu m) Enter(m, new Point(0, 0), 0.97);
            }));
        }
    }
}
