using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ScreenMaster.Native;

namespace ScreenMaster.Core
{
    /// <summary>3x4 affine color matrix, column-vector convention: out = M * [r g b] + offset.</summary>
    public sealed class ColorMatrix
    {
        // row-major: [r0 r1 r2 ro, g0 g1 g2 go, b0 b1 b2 bo]
        public readonly double[] M = new double[12];

        public static ColorMatrix Identity()
        {
            var m = new ColorMatrix();
            m.M[0] = m.M[5] = m.M[10] = 1;
            return m;
        }

        public static ColorMatrix From3x3(double[,] a, double ro = 0, double go = 0, double bo = 0)
        {
            var m = new ColorMatrix();
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m.M[r * 4 + c] = a[r, c];
            m.M[3] = ro; m.M[7] = go; m.M[11] = bo;
            return m;
        }

        public static ColorMatrix Diagonal(double r, double g, double b)
            => From3x3(new double[,] { { r, 0, 0 }, { 0, g, 0 }, { 0, 0, b } });

        /// <summary>Returns "this, then next" (next ∘ this).</summary>
        public ColorMatrix Then(ColorMatrix next)
        {
            var o = new ColorMatrix();
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += next.M[r * 4 + k] * M[k * 4 + c];
                    o.M[r * 4 + c] = s;
                }
                double off = next.M[r * 4 + 3];
                for (int k = 0; k < 3; k++) off += next.M[r * 4 + k] * M[k * 4 + 3];
                o.M[r * 4 + 3] = off;
            }
            return o;
        }

        public ColorMatrix LerpFromIdentity(double t)
        {
            if (t >= 1) return this;
            var id = Identity();
            var o = new ColorMatrix();
            for (int i = 0; i < 12; i++) o.M[i] = id.M[i] + (M[i] - id.M[i]) * t;
            return o;
        }

        public bool IsIdentity
        {
            get
            {
                var id = Identity();
                for (int i = 0; i < 12; i++) if (Math.Abs(M[i] - id.M[i]) > 1e-6) return false;
                return true;
            }
        }

        public MAGCOLOREFFECT ToMag()
        {
            var t = new float[25];
            for (int i = 0; i < 3; i++)       // input channel
                for (int j = 0; j < 3; j++)   // output channel
                    t[i * 5 + j] = (float)M[j * 4 + i];
            for (int j = 0; j < 3; j++) t[4 * 5 + j] = (float)M[j * 4 + 3];
            t[3 * 5 + 3] = 1;
            t[4 * 5 + 4] = 1;
            return new MAGCOLOREFFECT { transform = t };
        }
    }

    public static class ColorMath
    {
        public const double LR = 0.2126, LG = 0.7152, LB = 0.0722;

        public static (double r, double g, double b) ParseHex(string hex)
        {
            try
            {
                hex = hex.Trim().TrimStart('#');
                if (hex.Length == 8) hex = hex.Substring(2);
                if (hex.Length != 6) return (1, 1, 1);
                int v = int.Parse(hex, NumberStyles.HexNumber);
                return (((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
            }
            catch { return (1, 1, 1); }
        }

        // ---------------------------------------------------------------- matrix layers

        public static ColorMatrix MatrixFor(Layer l)
        {
            switch (l.Kind)
            {
                case LayerKind.HueSaturation:
                {
                    var m = HueRotate(l.Get("hue"));
                    double s = 1 + l.Get("saturation") / 100.0;
                    m = m.Then(Saturate(s));
                    double L = l.Get("lightness") / 100.0;
                    if (L > 0) m = m.Then(From(1 - L, L));
                    else if (L < 0) m = m.Then(From(1 + L, 0));
                    return m;
                }
                case LayerKind.ColorBalance:
                {
                    double gr = 1 + l.Get("cr") / 100.0 * 0.35;
                    double gg = 1 + l.Get("mg") / 100.0 * 0.35;
                    double gb = 1 + l.Get("yb") / 100.0 * 0.35;
                    if (l.Get("preserve") > 0.5)
                    {
                        double lum = LR * gr + LG * gg + LB * gb;
                        gr /= lum; gg /= lum; gb /= lum;
                    }
                    return ColorMatrix.Diagonal(gr, gg, gb);
                }
                case LayerKind.BlackWhite:
                {
                    double wr = l.Get("r") / 100.0, wg = l.Get("g") / 100.0, wb = l.Get("b") / 100.0;
                    double tr = 1, tg = 1, tb = 1;
                    if (l.Get("tint") > 0.5)
                    {
                        var (r, g, b) = ParseHex(l.GetS("tintColor"));
                        double lum = Math.Max(0.05, LR * r + LG * g + LB * b);
                        tr = r / lum; tg = g / lum; tb = b / lum;
                    }
                    return ColorMatrix.From3x3(new double[,] {
                        { wr * tr, wg * tr, wb * tr },
                        { wr * tg, wg * tg, wb * tg },
                        { wr * tb, wg * tb, wb * tb } });
                }
                case LayerKind.PhotoFilter:
                {
                    var (r, g, b) = ParseHex(l.GetS("color"));
                    double d = l.Get("density") / 100.0;
                    double gr = 1 - d + d * r, gg = 1 - d + d * g, gb = 1 - d + d * b;
                    if (l.Get("preserve") > 0.5)
                    {
                        double lum = Math.Max(0.05, LR * gr + LG * gg + LB * gb);
                        gr /= lum; gg /= lum; gb /= lum;
                    }
                    return ColorMatrix.Diagonal(gr, gg, gb);
                }
                case LayerKind.ChannelMixer:
                {
                    double[] row(string o) => new[] { l.Get(o + "r") / 100, l.Get(o + "g") / 100, l.Get(o + "b") / 100, l.Get(o + "c") / 100 };
                    var R = row("r");
                    var G = l.Get("mono") > 0.5 ? R : row("g");
                    var B = l.Get("mono") > 0.5 ? R : row("b");
                    return ColorMatrix.From3x3(new double[,] {
                        { R[0], R[1], R[2] }, { G[0], G[1], G[2] }, { B[0], B[1], B[2] } }, R[3], G[3], B[3]);
                }
                case LayerKind.Invert:
                    return ColorMatrix.From3x3(new double[,] { { -1, 0, 0 }, { 0, -1, 0 }, { 0, 0, -1 } }, 1, 1, 1);
                case LayerKind.ColorBlind:
                    // Machado, Oliveira & Fernandes (2009), severity 1.0; opacity = severity.
                    return ((int)l.Get("type")) switch
                    {
                        0 => ColorMatrix.From3x3(new double[,] { { 0.152286, 1.052583, -0.204868 }, { 0.114503, 0.786281, 0.099216 }, { -0.003882, -0.048116, 1.051998 } }),
                        1 => ColorMatrix.From3x3(new double[,] { { 0.367322, 0.860646, -0.227968 }, { 0.280085, 0.672501, 0.047413 }, { -0.011820, 0.042940, 0.968881 } }),
                        2 => ColorMatrix.From3x3(new double[,] { { 1.255528, -0.076749, -0.178779 }, { -0.078411, 0.930809, 0.147602 }, { 0.004733, 0.691367, 0.303900 } }),
                        _ => ColorMatrix.From3x3(new double[,] { { LR, LG, LB }, { LR, LG, LB }, { LR, LG, LB } }),
                    };
                default:
                    return ColorMatrix.Identity();
            }
        }

        static ColorMatrix From(double scale, double offset)
            => ColorMatrix.From3x3(new double[,] { { scale, 0, 0 }, { 0, scale, 0 }, { 0, 0, scale } }, offset, offset, offset);

        public static ColorMatrix HueRotate(double degrees)
        {
            double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return ColorMatrix.From3x3(new double[,] {
                { 0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928 },
                { 0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283 },
                { 0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072 } });
        }

        static ColorMatrix Luma()
            => ColorMatrix.From3x3(new double[,] { { LR, LG, LB }, { LR, LG, LB }, { LR, LG, LB } });

        public static ColorMatrix Saturate(double s)
            => ColorMatrix.From3x3(new double[,] {
                { 0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s },
                { 0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s },
                { 0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s } });

        public static ColorMatrix ComposeMatrix(IEnumerable<Layer> layers)
        {
            var m = ColorMatrix.Identity();
            foreach (var l in layers)
            {
                if (!l.Enabled) continue;
                double o = Math.Clamp(l.Opacity / 100.0, 0, 1);
                // a gradient map needs one luminance in all three channels before its gamma ramp recolors it
                if (l.Kind == LayerKind.GradientMap) { m = m.Then(Luma().LerpFromIdentity(o)); continue; }
                if (LayerDefs.Get(l.Kind).Engine != EngineKind.Matrix) continue;
                m = m.Then(MatrixFor(l).LerpFromIdentity(o));
            }
            return m;
        }

        // ---------------------------------------------------------------- gamma layers

        /// <summary>Per-channel tone function for a gamma layer. Input/output 0..1.</summary>
        public static Func<int, double, double> ToneFor(Layer l)
        {
            switch (l.Kind)
            {
                case LayerKind.BrightnessContrast:
                {
                    double b = l.Get("brightness") / 100.0, c = l.Get("contrast") / 100.0;
                    double gexp = Math.Exp(-b * 0.9);
                    double k = c >= 0 ? 1 + c * 2 : 1 + c;
                    return (ch, x) =>
                    {
                        double y = Math.Pow(Math.Clamp(x, 0, 1), gexp);
                        return Math.Clamp(0.5 + (y - 0.5) * k, 0, 1);
                    };
                }
                case LayerKind.Levels:
                {
                    int sel = (int)l.Get("channel");
                    double ib = l.Get("inBlack"), iw = Math.Max(ib + 1, l.Get("inWhite"));
                    double g = Math.Max(0.01, l.Get("gamma"));
                    double ob = l.Get("outBlack"), ow = l.Get("outWhite");
                    return (ch, x) =>
                    {
                        if (sel != 0 && sel - 1 != ch) return x;
                        double v = Math.Clamp((x * 255 - ib) / (iw - ib), 0, 1);
                        v = Math.Pow(v, 1 / g);
                        return Math.Clamp((ob + v * (ow - ob)) / 255, 0, 1);
                    };
                }
                case LayerKind.Curves:
                {
                    var master = Spline(l.GetCurve(0));
                    var per = new[] { Spline(l.GetCurve(1)), Spline(l.GetCurve(2)), Spline(l.GetCurve(3)) };
                    return (ch, x) => master(per[ch](x));
                }
                case LayerKind.Exposure:
                {
                    double e = Math.Pow(2, l.Get("exposure")), off = l.Get("offset"), g = Math.Max(0.01, l.Get("gamma"));
                    return (ch, x) => Math.Pow(Math.Clamp(x * e + off, 0, 1), 1 / g);
                }
                case LayerKind.Posterize:
                {
                    int n = Math.Clamp((int)l.Get("levels"), 2, 255);
                    return (ch, x) => Math.Min(n - 1, Math.Floor(x * n)) / (n - 1);
                }
                case LayerKind.Threshold:
                {
                    double t = l.Get("level");
                    return (ch, x) => x * 255 >= t ? 1 : 0;
                }
                case LayerKind.GradientMap:
                {
                    var stops = new List<(double r, double g, double b)> { ParseHex(l.GetS("color1")) };
                    if (l.Get("mid") > 0.5) stops.Add(ParseHex(l.GetS("color2")));
                    stops.Add(ParseHex(l.GetS("color3")));
                    if (l.Get("reverse") > 0.5) stops.Reverse();
                    int segs = stops.Count - 1;
                    return (ch, x) =>
                    {
                        double p = Math.Clamp(x, 0, 1) * segs;
                        int i = Math.Min(segs - 1, (int)p);
                        double t = p - i;
                        var a = stops[i]; var b = stops[i + 1];
                        double va = ch == 0 ? a.r : ch == 1 ? a.g : a.b, vb = ch == 0 ? b.r : ch == 1 ? b.g : b.b;
                        return va + (vb - va) * t;
                    };
                }
                case LayerKind.Temperature:
                {
                    var m = KelvinGains(l.Get("kelvin"));
                    return (ch, x) => x * m[ch];
                }
                case LayerKind.LiftGammaGain:
                {
                    double[] Tint(string colorKey, string tintKey, double scale)
                    {
                        var (r, g, b) = ParseHex(l.GetS(colorKey));
                        double lum = LR * r + LG * g + LB * b, k = l.Get(tintKey) / 100.0 * scale;
                        return new[] { (r - lum) * k, (g - lum) * k, (b - lum) * k };
                    }
                    double liftL = l.Get("liftLevel") / 100.0 * 0.25, gammaL = l.Get("gammaLevel") / 100.0 * 0.6, gainL = l.Get("gainLevel") / 100.0 * 0.5;
                    var liftT = Tint("liftColor", "liftTint", 0.5);
                    var gammaT = Tint("gammaColor", "gammaTint", 0.8);
                    var gainT = Tint("gainColor", "gainTint", 0.6);
                    return (ch, x) =>
                    {
                        double lift = liftL + liftT[ch], gain = 1 + gainL + gainT[ch], gamma = Math.Max(0.1, 1 + gammaL + gammaT[ch]);
                        double v = Math.Clamp(x * gain + lift * (1 - x), 0, 1);
                        return Math.Clamp(Math.Pow(v, 1 / gamma), 0, 1);
                    };
                }
                default:
                    return (ch, x) => x;
            }
        }

        /// <summary>
        /// Per-channel gains for a color temperature (Tanner Helland's blackbody fit), normalized so
        /// 6500K = (1,1,1) and no channel exceeds 1 (warming only removes blue/green, never clips).
        /// </summary>
        public static double[] KelvinGains(double kelvin)
        {
            static (double r, double g, double b) Raw(double k)
            {
                double t = Math.Clamp(k, 1000, 40000) / 100.0;
                double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
                double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
                double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
                return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
            }
            var n = Raw(6500);
            var c = Raw(kelvin);
            double[] m = { c.r / n.r, c.g / n.g, c.b / n.b };
            double max = m.Max();
            return m.Select(v => v / max).ToArray();
        }

        /// <summary>Monotone cubic (Fritsch–Carlson) through points in 0..255 → function on 0..1.</summary>
        public static Func<double, double> Spline(List<double[]> raw)
        {
            var pts = raw.Select(p => (x: Math.Clamp(p[0], 0, 255) / 255, y: Math.Clamp(p[1], 0, 255) / 255))
                         .OrderBy(p => p.x).ToList();
            // dedupe x
            for (int i = pts.Count - 1; i > 0; i--)
                if (pts[i].x - pts[i - 1].x < 1e-6) pts.RemoveAt(i);
            if (pts.Count == 0) return x => x;
            if (pts.Count == 1) { var y0 = pts[0].y; return x => y0; }

            int n = pts.Count;
            var xs = pts.Select(p => p.x).ToArray();
            var ys = pts.Select(p => p.y).ToArray();
            var d = new double[n - 1];
            for (int i = 0; i < n - 1; i++) d[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);
            var m = new double[n];
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (Math.Abs(d[i]) < 1e-12) { m[i] = m[i + 1] = 0; continue; }
                double a = m[i] / d[i], b = m[i + 1] / d[i];
                double h = a * a + b * b;
                if (h > 9) { double t = 3 / Math.Sqrt(h); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
            }
            return x =>
            {
                if (x <= xs[0]) return ys[0];
                if (x >= xs[n - 1]) return ys[n - 1];
                int i = Array.BinarySearch(xs, x);
                if (i >= 0) return ys[i];
                i = ~i - 1;
                double hh = xs[i + 1] - xs[i], t = (x - xs[i]) / hh;
                double t2 = t * t, t3 = t2 * t;
                double y = (2 * t3 - 3 * t2 + 1) * ys[i] + (t3 - 2 * t2 + t) * hh * m[i]
                         + (-2 * t3 + 3 * t2) * ys[i + 1] + (t3 - t2) * hh * m[i + 1];
                return Math.Clamp(y, 0, 1);
            };
        }

        /// <summary>Composite LUT [channel][0..255] in 0..1 for one monitor. Null when identity.</summary>
        public static double[][]? ComposeGamma(IEnumerable<Layer> layers, string device)
        {
            var lut = new double[3][];
            for (int c = 0; c < 3; c++) { lut[c] = new double[256]; for (int i = 0; i < 256; i++) lut[c][i] = i / 255.0; }
            bool any = false;
            foreach (var l in layers)
            {
                if (!l.Enabled || !l.AppliesTo(device) || LayerDefs.Get(l.Kind).Engine != EngineKind.Gamma) continue;
                any = true;
                var f = ToneFor(l);
                double o = Math.Clamp(l.Opacity / 100.0, 0, 1);
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < 256; i++)
                    {
                        double v = lut[c][i];
                        lut[c][i] = v + (f(c, v) - v) * o;
                    }
            }
            if (!any) return null;
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < 256; i++)
                    if (Math.Abs(lut[c][i] - i / 255.0) > 1e-4) return lut;
            return null;
        }
    }
}
