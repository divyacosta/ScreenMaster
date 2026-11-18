using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScreenMaster.Core;

namespace ScreenMaster.UI
{
    /// <summary>Photoshop-style curve editor. Points are [x,y] in 0..255.</summary>
    public sealed class CurveEditor : FrameworkElement
    {
        Layer? _layer;
        int _channel;
        int _drag = -1;
        const double Pad = 8;
        const double HitRadius = 9;

        public event Action? Changed;

        static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1D)));
        static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x33, 0x33, 0x38), 1);
        static readonly Pen DiagPen = FrozenPen(Color.FromRgb(0x44, 0x44, 0x4A), 1);
        static readonly Color[] ChannelColors =
        {
            Color.FromRgb(0xE6, 0xE6, 0xE8), Color.FromRgb(0xF2, 0x5C, 0x54), Color.FromRgb(0x5C, 0xD6, 0x6E), Color.FromRgb(0x4C, 0x8D, 0xF6),
        };

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

        public CurveEditor()
        {
            Height = 300;
            Cursor = Cursors.Cross;
            Focusable = true;
        }

        public void Bind(Layer layer, int channel)
        {
            _layer = layer;
            _channel = channel;
            InvalidateVisual();
        }

        public int Channel
        {
            get => _channel;
            set { _channel = value; InvalidateVisual(); }
        }

        Rect Area => new(Pad, Pad, Math.Max(10, ActualWidth - 2 * Pad), Math.Max(10, ActualHeight - 2 * Pad));

        Point ToScreen(double x, double y)
        {
            var a = Area;
            return new Point(a.Left + x / 255 * a.Width, a.Bottom - y / 255 * a.Height);
        }

        (double x, double y) FromScreen(Point p)
        {
            var a = Area;
            return (Math.Clamp((p.X - a.Left) / a.Width * 255, 0, 255), Math.Clamp((a.Bottom - p.Y) / a.Height * 255, 0, 255));
        }

        List<double[]> Points => _layer!.GetCurve(_channel);

        protected override void OnRender(DrawingContext dc)
        {
            var a = Area;
            dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
            for (int i = 1; i < 4; i++)
            {
                double x = a.Left + a.Width * i / 4, y = a.Top + a.Height * i / 4;
                dc.DrawLine(GridPen, new Point(x, a.Top), new Point(x, a.Bottom));
                dc.DrawLine(GridPen, new Point(a.Left, y), new Point(a.Right, y));
            }
            dc.DrawRectangle(null, GridPen, a);
            dc.DrawLine(DiagPen, a.BottomLeft, a.TopRight);
            if (_layer == null) return;

            // ghost curves of the other channels
            for (int c = 0; c < 4; c++)
            {
                if (c == _channel) continue;
                var pts = _layer.GetCurve(c);
                if (pts.Count == 2 && pts[0][0] == 0 && pts[0][1] == 0 && pts[1][0] == 255 && pts[1][1] == 255) continue;
                DrawCurve(dc, pts, new Pen(new SolidColorBrush(Color.FromArgb(90, ChannelColors[c].R, ChannelColors[c].G, ChannelColors[c].B)), 1));
            }

            var col = ChannelColors[_channel];
            DrawCurve(dc, Points, new Pen(new SolidColorBrush(col), 2));
            var fill = new SolidColorBrush(col);
            foreach (var p in Points)
            {
                var s = ToScreen(p[0], p[1]);
                dc.DrawRectangle(BgBrush, new Pen(fill, 1.5), new Rect(s.X - 4, s.Y - 4, 8, 8));
            }
        }

        void DrawCurve(DrawingContext dc, List<double[]> pts, Pen pen)
        {
            var f = ColorMath.Spline(pts);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i <= 255; i++)
                {
                    var s = ToScreen(i, f(i / 255.0) * 255);
                    if (i == 0) ctx.BeginFigure(s, false, false); else ctx.LineTo(s, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        int HitTest(Point p)
        {
            var pts = Points;
            for (int i = 0; i < pts.Count; i++)
            {
                var s = ToScreen(pts[i][0], pts[i][1]);
                if ((s - p).Length <= HitRadius) return i;
            }
            return -1;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (_layer == null) return;
            Focus();
            var p = e.GetPosition(this);
            int hit = HitTest(p);
            if (hit < 0)
            {
                var (x, y) = FromScreen(p);
                // add new point on the curve at this x if the click is near the curve, otherwise at the click
                var pts = Points;
                pts.Add(new[] { Math.Round(x), Math.Round(y) });
                pts.Sort((a, b) => a[0].CompareTo(b[0]));
                hit = pts.FindIndex(q => q[0] == Math.Round(x) && q[1] == Math.Round(y));
                Changed?.Invoke();
            }
            _drag = hit;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag < 0 || _layer == null) return;
            var pts = Points;
            var (x, y) = FromScreen(e.GetPosition(this));
            // keep x between neighbours
            double minX = _drag > 0 ? pts[_drag - 1][0] + 1 : 0;
            double maxX = _drag < pts.Count - 1 ? pts[_drag + 1][0] - 1 : 255;
            pts[_drag][0] = Math.Round(Math.Clamp(x, minX, maxX));
            pts[_drag][1] = Math.Round(y);
            InvalidateVisual();
            Changed?.Invoke();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            _drag = -1;
            ReleaseMouseCapture();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (_layer == null) return;
            int hit = HitTest(e.GetPosition(this));
            var pts = Points;
            if (hit >= 0 && pts.Count > 2)
            {
                pts.RemoveAt(hit);
                InvalidateVisual();
                Changed?.Invoke();
            }
            e.Handled = true;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            InvalidateVisual();
        }
    }
}
