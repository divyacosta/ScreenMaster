using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>Shared horizontal zoom/scroll for the ruler and all lanes (minutes of the day).</summary>
    public sealed class ArrangementView
    {
        public double Start { get; private set; }
        public double Span { get; private set; } = 1440;
        public event Action? Changed;

        public double X(double minute, double width) => (minute - Start) / Span * width;
        public double Min(double x, double width) => Start + x / Math.Max(1, width) * Span;

        public void Zoom(double factor, double anchorMin)
        {
            double span = Math.Clamp(Span / factor, 60, 1440);
            double start = anchorMin - (anchorMin - Start) * (span / Span);
            Set(start, span);
        }

        public void Scroll(double minutes) => Set(Start + minutes, Span);
        public void Fit() => Set(0, 1440);

        void Set(double start, double span)
        {
            Span = span;
            Start = Math.Clamp(start, 0, 1440 - span);
            Changed?.Invoke();
        }

        public static double Snap(double m) => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Math.Round(m) : Math.Round(m / 15) * 15;

        public static string Fmt(double m) { int t = (int)Math.Round(((m % 1440) + 1440) % 1440); if (t == 1440) t = 0; return $"{t / 60:00}:{t % 60:00}"; }
    }

    static class LaneDraw
    {
        public static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C)));
        public static readonly Pen Grid = FreezePen(Color.FromRgb(0x22, 0x22, 0x25), 1);
        public static readonly Pen GridStrong = FreezePen(Color.FromRgb(0x30, 0x30, 0x35), 1);
        /// <summary>Lime means "now" (the now line and time). Nothing else uses it.</summary>
        public static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0xF2, 0x4E)));
        public static readonly Brush SunMark = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)));
        /// <summary>The live "now" marker.</summary>
        public static readonly Brush Red = Amber;
        /// <summary>Simulated time: neutral white, so it never reads as live.</summary>
        public static readonly Brush Sim = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE8)));
        public static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)));
        public static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static Brush Freeze(Brush b) { b.Freeze(); return b; }
        static Pen FreezePen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

        public static void HourGrid(DrawingContext dc, ArrangementView v, double w, double top, double h)
        {
            int step = v.Span > 720 ? 60 : 30;
            for (double m = Math.Ceiling(v.Start / step) * step; m <= v.Start + v.Span; m += step)
            {
                double x = v.X(m, w);
                dc.DrawLine(m % 180 == 0 ? GridStrong : Grid, new Point(x, top), new Point(x, top + h));
            }
        }

        public static void Playhead(DrawingContext dc, ArrangementView v, double w, double top, double h)
        {
            var t = App.Ctl.Simulated ?? DateTime.Now;
            double m = t.Hour * 60 + t.Minute + t.Second / 60.0;
            double x = v.X(m, w);
            if (x < 0 || x > w) return;
            dc.DrawRectangle(App.Ctl.Simulated != null ? Sim : Red, null, new Rect(x - 1, top, 2, h));
        }
    }

    /// <summary>Hour ruler with sunrise/sunset marks. Drag on it to simulate a time (scrub).</summary>
    public sealed class Ruler : FrameworkElement
    {
        readonly ArrangementView _v;
        bool _scrub;

        public Ruler(ArrangementView v)
        {
            _v = v;
            Height = 26;
            Cursor = Cursors.SizeWE;
            v.Changed += InvalidateVisual;
            ToolTip = "Clique ou arraste para ver um horário (fica parado ali). Duplo clique volta ao vivo.";
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int step = _v.Span > 960 ? 180 : _v.Span > 480 ? 60 : 30;
            for (double m = Math.Ceiling(_v.Start / step) * step; m <= _v.Start + _v.Span; m += step)
            {
                double x = _v.X(m, w);
                dc.DrawLine(LaneDraw.GridStrong, new Point(x, h - 6), new Point(x, h));
                if (m >= 1440) continue;
                var ft = new FormattedText(ArrangementView.Fmt(m), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LaneDraw.Face, 10, LaneDraw.Dim, dpi);
                dc.DrawText(ft, new Point(Math.Min(x + 3, w - ft.Width), 2));
            }

            var loc = App.Ctl.Settings.Location;
            var date = (App.Ctl.Simulated ?? DateTime.Now).Date;
            if (loc != null && Sun.Times(date, loc.Lat, loc.Lon) is { } sun)
            {
                foreach (var (t, glyph) in new[] { (sun.rise, ""), (sun.set, "") })
                {
                    double x = _v.X(t, w);
                    if (x < 0 || x > w) continue;
                    var ft = new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(Ui.Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, LaneDraw.SunMark, dpi);
                    dc.DrawText(ft, new Point(x - ft.Width / 2, h - 16));
                }
            }

            var now = App.Ctl.Simulated ?? DateTime.Now;
            double nx = _v.X(now.Hour * 60 + now.Minute, w);
            if (nx >= 0 && nx <= w)
            {
                var brush = App.Ctl.Simulated != null ? LaneDraw.Sim : LaneDraw.Red;
                var label = new FormattedText((App.Ctl.Simulated != null ? "simulando " : "") + now.ToString("HH:mm"), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LaneDraw.Face, 10, LaneDraw.Bg, dpi);
                var r = new Rect(Math.Clamp(nx - label.Width / 2 - 5, 0, Math.Max(0, w - label.Width - 10)), h - 14, label.Width + 10, 14);
                dc.DrawRoundedRectangle(brush, null, r, 3, 3);
                dc.DrawText(label, new Point(r.X + 5, r.Y));
            }
        }

        DateTime _scrubAt;
        void ScrubTo(Point p, bool scrubbing = true)
        {
            double m = Math.Clamp(_v.Min(p.X, ActualWidth), 0, 1439.9);
            _scrubAt = DateTime.Today.AddMinutes(m);
            App.Ctl.Simulate(_scrubAt, scrubbing);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount == 2) { _scrub = false; ReleaseMouseCapture(); App.Ctl.Simulate(null); return; }
            _scrub = true;
            CaptureMouse();
            ScrubTo(e.GetPosition(this));
        }

        DateTime _lastScrub;
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_scrub || (DateTime.UtcNow - _lastScrub).TotalMilliseconds < 15) return; // ~1 per frame
            _lastScrub = DateTime.UtcNow;
            ScrubTo(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!_scrub) return;
            _scrub = false;
            ReleaseMouseCapture();
            ScrubTo(e.GetPosition(this), scrubbing: false); // stays pinned at this time
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e) => LaneControl.Wheel(_v, this, e);
        protected override void OnRenderSizeChanged(SizeChangedInfo i) { base.OnRenderSizeChanged(i); InvalidateVisual(); }
    }

    /// <summary>
    /// One automation lane (Ableton-style): click to add a point, drag to move (Shift = 1 min),
    /// Alt+drag a segment to bend it, double-click or Delete removes, right-click for sun anchors.
    /// </summary>
    public sealed class LaneControl : FrameworkElement
    {
        readonly ArrangementView _v;
        public AgendaTrack Track { get; }
        public Lane Lane { get; }
        public event Action<bool>? Changed;
        /// <summary>A point was selected (or deselected with null).</summary>
        public event Action<LaneControl, Keyframe?>? PointSelected;
        public Keyframe? SelectedPoint => _sel;

        public void Select(Keyframe? k) { _sel = k; InvalidateVisual(); }

        Keyframe? _sel, _drag, _bend;
        double _bendStartY, _bendStartCurve;
        const double Pad = 5;

        public LaneControl(ArrangementView v, AgendaTrack track, Lane lane)
        {
            _v = v; Track = track; Lane = lane;
            Height = 56;
            Focusable = true;
            Cursor = Cursors.Cross;
            v.Changed += InvalidateVisual;
        }

        (double min, double max, bool stepped, string unit) R => Automation.Range(Track.Layer.Kind, Lane.Param) ?? (0, 100, false, "");
        Color Col => (Color)ColorConverter.ConvertFromString(Categories.Of(Track.Layer.Kind).Color);
        DateTime Date => (App.Ctl.Simulated ?? DateTime.Now).Date;
        Location? Loc => App.Ctl.Settings.Location;

        double Y(double v) { var r = R; return Pad + (1 - (v - r.min) / Math.Max(1e-9, r.max - r.min)) * (ActualHeight - 2 * Pad); }
        double V(double y) { var r = R; double v = r.min + (1 - (y - Pad) / Math.Max(1, ActualHeight - 2 * Pad)) * (r.max - r.min); v = Math.Clamp(v, r.min, r.max); return r.stepped ? Math.Round(v) : v; }
        double T(Keyframe k) => Automation.TimeOf(k, Date, Loc);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(LaneDraw.Bg, null, new Rect(0, 0, w, h));
            LaneDraw.HourGrid(dc, _v, w, 0, h);
            var col = Col;
            var line = new Pen(new SolidColorBrush(Lane.Overridden ? Color.FromArgb(110, col.R, col.G, col.B) : col), 2);
            if (Lane.Overridden) line.DashStyle = DashStyles.Dash;

            if (Lane.Points.Count == 0)
            {
                // no automation yet: flat line at the layer's own value
                double cur = Lane.Param == "opacity" ? Track.Layer.Opacity : Track.Layer.Get(Lane.Param);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B)), 1.5) { DashStyle = DashStyles.Dash }, new Point(0, Y(cur)), new Point(w, Y(cur)));
                var ft = new FormattedText("clique para criar um ponto", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LaneDraw.Face, 10, LaneDraw.Dim, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(8, 4));
            }
            else
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    int steps = (int)Math.Max(48, w / 3);
                    var r = R;
                    for (int i = 0; i <= steps; i++)
                    {
                        double m = _v.Start + _v.Span * i / steps;
                        double val = Automation.Eval(Lane, Math.Min(m, 1439.999), Date, Loc, r.stepped) ?? 0;
                        var p = new Point(_v.X(m, w), Y(val));
                        if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, line, geo);

                var fill = new SolidColorBrush(col);
                foreach (var k in Lane.Points)
                {
                    double x = _v.X(T(k), w);
                    if (x < -6 || x > w + 6) continue;
                    var c = new Point(x, Y(k.V));
                    bool sel = k == _sel;
                    if (k.Anchor != Anchor.None)
                    {
                        dc.DrawEllipse(sel ? LaneDraw.Amber : LaneDraw.Bg, new Pen(sel ? LaneDraw.Amber : fill, 1.5), c, 5, 5); // round = tied to the sun
                    }
                    else dc.DrawRectangle(sel ? LaneDraw.Amber : LaneDraw.Bg, new Pen(sel ? LaneDraw.Amber : fill, 1.5), new Rect(c.X - 4, c.Y - 4, 8, 8));
                }
            }
            LaneDraw.Playhead(dc, _v, w, 0, h);

            if (_drag != null)
            {
                var label = $"{ArrangementView.Fmt(T(_drag))} · {Automation.Format(Track.Layer.Kind, Lane.Param, _drag.V)}"
                          + (_drag.Anchor != Anchor.None ? $" ({(_drag.Anchor == Anchor.Sunrise ? "nascer" : "pôr")} do sol {(_drag.Offset >= 0 ? "+" : "")}{_drag.Offset:0} min)" : "");
                var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LaneDraw.Face, 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double x = Math.Clamp(_v.X(T(_drag), w) + 8, 0, Math.Max(0, w - ft.Width - 8));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 0x2C, 0x2C, 0x30)), null, new Rect(x - 4, 2, ft.Width + 8, ft.Height + 2), 3, 3);
                dc.DrawText(ft, new Point(x, 3));
            }
        }

        Keyframe? HitPoint(Point p)
        {
            foreach (var k in Lane.Points)
            {
                var c = new Point(_v.X(T(k), ActualWidth), Y(k.V));
                if ((c - p).Length <= 7) return k;
            }
            return null;
        }

        /// <summary>The point that starts the segment under x (for Alt+drag bending).</summary>
        Keyframe? SegmentStart(double minute)
        {
            if (Lane.Points.Count < 2) return null;
            var pts = Lane.Points.OrderBy(T).ToList();
            var prev = pts.LastOrDefault(k => T(k) <= minute);
            return prev ?? pts[^1];
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            var p = e.GetPosition(this);
            var hit = HitPoint(p);
            if (e.ClickCount == 2 && hit != null)
            {
                Lane.Points.Remove(hit);
                _sel = null;
                Changed?.Invoke(true);
                PointSelected?.Invoke(this, null);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (hit == null && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                _bend = SegmentStart(_v.Min(p.X, ActualWidth));
                if (_bend != null) { _bendStartY = p.Y; _bendStartCurve = _bend.Curve; CaptureMouse(); e.Handled = true; return; }
            }
            if (hit == null)
            {
                double m = Math.Clamp(ArrangementView.Snap(_v.Min(p.X, ActualWidth)), 0, 1439);
                hit = new Keyframe { T = m, V = V(p.Y) };
                Lane.Points.Add(hit);
                Lane.Overridden = false;
                Changed?.Invoke(false);
            }
            _sel = _drag = hit;
            CaptureMouse();
            InvalidateVisual();
            PointSelected?.Invoke(this, hit);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_bend != null)
            {
                double dy = (_bendStartY - p.Y) / Math.Max(1, ActualHeight);
                _bend.Curve = Math.Clamp(_bendStartCurve + dy * 2, -1, 1);
                InvalidateVisual();
                Changed?.Invoke(false);
                return;
            }
            if (_drag == null)
            {
                Cursor = HitPoint(p) != null ? Cursors.SizeAll : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Cursors.SizeNS : Cursors.Cross;
                return;
            }
            double m = Math.Clamp(ArrangementView.Snap(_v.Min(p.X, ActualWidth)), 0, 1439);
            if (_drag.Anchor == Anchor.None) _drag.T = m;
            else
            {
                double baseT = Automation.TimeOf(new Keyframe { Anchor = _drag.Anchor }, Date, Loc);
                _drag.Offset = Math.Round(m - baseT);
            }
            _drag.V = V(p.Y);
            InvalidateVisual();
            Changed?.Invoke(false);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            bool wasEditing = _drag != null || _bend != null;
            _drag = null; _bend = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            if (wasEditing) { Changed?.Invoke(true); if (_sel != null) PointSelected?.Invoke(this, _sel); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.Key == Key.Delete || e.Key == Key.Back) && _sel != null)
            {
                Lane.Points.Remove(_sel);
                _sel = null;
                InvalidateVisual();
                Changed?.Invoke(true);
                PointSelected?.Invoke(this, null);
                e.Handled = true;
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            var hit = HitPoint(p);
            var menu = new ContextMenu();
            MenuItem Item(string text, Action a) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => { a(); InvalidateVisual(); Changed?.Invoke(true); }; menu.Items.Add(mi); return mi; }
            if (hit != null)
            {
                _sel = hit;
                bool hasLoc = App.Ctl.Settings.Location != null;
                void AnchorTo(Anchor a)
                {
                    double t = T(hit);
                    hit.Anchor = a;
                    double baseT = Automation.TimeOf(new Keyframe { Anchor = a }, Date, Loc);
                    hit.Offset = Math.Round(t - baseT);
                }
                var rise = Item("Prender ao nascer do sol", () => AnchorTo(Anchor.Sunrise));
                var set = Item("Prender ao pôr do sol", () => AnchorTo(Anchor.Sunset));
                if (!hasLoc) { rise.IsEnabled = set.IsEnabled = false; rise.ToolTip = "Escolha sua cidade em Configurações"; }
                if (hit.Anchor != Anchor.None) Item("Soltar do sol (horário fixo)", () => { hit.T = T(hit); hit.Anchor = Anchor.None; hit.Offset = 0; });
                Item("Curva reta", () => hit.Curve = 0);
                menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource(typeof(Separator)) });
                Item("Excluir ponto", () => { Lane.Points.Remove(hit); _sel = null; });
            }
            else
            {
                if (SegmentStart(_v.Min(p.X, ActualWidth)) is { } seg) Item("Deixar este trecho reto", () => seg.Curve = 0);
                Item("Limpar pontos desta automação", () => Lane.Points.Clear());
            }
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e) => Wheel(_v, this, e);

        /// <summary>Ctrl+wheel zooms around the mouse, Shift+wheel scrolls in time.</summary>
        public static void Wheel(ArrangementView v, FrameworkElement el, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                v.Zoom(e.Delta > 0 ? 1.25 : 0.8, v.Min(e.GetPosition(el).X, el.ActualWidth));
                e.Handled = true;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                v.Scroll(-e.Delta / 120.0 * v.Span / 12);
                e.Handled = true;
            }
        }

        public HelpInfo HelpAt(Point p)
        {
            double m = _v.Min(p.X, ActualWidth);
            var r = R;
            var val = Automation.Eval(Lane, Math.Clamp(m, 0, 1439.9), Date, Loc, r.stepped);
            var name = $"{Automation.Label(Track.Layer.Kind, Lane.Param)} · {Track.Layer.DisplayName}";
            var at = val != null ? $"Às {ArrangementView.Fmt(m)}: {Automation.Format(Track.Layer.Kind, Lane.Param, val.Value)}" : "Sem pontos ainda";
            return new HelpInfo(name,
                "Clique cria ponto, arraste move (Shift = minuto a minuto). Alt+arrastar um trecho entorta a curva. Duplo clique ou Delete remove. Clique direito: prender ao nascer/pôr do sol. Ctrl+roda dá zoom.",
                at + (Lane.Overridden ? " · sobrescrito à mão" : ""));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo i) { base.OnRenderSizeChanged(i); InvalidateVisual(); }
    }
}
