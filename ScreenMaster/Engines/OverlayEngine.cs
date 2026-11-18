using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenMaster.Core;
using ScreenMaster.Native;

namespace ScreenMaster.Engines
{
    /// <summary>
    /// Click-through, topmost layered windows (one "fill" + one "grain" per monitor) on a dedicated
    /// thread with its own message loop. They never read the screen; they only blend on top of it.
    /// </summary>
    public sealed class OverlayEngine
    {
        sealed record MonitorConfig(MonitorInfo Monitor, List<Layer> Fills, Layer? Grain);
        sealed record Config(List<MonitorConfig> Monitors, bool Hide);

        sealed class LayeredSurface : IDisposable
        {
            public IntPtr Hwnd, MemDc, Bitmap, OldBitmap, Bits;
            public int X, Y, W, H;
            public bool Shown;

            public unsafe Span<uint> Pixels => new((void*)Bits, W * H);

            public void Present()
            {
                var pt = new POINT { X = X, Y = Y };
                var sz = new SIZE { cx = W, cy = H };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };
                Win32.UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref pt, ref sz, MemDc, ref src, 0, ref blend, Win32.ULW_ALPHA);
                if (!Shown)
                {
                    Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
                    Shown = true;
                }
            }

            public void Dispose()
            {
                if (MemDc != IntPtr.Zero) { Win32.SelectObject(MemDc, OldBitmap); Win32.DeleteDC(MemDc); }
                if (Bitmap != IntPtr.Zero) Win32.DeleteObject(Bitmap);
                if (Hwnd != IntPtr.Zero) Win32.DestroyWindow(Hwnd);
                MemDc = Bitmap = Hwnd = IntPtr.Zero;
            }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        sealed class GrainState
        {
            public LayeredSurface Surface = null!;
            public uint[][] Tiles = Array.Empty<uint[]>();
            public string Key = "";
            public double Fps;
            public double NextTick;
            public readonly Random Rng = new();
        }

        const int Tile = 256;
        const string ClassName = "ScreenMasterOverlay";
        static readonly WndProc _wndProc = (h, m, w, l) => Win32.DefWindowProc(h, m, w, l);

        Thread? _thread;
        uint _threadId;
        readonly object _lock = new();
        Config? _pending;
        volatile bool _stop;
        readonly ManualResetEventSlim _started = new();

