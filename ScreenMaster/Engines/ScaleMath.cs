using System;
using System.Linq;

namespace ScreenMaster.Engines
{
    /// <summary>What the scale mode is told to do (from the hotkey or a program rule).</summary>
    public sealed record ScaleSpec(IntPtr Hwnd, string Device, int Aspect, bool Integer, int Filter = 0, double Sharpen = 0);

    /// <summary>Pure geometry for the scale mode (tested by --shadercheck).</summary>
    public static class ScaleMath
    {
        /// <summary>Single-pass filters (pixel shaders); index 5 = NVIDIA NIS (compute).</summary>
        public static readonly string[] FilterEntries = { "PS_Scale", "PS_ScaleBicubic", "PS_ScaleLanczos", "PS_Easu", "PS_Sgsr" };
        public const int NisFilter = 5;
        static readonly string[] BaseFilters = { "Bilinear (mais leve)", "Bicúbico (Catmull-Rom)", "Lanczos 3", "AMD FSR 1 (EASU)", "Snapdragon GSR (leve)", "NVIDIA NIS (até 2×)" };
        /// <summary>Multi-pass upscalers (xBR, Anime4K) come after NIS: filter index = GraphFilter0 + graph index.</summary>
        public const int GraphFilter0 = 6;
        public static readonly string[] Filters = BaseFilters.Concat(ShaderGraphs.All.Select(g => g.Name + " (2×)")).ToArray();
        public static readonly string[] Aspects = { "Manter a da janela", "4:3", "16:9", "21:9", "Esticar (tela toda)" };

        public readonly record struct Rect(double X, double Y, double W, double H)
        {
            public override string ToString() => $"{X:0.#},{Y:0.#} {W:0.#}x{H:0.#}";
        }

        /// <summary>Where a source of srcW×srcH lands on a dstW×dstH screen, centered, black around it.</summary>
        public static Rect Fit(int srcW, int srcH, int dstW, int dstH, int aspect, bool integer)
        {
            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return new Rect(0, 0, dstW, dstH);
            if (aspect == 4) return new Rect(0, 0, dstW, dstH);
            double ar = aspect switch { 1 => 4.0 / 3, 2 => 16.0 / 9, 3 => 21.0 / 9, _ => (double)srcW / srcH };
            double w, h;
            if (integer && aspect == 0)
            {
                int k = Math.Max(1, Math.Min(dstW / srcW, dstH / srcH));
                w = srcW * k; h = srcH * k;
            }
            else
            {
                w = dstW; h = w / ar;
                if (h > dstH) { h = dstH; w = h * ar; }
                w = Math.Round(w); h = Math.Round(h);
            }
            return new Rect(Math.Floor((dstW - w) / 2), Math.Floor((dstH - h) / 2), w, h);
        }

        /// <summary>A point inside the source rect (monitor pixels) → where it is drawn on the scaled image.</summary>
        public static (double x, double y) Map(double sx, double sy, Rect src, Rect dst)
            => (dst.X + (sx - src.X) * dst.W / src.W, dst.Y + (sy - src.Y) * dst.H / src.H);

        public static string Check()
        {
            var r1 = Fit(1280, 720, 1920, 1080, 0, false);
            var r2 = Fit(1280, 720, 2560, 1440, 0, true);
            var r3 = Fit(1280, 720, 1920, 1080, 1, false);
            var r4 = Fit(1280, 720, 2560, 1440, 0, false);
            var r5 = Fit(800, 600, 1920, 1080, 0, true);
            var (mx, my) = Map(640 + 100, 360 + 50, new Rect(100, 50, 1280, 720), r1);
            bool ok = r1 == new Rect(0, 0, 1920, 1080) && r2 == new Rect(0, 0, 2560, 1440)
                   && r3 == new Rect(240, 0, 1440, 1080) && r4 == new Rect(0, 0, 2560, 1440)
                   && r5 == new Rect(560, 240, 800, 600) && Math.Abs(mx - 960) < 1e-6 && Math.Abs(my - 540) < 1e-6;
            return ok ? "scale math: ok" : $"scale math: FAIL {r1} | {r2} | {r3} | {r4} | {r5} | {mx},{my}";
        }
    }
}
