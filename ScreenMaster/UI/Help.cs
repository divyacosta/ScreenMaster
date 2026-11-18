using System;
using System.Windows;
using System.Windows.Media;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>Attached help for the Info box. Either a fixed HelpInfo or a provider evaluated on hover.</summary>
    public static class Help
    {
        public static readonly DependencyProperty InfoProperty =
            DependencyProperty.RegisterAttached("Info", typeof(object), typeof(Help), new PropertyMetadata(null));

        public static void Set(DependencyObject el, HelpInfo info) => el.SetValue(InfoProperty, info);
        public static void Set(DependencyObject el, string uiKey) { if (HelpTexts.Ui.TryGetValue(uiKey, out var h)) el.SetValue(InfoProperty, h); }
        public static void Set(DependencyObject el, Func<HelpInfo?> provider) => el.SetValue(InfoProperty, provider);

        /// <summary>Walks up from the element under the mouse to the first element with help.</summary>
        public static HelpInfo? Find(DependencyObject? d)
        {
            while (d != null)
            {
                var v = d.GetValue(InfoProperty);
                if (v is HelpInfo h) return h;
                if (v is Func<HelpInfo?> f) { var r = f(); if (r != null) return r; }
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