        readonly Dictionary<string, LayeredSurface> _fills = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _fillKeys = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, GrainState> _grains = new(StringComparer.OrdinalIgnoreCase);
        bool _hide;
        public string CaptureStatus { get; private set; } = "";

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "ScreenMaster overlay" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.Wait(3000);
        }

        public void Update(List<MonitorInfo> monitors, List<Layer> layers, bool hideFromCapture)
        {
            var list = new List<MonitorConfig>();
            foreach (var m in monitors)
            {
                var fills = layers.Where(l => l.Enabled && l.AppliesTo(m.DeviceName) && l.Opacity > 0 &&
                                              (l.Kind is LayerKind.SolidColor or LayerKind.Gradient or LayerKind.Vignette or LayerKind.Guides or LayerKind.Pattern))
                                  .Select(l => l.Clone()).ToList();
                var grain = layers.LastOrDefault(l => l.Enabled && l.AppliesTo(m.DeviceName) && l.Kind == LayerKind.Grain
                                                      && l.Get("amount") > 0 && l.Opacity > 0)?.Clone();
                list.Add(new MonitorConfig(m, fills, grain));
            }
            Post(new Config(list, hideFromCapture));
        }

        public void Clear() => Post(new Config(new List<MonitorConfig>(), _hide));

        void Post(Config c)
        {
            lock (_lock) _pending = c;
            if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        public void Stop()
        {
            _stop = true;
            if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_APP, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(2000);
        }

        // ------------------------------------------------------------------ thread

        void Run()
        {
            _threadId = Win32.GetCurrentThreadId();
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = Win32.GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            Win32.RegisterClassEx(ref wc);
            // Force message queue creation before signalling.
            Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _started.Set();

            var sw = Stopwatch.StartNew();
            long nextTopmost = 0;
            bool fineTimer = false;
            while (!_stop)
            {
                // fast grain (above ~30 fps) needs 1 ms timer resolution to keep its pace; released when it stops
                bool needFine = _grains.Values.Any(g => g.Fps > 30);
                if (needFine != fineTimer) { if (needFine) timeBeginPeriod(1); else timeEndPeriod(1); fineTimer = needFine; }
                double nowMs = sw.Elapsed.TotalMilliseconds;
                double wait = 1000;
                foreach (var g in _grains.Values)
                    if (g.Fps > 0) wait = Math.Min(wait, Math.Max(0, g.NextTick - nowMs));
                Win32.MsgWaitForMultipleObjects(0, null, false, (uint)Math.Floor(wait), Win32.QS_ALLINPUT);

                while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
                {
                    if (msg.message == Win32.WM_QUIT) { _stop = true; break; }
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
                if (_stop) break;

                Config? cfg;
                lock (_lock) { cfg = _pending; _pending = null; }
                if (cfg != null)
                {
                    try { ApplyConfig(cfg); }
                    catch (Exception ex) { Log.Write("overlay apply: " + ex); }
                }

                long now = sw.ElapsedMilliseconds;
                nowMs = sw.Elapsed.TotalMilliseconds;
                foreach (var g in _grains.Values)
                {
                    if (g.Fps <= 0 || nowMs < g.NextTick - 0.5) continue;
                    double period = 1000 / g.Fps;
                    g.NextTick += period;
                    if (nowMs - g.NextTick > period) g.NextTick = nowMs + period; // fell behind: don't burst to catch up
                    try { RenderGrainFrame(g); g.Surface.Present(); }
                    catch (Exception ex) { Log.Write("grain frame: " + ex.Message); }
                }

                if (now >= nextTopmost)
                {
                    nextTopmost = now + 2000;
                    foreach (var s in _fills.Values.Concat(_grains.Values.Select(g => g.Surface)))
                        Win32.SetWindowPos(s.Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                }
            }

            foreach (var s in _fills.Values) s.Dispose();
            foreach (var g in _grains.Values) g.Surface.Dispose();
            _fills.Clear(); _grains.Clear();
        }

        LayeredSurface CreateSurface(MonitorInfo m, string tag)
        {
            var b = m.Bounds;
            var s = new LayeredSurface { X = b.Left, Y = b.Top, W = b.Width, H = b.Height };
            s.Hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                ClassName, "ScreenMaster " + tag, Win32.WS_POPUP, s.X, s.Y, s.W, s.H,
                IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
            if (s.Hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");

            var screen = Win32.GetDC(IntPtr.Zero);
            s.MemDc = Win32.CreateCompatibleDC(screen);
            Win32.ReleaseDC(IntPtr.Zero, screen);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = s.W, biHeight = -s.H,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            s.Bitmap = Win32.CreateDIBSection(s.MemDc, ref bmi, 0, out s.Bits, IntPtr.Zero, 0);
            if (s.Bitmap == IntPtr.Zero) { s.Dispose(); throw new InvalidOperationException("CreateDIBSection failed"); }
            s.OldBitmap = Win32.SelectObject(s.MemDc, s.Bitmap);
            SetAffinity(s.Hwnd);
            return s;
        }

        void SetAffinity(IntPtr hwnd)
        {
            bool ok = Win32.SetWindowDisplayAffinity(hwnd, _hide ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
            CaptureStatus = ok ? "" : $"Não foi possível esconder dos prints ({Marshal.GetLastWin32Error()})";
            if (!ok) Log.Write("SetWindowDisplayAffinity: " + CaptureStatus);
        }

        static bool SameBounds(LayeredSurface s, MonitorInfo m)
            => s.X == m.Bounds.Left && s.Y == m.Bounds.Top && s.W == m.Bounds.Width && s.H == m.Bounds.Height;

        void ApplyConfig(Config cfg)
        {
            bool hideChanged = cfg.Hide != _hide;
            _hide = cfg.Hide;
            var wanted = cfg.Monitors.ToDictionary(m => m.Monitor.DeviceName, StringComparer.OrdinalIgnoreCase);

            // ---- fills
            foreach (var dev in _fills.Keys.ToList())
                if (!wanted.TryGetValue(dev, out var mc) || mc.Fills.Count == 0 || !SameBounds(_fills[dev], mc.Monitor))
                {
                    _fills[dev].Dispose(); _fills.Remove(dev); _fillKeys.Remove(dev);
                }
            foreach (var mc in cfg.Monitors.Where(m => m.Fills.Count > 0))
            {
                var dev = mc.Monitor.DeviceName;
                var key = System.Text.Json.JsonSerializer.Serialize(mc.Fills.Select(f => new { f.Kind, f.Opacity, f.P, f.S }));
                if (!_fills.TryGetValue(dev, out var s)) { s = CreateSurface(mc.Monitor, "fill"); _fills[dev] = s; }
                else if (hideChanged) SetAffinity(s.Hwnd);
                if (_fillKeys.TryGetValue(dev, out var old) && old == key) continue;
                RenderFills(s, mc.Fills);
                s.Present();
                _fillKeys[dev] = key;
            }

            // ---- grain
            foreach (var dev in _grains.Keys.ToList())
                if (!wanted.TryGetValue(dev, out var mc) || mc.Grain == null || !SameBounds(_grains[dev].Surface, mc.Monitor))
                {
                    _grains[dev].Surface.Dispose(); _grains.Remove(dev);
                }
            foreach (var mc in cfg.Monitors.Where(m => m.Grain != null))
            {
                var dev = mc.Monitor.DeviceName;
                var gl = mc.Grain!;
                if (!_grains.TryGetValue(dev, out var g))
                {
                    g = new GrainState { Surface = CreateSurface(mc.Monitor, "grain") };
                    _grains[dev] = g;
                }
                else if (hideChanged) SetAffinity(g.Surface.Hwnd);

                double amount = gl.Get("amount") * Math.Clamp(gl.Opacity / 100.0, 0, 1);
                int size = Math.Clamp((int)Math.Round(gl.Get("size")), 1, 4);
                bool color = gl.Get("color") > 0.5;
                var key = $"{amount:F2}|{size}|{color}";
                // never faster than this monitor can show (a 60 Hz screen gets at most 60 even if the layer asks 144)
                g.Fps = Math.Clamp(gl.Get("fps"), 0, Native.DisplayModes.RefreshOf(dev));
                if (g.Key != key)
                {
                    g.Tiles = BuildTiles(amount, size, color, g.Fps > 0 ? 6 : 1);
                    g.Key = key;
                    RenderGrainFrame(g);
                    g.Surface.Present();
                }
                else if (!g.Surface.Shown) { RenderGrainFrame(g); g.Surface.Present(); }
            }
        }

        // ------------------------------------------------------------------ rendering

        static uint Pack(double r, double g, double b, double a)
        {
            // premultiplied BGRA
            uint A = (uint)Math.Clamp(Math.Round(a * 255), 0, 255);
            uint R = (uint)Math.Clamp(Math.Round(r * 255), 0, A);
            uint G = (uint)Math.Clamp(Math.Round(g * 255), 0, A);
            uint B = (uint)Math.Clamp(Math.Round(b * 255), 0, A);
            return (A << 24) | (R << 16) | (G << 8) | B;
        }

        static unsafe void RenderFills(LayeredSurface s, List<Layer> fills)
        {
            int W = s.W, H = s.H;
            uint* px = (uint*)s.Bits;
            var layers = fills.Select(f => new FillParams(f, W, H)).ToArray();
            Parallel.For(0, H, y =>
            {
                uint* row = px + (long)y * W;
                for (int x = 0; x < W; x++)
                {
                    // accumulate premultiplied "over"
                    double r = 0, g = 0, b = 0, a = 0;
                    foreach (var f in layers)
                    {
                        f.Sample(x, y, out var sr, out var sg, out var sb, out var sa);
                        if (sa <= 0) continue;
                        r = sr * sa + r * (1 - sa);
                        g = sg * sa + g * (1 - sa);
                        b = sb * sa + b * (1 - sa);
                        a = sa + a * (1 - sa);
                    }
                    row[x] = Pack(r, g, b, a);
                }
            });
        }

        sealed class FillParams
        {
            readonly LayerKind _k;
            double _op, _r1, _g1, _b1, _a1, _r2, _g2, _b2, _a2, _dx, _dy, _min, _span;
            readonly double _cx, _cy, _inner, _outer, _amount;

            public FillParams(Layer l, int w, int h)
            {
                _k = l.Kind;
                _op = Math.Clamp(l.Opacity / 100.0, 0, 1);
                switch (_k)
                {
                    case LayerKind.SolidColor:
                        (_r1, _g1, _b1) = ColorMath.ParseHex(l.GetS("color"));
                        break;
                    case LayerKind.Gradient:
                    {
                        (_r1, _g1, _b1) = ColorMath.ParseHex(l.GetS("color1"));
                        (_r2, _g2, _b2) = ColorMath.ParseHex(l.GetS("color2"));
                        _a1 = l.Get("alpha1") / 100; _a2 = l.Get("alpha2") / 100;
                        double ang = l.Get("angle") * Math.PI / 180;
                        // Photoshop: 90° = bottom → top
                        _dx = Math.Cos(ang); _dy = -Math.Sin(ang);
                        double[] proj = { 0, _dx * w, _dy * h, _dx * w + _dy * h };
                        _min = proj.Min(); _span = Math.Max(1, proj.Max() - _min);
                        break;
                    }
                    case LayerKind.Vignette:
                    {
                        _cx = w / 2.0; _cy = h / 2.0;
                        _amount = l.Get("amount") / 100;
                        double size = l.Get("size") / 100, feather = Math.Max(0.01, l.Get("feather") / 100);
                        _inner = size * 0.9;
                        _outer = _inner + feather * 0.8;
                        break;
                    }
                    case LayerKind.Guides:
                        InitGuides(l, w, h);
                        break;
                    case LayerKind.Pattern:
                        (_r1, _g1, _b1) = ColorMath.ParseHex(l.GetS("color"));
                        _mode = (int)l.Get("type");
                        _grid = Math.Max(2, (int)l.Get("size"));
                        _gutter = Math.Clamp((int)l.Get("thickness"), 1, _grid - 1);
                        break;
                }
            }

            public void Sample(int x, int y, out double r, out double g, out double b, out double a)
            {
                switch (_k)
                {
                    case LayerKind.SolidColor:
                        r = _r1; g = _g1; b = _b1; a = _op; return;
                    case LayerKind.Gradient:
                    {
                        double t = ((x * _dx + y * _dy) - _min) / _span;
                        r = _r1 + (_r2 - _r1) * t; g = _g1 + (_g2 - _g1) * t; b = _b1 + (_b2 - _b1) * t;
                        a = (_a1 + (_a2 - _a1) * t) * _op;
                        return;
                    }
                    case LayerKind.Vignette:
                    {
                        double nx = (x - _cx) / _cx, ny = (y - _cy) / _cy;
                        double d = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
                        double t = Math.Clamp((d - _inner) / (_outer - _inner), 0, 1);
                        t = t * t * (3 - 2 * t);
                        r = g = b = 0; a = t * _amount * _op;
                        return;
                    }
                    case LayerKind.Guides:
                    {
                        r = _r1; g = _g1; b = _b1;
                        a = GuideAlpha(x, y) * _op;
                        return;
                    }
                    case LayerKind.Pattern:
                    {
                        r = _r1; g = _g1; b = _b1;
                        a = PatternAlpha(x, y) * _op;
                        return;
                    }
                }
                r = g = b = a = 0;
            }

            // ---- guides (design overlay): columns, grid, center, thirds
            int _mode, _w, _h, _cols, _gutter, _margin, _grid;
            bool _fill;
            double _colW;

            public void InitGuides(Layer l, int w, int h)
            {
                (_r1, _g1, _b1) = ColorMath.ParseHex(l.GetS("color"));
                _mode = (int)l.Get("mode"); _w = w; _h = h;
                _cols = Math.Max(1, (int)l.Get("columns"));
                _gutter = (int)l.Get("gutter"); _margin = (int)l.Get("margin"); _grid = Math.Max(2, (int)l.Get("grid"));
                _fill = l.Get("fill") > 0.5;
                _colW = Math.Max(1, (w - 2.0 * _margin - _gutter * (_cols - 1)) / _cols);
            }

            /// <summary>Procedural tile: _grid = cell size, _gutter = line/dot thickness.</summary>
            double PatternAlpha(int x, int y)
            {
                int n = _grid, t = _gutter, px = x % n, py = y % n;
                switch (_mode)
                {
                    case 0: // dots: a disc of diameter ~thickness+1 in each cell
                    {
                        double c = (n - 1) / 2.0, rad = Math.Max(0.6, t / 2.0 + 0.5);
                        double dx = px - c, dy = py - c, d = Math.Sqrt(dx * dx + dy * dy);
                        return Math.Clamp(rad - d + 0.5, 0, 1);
                    }
                    case 1: return px < t ? 1 : 0;                         // vertical lines
                    case 2: return ((x / n) + (y / n)) % 2 == 0 ? 1 : 0;   // checker
                    case 3: return py < t ? 1 : 0;                         // scanlines
                    case 4: return px < t || py < t ? 1 : 0;               // crosshatch
                    default: return ((x + y) % n) < t ? 1 : 0;             // diagonals
                }
            }

            double GuideAlpha(int x, int y)
            {
                switch (_mode)
                {
                    case 0: // columns
                    {
                        if (x < _margin || x >= _w - _margin) return 0;
                        double pos = (x - _margin) % (_colW + _gutter);
                        bool inCol = pos < _colW;
                        if (_fill) return inCol ? 1 : 0;
                        return inCol && (pos < 1 || pos > _colW - 1) ? 1 : 0; // column edges only
                    }
                    case 1: // grid: every Nth pixel, stronger every 8 cells
                    {
                        bool lx = x % _grid == 0, ly = y % _grid == 0;
                        if (!lx && !ly) return 0;
                        bool major = (lx && x % (_grid * 8) == 0) || (ly && y % (_grid * 8) == 0);
                        return major ? 1 : 0.45;
                    }
                    case 2: // center cross
                        return Math.Abs(x - _w / 2) < 1 || Math.Abs(y - _h / 2) < 1 ? 1 : 0;
                    default: // thirds
                        return x == _w / 3 || x == 2 * _w / 3 || y == _h / 3 || y == 2 * _h / 3 ? 1 : 0;
                }
            }
        }

        static uint[][] BuildTiles(double amount, int size, bool color, int count)
        {
            var rng = new Random();
            double amp = amount / 100.0 * 0.6;
            var tiles = new uint[count][];
            int n = Tile / size; // noise grid resolution
            for (int t = 0; t < count; t++)
            {
                var noise = new double[4][]; // lum + r,g,b
                for (int k = 0; k < (color ? 4 : 1); k++)
                {
                    noise[k] = new double[n * n];
                    for (int i = 0; i < n * n; i++)
                        noise[k][i] = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) / 1.5; // ~gaussian, [-1,1]
                }
                var tile = new uint[Tile * Tile];
                for (int y = 0; y < Tile; y++)
                    for (int x = 0; x < Tile; x++)
                    {
                        double v = SampleBilinear(noise[0], n, x, y, size);
                        double a = Math.Min(1, Math.Abs(v) * amp);
                        if (!color)
                        {
                            tile[y * Tile + x] = v > 0 ? Pack(a, a, a, a) : Pack(0, 0, 0, a);
                        }
                        else
                        {
                            double cr = 0.5 + 0.5 * SampleBilinear(noise[1], n, x, y, size);
                            double cg = 0.5 + 0.5 * SampleBilinear(noise[2], n, x, y, size);
                            double cb = 0.5 + 0.5 * SampleBilinear(noise[3], n, x, y, size);
                            double baseV = v > 0 ? 1 : 0;
                            double mix = 0.6;
                            tile[y * Tile + x] = Pack(a * (baseV * (1 - mix) + cr * mix), a * (baseV * (1 - mix) + cg * mix),
                                                      a * (baseV * (1 - mix) + cb * mix), a);
                        }
                    }
                tiles[t] = tile;
            }
            return tiles;
        }

        static double SampleBilinear(double[] grid, int n, int x, int y, int size)
        {
            if (size == 1) return grid[y * n + x];
            double fx = (double)x / size, fy = (double)y / size;
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = (x0 + 1) % n, y1 = (y0 + 1) % n;
            double tx = fx - x0, ty = fy - y0;
            double a = grid[y0 * n + x0] * (1 - tx) + grid[y0 * n + x1] * tx;
            double b = grid[y1 * n + x0] * (1 - tx) + grid[y1 * n + x1] * tx;
            return a * (1 - ty) + b * ty;
        }

        static void RenderGrainFrame(GrainState g)
        {
            // Every 256x256 cell of the screen gets its own random tile + offset, so there is no visible repetition.
            var s = g.Surface;
            int W = s.W, H = s.H;
            int cols = (W + Tile - 1) / Tile, rows = (H + Tile - 1) / Tile;
            var cellTile = new int[rows * cols];
            var cellOx = new int[rows * cols];
            var cellOy = new int[rows * cols];
            for (int i = 0; i < cellTile.Length; i++)
            {
                cellTile[i] = g.Rng.Next(g.Tiles.Length);
                cellOx[i] = g.Rng.Next(Tile);
                cellOy[i] = g.Rng.Next(Tile);
            }
            var px = s.Pixels;
            for (int y = 0; y < H; y++)
            {
                int band = y / Tile, ly = y % Tile;
                var dstRow = px.Slice(y * W, W);
                for (int c = 0; c < cols; c++)
                {
                    int ci = band * cols + c;
                    var tile = g.Tiles[cellTile[ci]].AsSpan();
                    var src = tile.Slice(((ly + cellOy[ci]) % Tile) * Tile, Tile);
                    int x0 = c * Tile, len = Math.Min(Tile, W - x0), ox = cellOx[ci];
                    int first = Math.Min(Tile - ox, len);
                    src.Slice(ox, first).CopyTo(dstRow.Slice(x0));
                    if (first < len) src.Slice(0, len - first).CopyTo(dstRow.Slice(x0 + first));
                }
            }
        }
    }
}
