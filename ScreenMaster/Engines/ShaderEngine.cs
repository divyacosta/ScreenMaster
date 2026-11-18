using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenMaster.Core;
using ScreenMaster.Native;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenMaster.Engines
{
    /// <summary>
    /// Experimental "shader mode": per monitor, captures the desktop (Desktop Duplication), runs a pixel
    /// shader over it and presents the result in a click-through topmost window that is excluded from
    /// capture (so it never sees itself). Used by Pesquisa de cores (3D LUT), Cor seletiva and Nitidez.
    ///
    /// Safety: any capture failure (UAC/secure desktop, mode change, exclusive fullscreen, HDR) hides the
    /// window instead of leaving a frozen frame on top; a watchdog hides it if a worker stops beating.
    /// </summary>
    public sealed class ShaderEngine
    {
        /// <summary>Shader layers for one monitor, bottom to top of the stack: each one is a pass over the previous result.</summary>
        public sealed record Params(List<Layer> Passes, ScaleSpec? Scale = null);

        readonly Dictionary<Guid, double> _timings = new();
        /// <summary>GPU time per shader layer (ms, worst monitor), measured with timestamp queries.</summary>
        public Dictionary<Guid, double> TimingsSnapshot() { lock (_lock) return new(_timings); }
        internal void SetTiming(Guid id, double ms) { lock (_lock) _timings[id] = _timings.TryGetValue(id, out var o) ? Math.Max(ms, o * 0.8 + ms * 0.2) : ms; }

        readonly Dictionary<string, Worker> _workers = new(StringComparer.OrdinalIgnoreCase);
        readonly object _lock = new();
        Timer? _watchdog;

        readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per monitor: why shader mode is not showing there (copy, safe on any thread).</summary>
        public Dictionary<string, string> ErrorsSnapshot() { lock (_lock) return new(_errors, StringComparer.OrdinalIgnoreCase); }
        public string ErrorOf(string dev) { lock (_lock) return _errors.GetValueOrDefault(dev) ?? ""; }

        public bool Active { get { lock (_lock) return _workers.Count > 0; } }

        /// <summary>This monitor's shader window is up and showing frames.</summary>
        public bool IsShowing(string dev) { lock (_lock) return _workers.TryGetValue(dev, out var w) && w.Visible; }

        public void Update(List<MonitorInfo> monitors, List<Layer> layers, ScaleSpec? scale = null)
        {
            lock (_lock)
            {
                var wanted = new Dictionary<string, (MonitorInfo m, Params p)>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in monitors)
                {
                    var passes = layers.Where(l => l.Enabled && l.Opacity > 0 && l.AppliesTo(m.DeviceName)
                                                   && LayerDefs.Get(l.Kind).Engine == EngineKind.Shader
                                                   && (l.Kind != LayerKind.ColorLookup || File.Exists(l.GetS("file"))))
                                       .Select(l => l.CloneKeepId()).ToList();
                    var sc = scale != null && string.Equals(scale.Device, m.DeviceName, StringComparison.OrdinalIgnoreCase) ? scale : null;
                    if (passes.Count > 0 || sc != null) wanted[m.DeviceName] = (m, new Params(passes, sc));
                }
                foreach (var dev in _workers.Keys.ToList())
                    if (!wanted.TryGetValue(dev, out var w) || !_workers[dev].SameBounds(w.m))
                    {
                        _workers[dev].Stop();
                        _workers.Remove(dev);
                        _errors.Remove(dev);
                    }
                foreach (var (dev, w) in wanted)
                {
                    if (!_workers.TryGetValue(dev, out var wk))
                    {
                        wk = new Worker(w.m, this);
                        _workers[dev] = wk;
                        wk.Start();
                    }
                    wk.Post(w.p);
                }
                if (_workers.Count > 0 && _watchdog == null)
                    _watchdog = new Timer(_ => Watch(), null, 500, 500);
                else if (_workers.Count == 0 && _watchdog != null) { _watchdog.Dispose(); _watchdog = null; }
                var live = wanted.Values.SelectMany(w => w.p.Passes).Select(l => l.Id).ToHashSet();
                foreach (var id in _timings.Keys.Where(id => !live.Contains(id)).ToList()) _timings.Remove(id);
            }
        }

        public void Clear() => Update(new List<MonitorInfo>(), new List<Layer>());

        public void Stop() => Clear();

        void Watch()
        {
            List<Worker> list;
            lock (_lock) list = _workers.Values.ToList();
            long now = Environment.TickCount64;
            foreach (var w in list)
                if (w.Visible && now - w.Heartbeat > 1500) w.HideFromOtherThread("watchdog: sem resposta");
        }

        internal void SetError(string dev, string msg)
        {
            lock (_lock) { if (msg.Length == 0) _errors.Remove(dev); else _errors[dev] = msg; }
        }

        /// <summary>--shadercheck: compiles the HLSL and parses a generated .cube, without touching the screen.</summary>
        public static string Check()
        {
            var sb = new System.Text.StringBuilder();
            try { NisCoefficients(); var all = new[] { "VS", "PS_Cursor", "PS_FsrRcas", "CS_Nis", "PS_Crop" }.Concat(ShaderGraphs.All.SelectMany(g => g.Passes.Select(p => p.Entry))).Concat(ScaleMath.FilterEntries).Concat(PassEntries.Values).ToList(); foreach (var e in all) Bytecode(e); sb.AppendLine("hlsl: ok (" + all.Count + " shaders)"); }
            catch (Exception ex) { sb.AppendLine("hlsl: FAIL " + ex.Message); }
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "sm_check.cube");
                var lines = new List<string> { "TITLE \"Teste\"", "LUT_3D_SIZE 2" };
                for (int b = 0; b < 2; b++) for (int g = 0; g < 2; g++) for (int r = 0; r < 2; r++) lines.Add($"{r} {g} {b}");
                File.WriteAllLines(tmp, lines);
                var lut = LoadCube(tmp);
                sb.AppendLine(lut.Size == 2 && lut.Title == "Teste" && lut.Rgba[4] == 1 && lut.Rgba[5] == 0 ? "cube: ok" : "cube: FAIL valores");
                File.Delete(tmp);
            }
            catch (Exception ex) { sb.AppendLine("cube: FAIL " + ex.Message); }
            foreach (var k in Enum.GetValues<LayerKind>())
            {
                try
                {
                    var d = LayerDefs.Get(k); var l = Layer.Create(k); Categories.Of(k); UI.Ui.Resource(k);
                    HelpTexts.EngineFacts(d.Engine, false);
                    if (!HelpTexts.Layer.ContainsKey(k)) sb.AppendLine($"kind {k}: sem texto de ajuda");
                    foreach (var p in d.Params) l.Get(p.Key);
                }
                catch (Exception ex) { sb.AppendLine($"kind {k}: FAIL {ex.GetType().Name} {ex.Message}"); }
            }
            sb.AppendLine("kinds: checked " + Enum.GetValues<LayerKind>().Length);
            sb.AppendLine(ScaleMath.Check());
            try { sb.AppendLine(OffscreenTest()); }
            catch (Exception ex) { sb.AppendLine("passes: FAIL " + ex.Message); }
            return sb.ToString();
        }

        /// <summary>Runs every pass with neutral settings on a small offscreen image: output must equal input.</summary>
        static unsafe string OffscreenTest()
        {
            const int W = 8, H = 8;
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
            using (dev) using (ctx)
            {
                var px = new uint[W * H];
                for (int i = 0; i < px.Length; i++) { int x = i % W, y = i / W; px[i] = 0xFF000000u | (uint)(x * 255 / (W - 1)) << 16 | (uint)(y * 255 / (H - 1)) << 8 | (uint)((x + y) * 255 / (W + H - 2)); }
                Texture2DDescription D(BindFlags b, ResourceUsage u = ResourceUsage.Default, CpuAccessFlags c = CpuAccessFlags.None) => new()
                { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = u, BindFlags = b, CPUAccessFlags = c };
                ID3D11Texture2D src;
                fixed (uint* p = px) src = dev.CreateTexture2D(D(BindFlags.ShaderResource), new[] { new SubresourceData((IntPtr)p, W * 4) });
                using (src)
                using (var srv = dev.CreateShaderResourceView(src))
                using (var dst = dev.CreateTexture2D(D(BindFlags.RenderTarget)))
                using (var rtv = dev.CreateRenderTargetView(dst))
                using (var stage = dev.CreateTexture2D(D(BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)))
                using (var vs = dev.CreateVertexShader(Bytecode("VS")))
                using (var cb = dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default)))
                using (var samp = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp)))
                using (var lin = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp)))
                {
                    var ident = new float[] { 0,0,0,1, 1,0,0,1, 0,1,0,1, 1,1,0,1, 0,0,1,1, 1,0,1,1, 0,1,1,1, 1,1,1,1 };
                    ID3D11Texture3D lutTex;
                    fixed (float* f = ident)
                        lutTex = dev.CreateTexture3D(new Texture3DDescription { Width = 2, Height = 2, Depth = 2, MipLevels = 1, Format = Format.R32G32B32A32_Float, Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource },
                                                     new[] { new SubresourceData((IntPtr)f, 2 * 16, 4 * 16) });
                    using (lutTex)
                    using (var lutView = dev.CreateShaderResourceView(lutTex))
                    {
                        var res = new List<string>();
                        foreach (var (kind, entry) in PassEntries)
                        {
                            var l = Layer.Create(kind);
                            if (kind == LayerKind.Sharpen) l.P["sharpen"] = 0;
                            using var ps = dev.CreatePixelShader(Bytecode(entry));
                            ctx.UpdateSubresource(ConstantsFor(l, W, H, 2), cb);
                            ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                            ctx.OMSetRenderTargets(rtv);
                            ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, W, H));
                            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                            ctx.VSSetShader(vs); ctx.PSSetShader(ps);
                            ctx.PSSetConstantBuffer(0, cb);
                            ctx.PSSetSamplers(0, new[] { samp, lin });
                            ctx.PSSetShaderResources(0, new[] { srv, lutView });
                            ctx.Draw(3, 0);
                            ctx.CopyResource(stage, dst);
                            var map = ctx.Map(stage, 0, MapMode.Read);
                            int worst = 0;
                            for (int y = 0; y < H; y++)
                                for (int x = 0; x < W; x++)
                                {
                                    uint o = ((uint*)map.DataPointer)[y * (map.RowPitch / 4) + x], e = px[y * W + x];
                                    for (int c = 0; c < 24; c += 8) worst = Math.Max(worst, Math.Abs((int)((o >> c) & 0xFF) - (int)((e >> c) & 0xFF)));
                                }
                            ctx.Unmap(stage, 0);
                            res.Add($"{kind}={(worst <= 1 ? "ok" : "FAIL diff " + worst)}");
                        }
                        // scale filters at 1:1 must reproduce the image (small differences are normal for EASU/SGSR)
                        var sres = new List<string>();
                        for (int f = 0; f < ScaleMath.FilterEntries.Length; f++)
                        {
                            using var ps = dev.CreatePixelShader(Bytecode(ScaleMath.FilterEntries[f]));
                            var c = new Constants
                            {
                                Texel = new Vector4(1f / W, 1f / H, 0, 1), A = new Vector4(0, 0, 1, 1), S0 = new Vector4(0, 0, W, H),
                                S1 = new Vector4(1, 1, 0, 0), S2 = new Vector4(1f / W, 1f / H, 1f / W, -1f / H), S3 = new Vector4(-1f / W, 2f / H, 1f / W, 2f / H), S4 = new Vector4(0, 4f / H, 0, 0),
                            };
                            ctx.UpdateSubresource(c, cb);
                            ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                            ctx.OMSetRenderTargets(rtv);
                            ctx.PSSetShader(ps);
                            ctx.PSSetShaderResources(0, new[] { srv, lutView });
                            ctx.Draw(3, 0);
                            ctx.CopyResource(stage, dst);
                            var map = ctx.Map(stage, 0, MapMode.Read);
                            double sum = 0; int n = 0;
                            for (int y = 1; y < H - 1; y++)
                                for (int x = 1; x < W - 1; x++)
                                {
                                    uint o = ((uint*)map.DataPointer)[y * (map.RowPitch / 4) + x], e = px[y * W + x];
                                    for (int cc = 0; cc < 24; cc += 8) { sum += Math.Abs((int)((o >> cc) & 0xFF) - (int)((e >> cc) & 0xFF)); n++; }
                                }
                            ctx.Unmap(stage, 0);
                            double avg = sum / n;
                            sres.Add($"{ScaleMath.FilterEntries[f]}={(avg <= 6 ? "ok" : "FAIL")}({avg:0.0})");
                        }
                        // NVIDIA NIS at 1:1 with no sharpening must also reproduce the image
                        string nisStep = "coef";
                        try
                        {
                            var (cs, cu) = NisCoefficients();
                            ID3D11Texture2D Coef(float[] v) { fixed (float* d = v) return dev.CreateTexture2D(new Texture2DDescription { Width = 2, Height = 64, MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource }, new[] { new SubresourceData((IntPtr)d, 32) }); }
                            using var ts = Coef(cs); using var tu = Coef(cu); nisStep = "views";
                            using var vs2 = dev.CreateShaderResourceView(ts); using var vu = dev.CreateShaderResourceView(tu);
                            nisStep = "out"; using var outT = dev.CreateTexture2D(new Texture2DDescription { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.UnorderedAccess });
                            nisStep = "uav"; using var uav = dev.CreateUnorderedAccessView(outT); nisStep = "staging";
                            using var st16 = dev.CreateTexture2D(new Texture2DDescription { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
                            nisStep = "cs"; using var csh = dev.CreateComputeShader(Bytecode("CS_Nis")); nisStep = "cb";
                            using var cbn = dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<NisConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
                            nisStep = "run"; ctx.UpdateSubresource(NisConfig(0, 0, 0, W, H, W, H, W, H), cbn);
                            ctx.OMSetRenderTargets(new ID3D11RenderTargetView[] { null! });
                            ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                            ctx.CSSetShader(csh); ctx.CSSetConstantBuffer(0, cbn); ctx.CSSetSampler(0, lin);
                            ctx.CSSetShaderResources(0, new[] { srv, vs2, vu });
                            ctx.CSSetUnorderedAccessView(0, uav);
                            ctx.Dispatch(1, 1, 1);
                            ctx.CSSetUnorderedAccessView(0, null!);
                            ctx.CopyResource(st16, outT);
                            var map = ctx.Map(st16, 0, MapMode.Read);
                            double sum = 0; int n = 0;
                            for (int y = 1; y < H - 1; y++)
                                for (int x = 1; x < W - 1; x++)
                                {
                                    var hp = (Half*)((byte*)map.DataPointer + y * map.RowPitch) + x * 4;
                                    uint e = px[y * W + x];
                                    float[] exp = { ((e >> 16) & 0xFF) / 255f, ((e >> 8) & 0xFF) / 255f, (e & 0xFF) / 255f };
                                    for (int c = 0; c < 3; c++) { sum += Math.Abs((float)hp[c] - exp[c]) * 255; n++; }
                                }
                            ctx.Unmap(st16, 0);
                            double avg = sum / n;
                            sres.Add($"CS_Nis={(avg <= 6 ? "ok" : "FAIL")}({avg:0.0})");
                        }
                        catch (Exception ex) { sres.Add($"CS_Nis=FAIL em {nisStep}: {ex.Message.Split((char)10)[0]}"); }
                        // multi-pass upscalers on a smooth image: OUT (2×) must stay close to the source
                        var gres = new List<string>();
                        foreach (var g in ShaderGraphs.All)
                        {
                            try
                            {
                                var texs = new Dictionary<string, (ID3D11Texture2D t, ID3D11ShaderResourceView v, ID3D11RenderTargetView r, int w, int h)>();
                                void Make(string name, int w, int h)
                                {
                                    var t = dev.CreateTexture2D(new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget });
                                    texs[name] = (t, dev.CreateShaderResourceView(t), dev.CreateRenderTargetView(t), w, h);
                                }
                                Make("SOURCE", W, H);
                                foreach (var gp in g.Passes) if (!texs.ContainsKey(gp.Output)) Make(gp.Output, (int)(W * gp.Scale), (int)(H * gp.Scale));
                                // SOURCE = the test image (copy through PS_Scale at 1:1)
                                using (var cp = dev.CreatePixelShader(Bytecode("PS_Scale")))
                                {
                                    ctx.UpdateSubresource(new Constants { Texel = new Vector4(1f / W, 1f / H, 0, 1), A = new Vector4(0, 0, 1, 1), S0 = new Vector4(0, 0, W, H) }, cb);
                                    ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                                    ctx.OMSetRenderTargets(texs["SOURCE"].r);
                                    ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, W, H));
                                    ctx.PSSetShader(cp);
                                    ctx.PSSetShaderResources(0, new[] { srv, lutView });
                                    ctx.Draw(3, 0);
                                }
                                using var gcb = dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<GraphConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
                                Vector4 Sz(int w, int h) => new(w, h, 1f / w, 1f / h);
                                foreach (var gp in g.Passes)
                                {
                                    using var ps = dev.CreatePixelShader(Bytecode(gp.Entry));
                                    var o = texs[gp.Output]; var ins = gp.Inputs.Select(n => texs[n]).ToArray();
                                    ctx.UpdateSubresource(new GraphConstants { OutSize = Sz(o.w, o.h), In0Size = Sz(ins[0].w, ins[0].h), SrcSize = Sz(W, H) }, gcb);
                                    ctx.PSSetConstantBuffer(0, gcb);
                                    ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null!, null! });
                                    ctx.OMSetRenderTargets(o.r);
                                    ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, o.w, o.h));
                                    ctx.PSSetShader(ps);
                                    ctx.PSSetShaderResources(0, ins.Select(x => x.v).ToArray());
                                    ctx.Draw(3, 0);
                                }
                                ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null!, null! });
                                ctx.PSSetConstantBuffer(0, cb);
                                var outT = texs["OUT"];
                                using var st = dev.CreateTexture2D(new Texture2DDescription { Width = (uint)outT.w, Height = (uint)outT.h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
                                ctx.CopyResource(st, outT.t);
                                var map = ctx.Map(st, 0, MapMode.Read);
                                double sum = 0; int n = 0; bool nan = false;
                                for (int y = 2; y < outT.h - 2; y++)
                                    for (int x = 2; x < outT.w - 2; x++)
                                    {
                                        var hp = (Half*)((byte*)map.DataPointer + y * map.RowPitch) + x * 4;
                                        // expected: the smooth gradient sampled at the same place
                                        double sx = (x + 0.5) / 2 - 0.5, sy = (y + 0.5) / 2 - 0.5;
                                        double[] e = { sx * 255 / (W - 1), sy * 255 / (H - 1), (sx + sy) * 255 / (W + H - 2) };
                                        for (int c = 0; c < 3; c++) { float v = (float)hp[c]; if (float.IsNaN(v)) nan = true; sum += Math.Abs(v * 255 - Math.Clamp(e[c], 0, 255)); n++; }
                                    }
                                ctx.Unmap(st, 0);
                                foreach (var t in texs.Values) { t.r.Dispose(); t.v.Dispose(); t.t.Dispose(); }
                                double avg = sum / n;
                                gres.Add($"{g.Name}={(nan ? "FAIL NaN" : avg <= 12 ? "ok" : "FAIL")}({avg:0.0})");
                            }
                            catch (Exception ex) { gres.Add($"{g.Name}=FAIL {ex.Message.Split((char)10)[0]}"); }
                        }
                        return "passes: " + string.Join(", ", res) + Environment.NewLine + "scale filters 1:1: " + string.Join(", ", sres)
                               + Environment.NewLine + "upscalers 2x: " + string.Join(", ", gres);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ LUT files

        public sealed record CubeLut(int Size, float[] Rgba, string Title);
        static readonly Dictionary<string, (DateTime stamp, CubeLut lut)> _cubeCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads an Adobe/Resolve .cube 3D LUT (LUT_3D_SIZE, DOMAIN_MIN/MAX, red fastest).</summary>
        public static CubeLut LoadCube(string path)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_cubeCache)
                if (_cubeCache.TryGetValue(path, out var c) && c.stamp == stamp) return c.lut;
            int n = 0; string title = Path.GetFileNameWithoutExtension(path);
            var min = new float[] { 0, 0, 0 }; var max = new float[] { 1, 1, 1 };
            var data = new List<float>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (char.IsLetter(line[0]))
                {
                    switch (parts[0].ToUpperInvariant())
                    {
                        case "TITLE": title = line.Substring(5).Trim().Trim('"'); break;
                        case "LUT_3D_SIZE": n = int.Parse(parts[1], CultureInfo.InvariantCulture); break;
                        case "LUT_1D_SIZE": throw new InvalidDataException("LUT 1D não é suportada (só 3D).");
                        case "DOMAIN_MIN": for (int i = 0; i < 3; i++) min[i] = float.Parse(parts[i + 1], CultureInfo.InvariantCulture); break;
                        case "DOMAIN_MAX": for (int i = 0; i < 3; i++) max[i] = float.Parse(parts[i + 1], CultureInfo.InvariantCulture); break;
                    }
                    continue;
                }
                if (parts.Length < 3) continue;
                for (int i = 0; i < 3; i++) data.Add(float.Parse(parts[i], CultureInfo.InvariantCulture));
            }
            if (n < 2 || n > 129) throw new InvalidDataException("Arquivo .cube sem LUT_3D_SIZE válido.");
            if (data.Count != n * n * n * 3) throw new InvalidDataException($"Esperava {n * n * n} cores, achei {data.Count / 3}.");
            var rgba = new float[n * n * n * 4];
            for (int i = 0; i < n * n * n; i++)
            {
                for (int c = 0; c < 3; c++)
                    rgba[i * 4 + c] = (data[i * 3 + c] - min[c]) / Math.Max(1e-6f, max[c] - min[c]);
                rgba[i * 4 + 3] = 1;
            }
            var lut = new CubeLut(n, rgba, title);
            lock (_cubeCache) _cubeCache[path] = (stamp, lut);
            return lut;
        }

        // ------------------------------------------------------------------ worker (one per monitor)

        /// <summary>Pixel shader entry point per shader layer kind (each layer = one pass).</summary>
        static readonly Dictionary<LayerKind, string> PassEntries = new()
        {
            [LayerKind.Sharpen] = "PS_Spatial",
            [LayerKind.ColorLookup] = "PS_Lut",
            [LayerKind.SelectiveColor] = "PS_Selective",
        };

        static string Vendor(string path)
        {
            var asm = typeof(ShaderEngine).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/').Equals("Vendor/" + path, StringComparison.OrdinalIgnoreCase))
                       ?? throw new FileNotFoundException("recurso embutido ausente: " + path);
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>Common declarations (cbuffer, textures, samplers, VS) shared by every shader source.</summary>
        static string Common => Hlsl.Substring(0, Hlsl.IndexOf("float Luma(", StringComparison.Ordinal));

        static string SourceFor(string entry) => entry switch
        {
            "PS_Easu" or "PS_FsrRcas" => Common + @"
#define A_GPU 1
#define A_HLSL 1
" + Vendor("fsr/ffx_a.h") + @"
#define FSR_EASU_F 1
#define FSR_RCAS_F 1
AF4 FsrEasuRF(AF2 p) { return Src.GatherRed(Pt, p); }
AF4 FsrEasuGF(AF2 p) { return Src.GatherGreen(Pt, p); }
AF4 FsrEasuBF(AF2 p) { return Src.GatherBlue(Pt, p); }
AF4 FsrRcasLoadF(ASU2 p) { return Src.Load(int3(p, 0)); }
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}
" + Vendor("fsr/ffx_fsr1.h") + @"
// SR[0] = destination rect (output px), SR[1..4] = FsrEasuConOffset constants
float4 PS_Easu(V i) : SV_Target
{
    float2 p = i.pos.xy - SR[0].xy;
    if (any(p < 0) || any(p >= SR[0].zw)) return float4(0, 0, 0, 1);
    AF3 c; FsrEasuF(c, AU2(p), asuint(SR[1]), asuint(SR[2]), asuint(SR[3]), asuint(SR[4]));
    return float4(saturate(c), 1);
}
// A = FsrRcasCon constant (as float bits)
float4 PS_FsrRcas(V i) : SV_Target
{
    AF1 r, g, b; FsrRcasF(r, g, b, AU2(i.pos.xy), asuint(A));
    return float4(r, g, b, 1);
}",
            "PS_Sgsr" => Common + @"
#define SGSR_MOBILE
half4 SGSRRH(float2 p) { return Src.GatherRed(Lin, p); }
half4 SGSRGH(float2 p) { return Src.GatherGreen(Lin, p); }
half4 SGSRBH(float2 p) { return Src.GatherBlue(Lin, p); }
half4 SGSRAH(float2 p) { return Src.GatherAlpha(Lin, p); }
half4 SGSRRGBH(float2 p) { return Src.SampleLevel(Lin, p, 0); }
half4 SGSRH(float2 p, uint channel) { if (channel == 0) return SGSRRH(p); if (channel == 1) return SGSRGH(p); if (channel == 2) return SGSRBH(p); return SGSRAH(p); }
" + Vendor("sgsr/sgsr1_mobile.h") + @"
// A = source rect in uv, SR[0] = destination rect; ViewportInfo = (1/texW, 1/texH, texW, texH)
float4 PS_Sgsr(V i) : SV_Target
{
    float2 p = (i.pos.xy - SR[0].xy) / SR[0].zw;
    if (any(p < 0) || any(p > 1)) return float4(0, 0, 0, 1);
    half4 o = half4(0, 0, 0, 1);
    SgsrYuvH(o, A.xy + p * A.zw, float4(Texel.xy, 1 / Texel.xy));
    return float4(saturate(o.rgb), 1);
}",
            "CS_Nis" => @"
#define NIS_HLSL 1
#define NIS_SCALER 1
#define NIS_HDR_MODE 0
#define NIS_VIEWPORT_SUPPORT 1
#define NIS_BLOCK_WIDTH 32
#define NIS_BLOCK_HEIGHT 24
#define NIS_THREAD_GROUP_SIZE 256
// constant layout from NVIDIA's NIS_Main.hlsl (MIT)
cbuffer cb : register(b0)
{
    float kDetectRatio; float kDetectThres; float kMinContrastRatio; float kRatioNorm;
    float kContrastBoost; float kEps; float kSharpStartY; float kSharpScaleY;
    float kSharpStrengthMin; float kSharpStrengthScale; float kSharpLimitMin; float kSharpLimitScale;
    float kScaleX; float kScaleY; float kDstNormX; float kDstNormY;
    float kSrcNormX; float kSrcNormY;
    uint kInputViewportOriginX; uint kInputViewportOriginY; uint kInputViewportWidth; uint kInputViewportHeight;
    uint kOutputViewportOriginX; uint kOutputViewportOriginY; uint kOutputViewportWidth; uint kOutputViewportHeight;
    float reserved0; float reserved1;
};
SamplerState samplerLinearClamp : register(s0);
Texture2D in_texture : register(t0);
RWTexture2D<float4> out_texture : register(u0);
Texture2D coef_scaler : register(t1);
Texture2D coef_usm : register(t2);
" + Vendor("nis/NIS_Scaler.h") + @"
[numthreads(NIS_THREAD_GROUP_SIZE, 1, 1)]
void CS_Nis(uint3 blockIdx : SV_GroupID, uint3 threadIdx : SV_GroupThreadID) { NVScaler(blockIdx.xy, threadIdx.x); }",
            _ when GraphOf(entry) is { } g => Vendor(g.Resource),
            _ => Hlsl,
        };

        static ShaderGraph? GraphOf(string entry) => ShaderGraphs.All.FirstOrDefault(g => g.Passes.Any(p => p.Entry == entry));

        [StructLayout(LayoutKind.Sequential)]
        struct GraphConstants { public Vector4 OutSize, In0Size, SrcSize, Pad; }

        static float[]? _nisScale, _nisUsm;
        /// <summary>NIS filter tables (64 phases × 8 taps), read from NVIDIA's NIS_Config.h as shipped.</summary>
        static (float[] scale, float[] usm) NisCoefficients()
        {
            if (_nisScale != null && _nisUsm != null) return (_nisScale, _nisUsm);
            var src = Vendor("nis/NIS_Config.h");
            float[] Table(string name)
            {
                int i = src.IndexOf(name + "[kPhaseCount][kFilterSize] = {", StringComparison.Ordinal);
                int j = src.IndexOf("};", i, StringComparison.Ordinal);
                var nums = System.Text.RegularExpressions.Regex.Matches(src.Substring(i + name.Length + 30, j - i - name.Length - 30), @"-?\d+\.\d+(?:e-?\d+)?f?");
                var v = nums.Select(m => float.Parse(m.Value.TrimEnd('f'), CultureInfo.InvariantCulture)).ToArray();
                if (v.Length != 64 * 8) throw new InvalidDataException($"NIS: {name} com {v.Length} valores");
                return v;
            }
            _nisScale = Table("coef_scale"); _nisUsm = Table("coef_usm");
            return (_nisScale, _nisUsm);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NisConstants
        {
            public float DetectRatio, DetectThres, MinContrastRatio, RatioNorm, ContrastBoost, Eps, SharpStartY, SharpScaleY,
                         SharpStrengthMin, SharpStrengthScale, SharpLimitMin, SharpLimitScale, ScaleX, ScaleY, DstNormX, DstNormY, SrcNormX, SrcNormY;
            public uint InX, InY, InW, InH, OutX, OutY, OutW, OutH;
            public float R0, R1, Pad0, Pad1, Pad2, Pad3; // 128 bytes (constant buffers are multiples of 16)
        }

        /// <summary>Port of NVScalerUpdateConfig (NIS_Config.h, SDR) — sharpness 0..1.</summary>
        static NisConstants NisConfig(float sharpness, uint inX, uint inY, uint inW, uint inH, uint texW, uint texH, uint outW, uint outH)
        {
            sharpness = Math.Clamp(sharpness, 0, 1);
            float s = sharpness - 0.5f;
            float maxScale = s >= 0 ? 1.25f : 1.75f, minScale = s >= 0 ? 1.25f : 1.0f, limitScale = s >= 0 ? 1.25f : 1.0f;
            float strengthMin = Math.Max(0, 0.4f + s * minScale * 1.2f), strengthMax = 1.6f + s * maxScale * 1.8f;
            float limitMin = Math.Max(0.1f, 0.14f + s * limitScale * 0.32f), limitMax = 0.5f + s * limitScale * 0.6f;
            return new NisConstants
            {
                DetectRatio = 2 * 1127f / 1024f, DetectThres = 64f / 1024f, MinContrastRatio = 2f, RatioNorm = 1f / (10f - 2f),
                ContrastBoost = 1f, Eps = 1f / 255f, SharpStartY = 0.45f, SharpScaleY = 1f / (0.9f - 0.45f),
                SharpStrengthMin = strengthMin, SharpStrengthScale = strengthMax - strengthMin, SharpLimitMin = limitMin, SharpLimitScale = limitMax - limitMin,
                ScaleX = inW / (float)outW, ScaleY = inH / (float)outH, DstNormX = 1f / outW, DstNormY = 1f / outH, SrcNormX = 1f / texW, SrcNormY = 1f / texH,
                InX = inX, InY = inY, InW = inW, InH = inH, OutX = 0, OutY = 0, OutW = outW, OutH = outH,
            };
        }

        static readonly Dictionary<string, byte[]> _bytecode = new();
        /// <summary>Compiled once per process (d3dcompiler_47 ships with Windows), shared by every device.</summary>
        static byte[] Bytecode(string entry)
        {
            lock (_bytecode)
            {
                if (_bytecode.TryGetValue(entry, out var b)) return b;
                b = Compiler.Compile(SourceFor(entry), entry, entry + ".hlsl", entry == "VS" ? "vs_5_0" : entry.StartsWith("CS_") ? "cs_5_0" : "ps_5_0").ToArray();
                _bytecode[entry] = b;
                return b;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Constants
        {
            public Vector4 Texel;      // 1/w, 1/h, lutSize, mix (layer opacity)
            public Vector4 A;          // per pass: spatial = sharpen, clarity, dehaze; selective = relative
            public Vector4 S0, S1, S2, S3, S4, S5, S6, S7, S8; // selective: c, m, y, k per range (-1..1)
        }

        static Constants ConstantsFor(Layer l, int w, int h, int lutSize)
        {
            var c = new Constants { Texel = new Vector4(1f / w, 1f / h, lutSize, (float)Math.Clamp(l.Opacity / 100, 0, 1)) };
            switch (l.Kind)
            {
                case LayerKind.Sharpen:
                    c.A = new Vector4((float)(l.Get("sharpen") / 100), (float)(l.Get("clarity") / 100), (float)(l.Get("dehaze") / 100), 0);
                    break;
                case LayerKind.SelectiveColor:
                    c.A = new Vector4(l.Get("relative") > 0.5 ? 1 : 0, 0, 0, 0);
                    Vector4 R(int i) { var k = LayerDefs.SelectiveRanges[i].key; return new Vector4((float)l.Get(k + "_c"), (float)l.Get(k + "_m"), (float)l.Get(k + "_y"), (float)l.Get(k + "_k")) / 100f; }
                    c.S0 = R(0); c.S1 = R(1); c.S2 = R(2); c.S3 = R(3); c.S4 = R(4); c.S5 = R(5); c.S6 = R(6); c.S7 = R(7); c.S8 = R(8);
                    break;
            }
            return c;
        }

        sealed class Worker
        {
            readonly MonitorInfo _m;
            readonly ShaderEngine _owner;
            Thread? _thread;
            volatile bool _stop;
            Params? _pending;
            readonly object _plock = new();
            public long Heartbeat = Environment.TickCount64;
            public volatile bool Visible;
            IntPtr _hwnd;

            public Worker(MonitorInfo m, ShaderEngine owner) { _m = m; _owner = owner; }

            public bool SameBounds(MonitorInfo m) => m.Bounds.Left == _m.Bounds.Left && m.Bounds.Top == _m.Bounds.Top
                                                     && m.Bounds.Width == _m.Bounds.Width && m.Bounds.Height == _m.Bounds.Height;

            public void Start()
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "ScreenMaster shader " + _m.DeviceName };
                _thread.Start();
            }

            public void Post(Params p) { lock (_plock) _pending = p; }

            public void Stop()
            {
                _stop = true;
                HideFromOtherThread(null);
                _thread?.Join(1500);
            }

            public void HideFromOtherThread(string? why)
            {
                var h = _hwnd;
                if (h != IntPtr.Zero) ShowWindowAsync(h, 0 /* SW_HIDE */);
                Visible = false;
                if (why != null) { Log.Write($"shader {_m.DeviceName}: {why}"); _owner.SetError(_m.DeviceName, "Modo shader escondido: " + why); }
            }

            // ---- D3D state
            ID3D11Device? _dev;
            ID3D11DeviceContext? _ctx;
            IDXGIOutputDuplication? _dup;
            IDXGISwapChain1? _swap;
            ID3D11Texture2D? _src;
            ID3D11ShaderResourceView? _srcView;
            readonly Dictionary<string, (ID3D11Texture3D tex, ID3D11ShaderResourceView view, int size)> _luts = new(StringComparer.OrdinalIgnoreCase);
            (ID3D11Texture3D tex, ID3D11ShaderResourceView view, int size)? _identityLut;
            ID3D11VertexShader? _vs;
            readonly Dictionary<LayerKind, ID3D11PixelShader> _ps = new();
            ID3D11Buffer? _cb;
            // ping-pong targets between passes
            readonly ID3D11Texture2D?[] _tmp = new ID3D11Texture2D?[2];
            readonly ID3D11ShaderResourceView?[] _tmpView = new ID3D11ShaderResourceView?[2];
            readonly ID3D11RenderTargetView?[] _tmpRtv = new ID3D11RenderTargetView?[2];
            // GPU timing: a ring of (disjoint, timestamps per boundary) read back a few frames later
            sealed class TimingSet { public ID3D11Query Disjoint = null!; public List<ID3D11Query> Stamps = new(); public List<Guid> Ids = new(); public bool Pending; }
            readonly TimingSet?[] _timing = new TimingSet?[3];
            int _timingIdx;
            // scale mode
            ID3D11PixelShader? _psCursor, _psRcas;
            // NVIDIA NIS (compute): coefficient tables, constants and an output sized to the scaled image (≤ 2× the source)
            ID3D11ComputeShader? _csNis;
            ID3D11Buffer? _cbNis;
            ID3D11Texture2D? _nisCoefS, _nisCoefU, _nisOut;
            ID3D11ShaderResourceView? _nisCoefSView, _nisCoefUView, _nisOutView;
            ID3D11UnorderedAccessView? _nisOutUav;
            int _nisW, _nisH;
            // multi-pass upscalers: named RGBA16F targets ("SOURCE" = cropped window), per-graph shaders
            ID3D11PixelShader? _psCrop;
            ID3D11Buffer? _cbGraph;
            readonly Dictionary<string, ID3D11PixelShader> _psGraph = new();
            readonly Dictionary<string, (ID3D11Texture2D tex, ID3D11ShaderResourceView srv, ID3D11RenderTargetView rtv, int w, int h)> _graphTex = new();
            string _graphKey = "";
            readonly ID3D11PixelShader?[] _psScale = new ID3D11PixelShader?[ScaleMath.FilterEntries.Length];
            ID3D11BlendState? _blend;
            long _lastScaleRender;
            IntPtr _cursorHandle;
            ID3D11Texture2D? _cursorTex;
            ID3D11ShaderResourceView? _cursorView;
            int _cursorW, _cursorH, _hotX, _hotY;
            ID3D11SamplerState? _linear, _point;
            int _w, _h;

            void Run()
            {
                try
                {
                    CreateWindow();
                    Params? cur = null;
                    bool haveFrame = false, dirty = false;
                    long retryAt = 0;
                    while (!_stop)
                    {
                        Heartbeat = Environment.TickCount64;
                        Pump();
                        Params? np;
                        lock (_plock) { np = _pending; _pending = null; }
                        if (np != null) { cur = np; dirty = true; }
                        if (cur == null) { Thread.Sleep(10); continue; }

                        if (_dup == null)
                        {
                            if (Environment.TickCount64 < retryAt) { Thread.Sleep(20); continue; }
                            var err = InitD3D();
                            if (err != null)
                            {
                                Hide(err);
                                retryAt = Environment.TickCount64 + 1000;
                                ReleaseD3D();
                                continue;
                            }
                            haveFrame = false;
                        }

                        if (dirty) { try { UpdateLuts(cur); } catch (Exception ex) { _owner.SetError(_m.DeviceName, "LUT: " + ex.Message); } }

                        var r = _dup!.AcquireNextFrame(16, out var info, out var res);
                        if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                        {
                            // the source window can move or the cursor change shape without a new desktop frame
                            if (cur.Scale != null && haveFrame && Environment.TickCount64 - _lastScaleRender > 100) dirty = true;
                            if (dirty && haveFrame) { Render(cur); dirty = false; }
                            continue;
                        }
                        if (r.Failure)
                        {
                            Hide(r.Code == Vortice.DXGI.ResultCode.AccessLost.Code ? "captura interrompida (UAC, troca de modo ou tela cheia exclusiva)" : $"captura falhou (0x{r.Code:X8})");
                            ReleaseD3D();
                            retryAt = Environment.TickCount64 + 500;
                            continue;
                        }
                        try
                        {
                            if (info.ProtectedContentMaskedOut)
                            {
                                Hide("vídeo protegido na tela (a captura vem preta)");
                                haveFrame = false;
                                continue;
                            }
                            if (cur.Scale != null && info.LastMouseUpdateTime != 0) dirty = true;
                            if (info.LastPresentTime != 0 || !haveFrame)
                            {
                                using var tex = res!.QueryInterface<ID3D11Texture2D>();
                                if (tex.Description.Format != Format.B8G8R8A8_UNorm)
                                {
                                    Hide($"formato de captura não suportado ({tex.Description.Format})");
                                    haveFrame = false;
                                    continue;
                                }
                                _ctx!.CopyResource(_src!, tex);
                                haveFrame = true;
                                dirty = true;
                            }
                        }
                        finally
                        {
                            res?.Dispose();
                            _dup.ReleaseFrame();
                        }
                        if (dirty && haveFrame) { Render(cur); dirty = false; }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"shader {_m.DeviceName} crashed: {ex}");
                    _owner.SetError(_m.DeviceName, "Modo shader parou: " + ex.Message);
                }
                finally
                {
                    Visible = false;
                    ReleaseD3D();
                    if (_hwnd != IntPtr.Zero) { Win32.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
                }
            }

            void Hide(string why)
            {
                if (Visible || _owner.ErrorOf(_m.DeviceName) != "Modo shader escondido: " + why)
                    HideFromOtherThread(why);
            }

            static void Pump()
            {
                while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }

            void CreateWindow()
            {
                EnsureClass();
                var b = _m.Bounds;
                _w = b.Width; _h = b.Height;
                _hwnd = Win32.CreateWindowEx(
                    Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
                    ClassName, "ScreenMaster shader", Win32.WS_POPUP, b.Left, b.Top, b.Width, b.Height,
                    IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx ({Marshal.GetLastWin32Error()})");
                SetLayeredWindowAttributes(_hwnd, 0, 255, 2 /* LWA_ALPHA */);
                // never capture ourselves: without this the shader would feed on its own output
                if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
                    throw new InvalidOperationException("o Windows não deixou esconder a janela da captura (precisa do Windows 10 2004 ou mais novo)");
            }

            string? InitD3D()
            {
                try
                {
                    using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                    IDXGIAdapter1? adapter = null; IDXGIOutput? output = null;
                    for (uint a = 0; factory.EnumAdapters1(a, out var ad).Success; a++)
                    {
                        for (uint o = 0; ad.EnumOutputs(o, out var op).Success; o++)
                        {
                            if (string.Equals(op.Description.DeviceName, _m.DeviceName, StringComparison.OrdinalIgnoreCase)) { adapter = ad; output = op; break; }
                            op.Dispose();
                        }
                        if (output != null) break;
                        ad.Dispose();
                    }
                    if (adapter == null || output == null) return "monitor não encontrado na placa de vídeo";
                    using (adapter)
                    using (output)
                    {
                        using (var o6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                            if (o6 != null && o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020)
                                return "monitor em HDR (o modo shader só funciona em SDR)";
                        if (output.Description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            return "monitor girado não é suportado";

                        var fl = new[] { FeatureLevel.Level_11_0 };
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, fl, out _dev, out _ctx).CheckError();
                        using var o1 = output.QueryInterface<IDXGIOutput1>();
                        _dup = o1.DuplicateOutput(_dev);
                        var dd = _dup.Description.ModeDescription;
                        _w = (int)dd.Width; _h = (int)dd.Height;

                        using var f2 = adapter.GetParent<IDXGIFactory2>();
                        var sd = new SwapChainDescription1
                        {
                            Width = (uint)_w, Height = (uint)_h, Format = Format.B8G8R8A8_UNorm,
                            BufferCount = 2, BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
                        };
                        _swap = f2.CreateSwapChainForHwnd(_dev, _hwnd, sd);
                        f2.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
                    }

                    _src = _dev.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                    });
                    _srcView = _dev.CreateShaderResourceView(_src);
                    for (int i = 0; i < 2; i++)
                    {
                        _tmp[i] = _dev.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                        });
                        _tmpView[i] = _dev.CreateShaderResourceView(_tmp[i]);
                        _tmpRtv[i] = _dev.CreateRenderTargetView(_tmp[i]);
                    }
                    _vs = _dev.CreateVertexShader(Bytecode("VS"));
                    foreach (var (k, e) in PassEntries) _ps[k] = _dev.CreatePixelShader(Bytecode(e));
                    for (int f = 0; f < _psScale.Length; f++) _psScale[f] = _dev.CreatePixelShader(Bytecode(ScaleMath.FilterEntries[f]));
                    _psRcas = _dev.CreatePixelShader(Bytecode("PS_FsrRcas"));
                    _csNis = _dev.CreateComputeShader(Bytecode("CS_Nis"));
                    _psCrop = _dev.CreatePixelShader(Bytecode("PS_Crop"));
                    _cbGraph = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<GraphConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
                    _cbNis = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<NisConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
                    var (cs, cu) = NisCoefficients();
                    (_nisCoefS, _nisCoefSView) = CoefTexture(cs);
                    (_nisCoefU, _nisCoefUView) = CoefTexture(cu);
                    _psCursor = _dev.CreatePixelShader(Bytecode("PS_Cursor"));
                    var bd = new BlendDescription();
                    bd.RenderTarget[0] = new RenderTargetBlendDescription
                    {
                        BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
                        SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
                        RenderTargetWriteMask = ColorWriteEnable.All,
                    };
                    _blend = _dev.CreateBlendState(bd);
                    for (int i = 0; i < _timing.Length; i++)
                        _timing[i] = new TimingSet { Disjoint = _dev.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)) };
                    _cb = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
                    _linear = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
                    _point = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
                    return null;
                }
                catch (Exception ex)
                {
                    Log.Write($"shader init {_m.DeviceName}: {ex.Message}");
                    return ex is SharpGen.Runtime.SharpGenException sg && sg.ResultCode.Code == Vortice.DXGI.ResultCode.Unsupported.Code
                        ? "captura não suportada nesta placa/monitor"
                        : "não foi possível iniciar a captura: " + ex.Message;
                }
            }

            void ReleaseD3D()
            {
                foreach (var (t, v, _) in _luts.Values) { v.Dispose(); t.Dispose(); }
                _luts.Clear();
                if (_identityLut is { } id) { id.view.Dispose(); id.tex.Dispose(); _identityLut = null; }
                for (int i = 0; i < 2; i++) { _tmpRtv[i]?.Dispose(); _tmpRtv[i] = null; _tmpView[i]?.Dispose(); _tmpView[i] = null; _tmp[i]?.Dispose(); _tmp[i] = null; }
                foreach (var ts in _timing) { if (ts == null) continue; ts.Disjoint.Dispose(); foreach (var q in ts.Stamps) q.Dispose(); }
                Array.Clear(_timing);
                foreach (var ps in _ps.Values) ps.Dispose();
                _ps.Clear();
                for (int f = 0; f < _psScale.Length; f++) { _psScale[f]?.Dispose(); _psScale[f] = null; }
                _csNis?.Dispose(); _csNis = null; _cbNis?.Dispose(); _cbNis = null;
                _psCrop?.Dispose(); _psCrop = null; _cbGraph?.Dispose(); _cbGraph = null;
                foreach (var ps in _psGraph.Values) ps.Dispose();
                _psGraph.Clear();
                ReleaseGraphTextures();
                _nisCoefSView?.Dispose(); _nisCoefSView = null; _nisCoefS?.Dispose(); _nisCoefS = null;
                _nisCoefUView?.Dispose(); _nisCoefUView = null; _nisCoefU?.Dispose(); _nisCoefU = null;
                ReleaseNisOut();
                _psRcas?.Dispose(); _psRcas = null; _psCursor?.Dispose(); _psCursor = null; _blend?.Dispose(); _blend = null;
                _cursorView?.Dispose(); _cursorView = null; _cursorTex?.Dispose(); _cursorTex = null; _cursorHandle = IntPtr.Zero;
                _srcView?.Dispose(); _srcView = null; _src?.Dispose(); _src = null;
                _cb?.Dispose(); _cb = null; _linear?.Dispose(); _linear = null; _point?.Dispose(); _point = null;
                _swap?.Dispose(); _swap = null;
                try { _dup?.Dispose(); } catch { }
                _dup = null;
                _vs?.Dispose(); _vs = null;
                _ctx?.ClearState(); _ctx?.Dispose(); _ctx = null;
                _dev?.Dispose(); _dev = null;
            }

            (ID3D11Texture3D tex, ID3D11ShaderResourceView view, int size) MakeLut(CubeLut lut)
            {
                var desc = new Texture3DDescription
                {
                    Width = (uint)lut.Size, Height = (uint)lut.Size, Depth = (uint)lut.Size, MipLevels = 1,
                    Format = Format.R32G32B32A32_Float, Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                };
                ID3D11Texture3D tex;
                unsafe
                {
                    fixed (float* d = lut.Rgba)
                    {
                        var init = new SubresourceData((IntPtr)d, (uint)(lut.Size * 16), (uint)(lut.Size * lut.Size * 16));
                        tex = _dev!.CreateTexture3D(desc, new[] { init });
                    }
                }
                return (tex, _dev!.CreateShaderResourceView(tex), lut.Size);
            }

            /// <summary>Keeps one 3D texture per .cube used by this monitor's chain (and an identity one).</summary>
            void UpdateLuts(Params p)
            {
                if (_dev == null) return;
                _identityLut ??= MakeLut(new CubeLut(2, new float[] { 0,0,0,1, 1,0,0,1, 0,1,0,1, 1,1,0,1, 0,0,1,1, 1,0,1,1, 0,1,1,1, 1,1,1,1 }, ""));
                var paths = p.Passes.Where(l => l.Kind == LayerKind.ColorLookup).Select(l => l.GetS("file")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var old in _luts.Keys.Where(k => !paths.Contains(k)).ToList()) { _luts[old].view.Dispose(); _luts[old].tex.Dispose(); _luts.Remove(old); }
                foreach (var path in paths.Where(x => !_luts.ContainsKey(x))) _luts[path] = MakeLut(LoadCube(path));
            }

            void Render(Params p)
            {
                if (_dev == null || _ctx == null || _swap == null || _vs == null) return;
                if (_identityLut == null) UpdateLuts(p);
                var passes = p.Passes.Where(l => _ps.ContainsKey(l.Kind)).ToList();
                ScaleMath.Rect srcRect = default, dstRect = default;
                bool scaling = false;
                if (p.Scale != null)
                {
                    if (!ScaleSource(p.Scale, out srcRect, out var why)) { Hide(why); return; }
                    dstRect = ScaleMath.Fit((int)srcRect.W, (int)srcRect.H, _w, _h, p.Scale.Aspect, p.Scale.Integer);
                    scaling = true;
                    _lastScaleRender = Environment.TickCount64;
                }
                if (passes.Count == 0 && !scaling) return;

                ReadTimings();
                var ts = _timing[_timingIdx];
                bool timing = ts != null && !ts.Pending;
                if (timing)
                {
                    while (ts!.Stamps.Count < passes.Count + 1) ts.Stamps.Add(_dev.CreateQuery(new QueryDescription(QueryType.Timestamp)));
                    ts.Ids = passes.Select(l => l.Id).ToList();
                    _ctx.Begin(ts.Disjoint);
                    _ctx.End(ts.Stamps[0]);
                }

                using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
                using var backRtv = _dev.CreateRenderTargetView(back);
                _ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _w, _h));
                _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _ctx.VSSetShader(_vs);
                _ctx.PSSetSamplers(0, new[] { _point!, _linear! });
                _ctx.PSSetConstantBuffer(0, _cb);

                var input = _srcView!;
                int slot = 0;
                if (scaling)
                {
                    var sc = new Constants
                    {
                        Texel = new Vector4(1f / _w, 1f / _h, 0, 1),
                        A = new Vector4((float)(srcRect.X / _w), (float)(srcRect.Y / _h), (float)(srcRect.W / _w), (float)(srcRect.H / _h)),
                        S0 = new Vector4((float)dstRect.X, (float)dstRect.Y, (float)dstRect.W, (float)dstRect.H),
                    };
                    // FsrEasuConOffset (AMD FSR 1): viewport = source rect, resource = captured monitor, output = destination rect
                    float ivx = (float)srcRect.W, ivy = (float)srcRect.H, isx = _w, isy = _h, ox = (float)dstRect.W, oy = (float)dstRect.H;
                    sc.S1 = new Vector4(ivx / ox, ivy / oy, 0.5f * ivx / ox - 0.5f + (float)srcRect.X, 0.5f * ivy / oy - 0.5f + (float)srcRect.Y);
                    sc.S2 = new Vector4(1 / isx, 1 / isy, 1 / isx, -1 / isy);
                    sc.S3 = new Vector4(-1 / isx, 2 / isy, 1 / isx, 2 / isy);
                    sc.S4 = new Vector4(0, 4 / isy, 0, 0);
                    bool rcas = p.Scale!.Sharpen > 0;
                    _ctx.UpdateSubresource(sc, _cb!);
                    _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                    bool nis = p.Scale.Filter == ScaleMath.NisFilter;
                    int gi = p.Scale.Filter - ScaleMath.GraphFilter0;
                    bool graph = gi >= 0 && gi < ShaderGraphs.All.Length;
                    if (nis) { rcas = false; NisScale(srcRect, dstRect, (float)(p.Scale.Sharpen / 100), ref sc); input = _nisOutView!; }
                    else if (graph) { input = RunGraph(ShaderGraphs.All[gi], srcRect, ref sc); }
                    _ctx.UpdateSubresource(sc, _cb!);
                    _ctx.OMSetRenderTargets(passes.Count == 0 && !rcas ? backRtv : _tmpRtv[0]!);
                    // NIS output is placed (and resized if the screen is > 2× the source) with Lanczos
                    _ctx.PSSetShader(nis || graph ? _psScale[2] : _psScale[Math.Clamp(p.Scale.Filter, 0, _psScale.Length - 1)]);
                    _ctx.PSSetShaderResources(0, new[] { input, _identityLut!.Value.view });
                    _ctx.Draw(3, 0);
                    input = _srcView!;
                    if (rcas)
                    {
                        // AMD FSR 1 RCAS on the scaled image: 0..100 % → 2..0 stops (FsrRcasCon: con.x = exp2(-stops))
                        sc.A = new Vector4((float)Math.Pow(2, -2 * (1 - p.Scale.Sharpen / 100)), 0, 0, 0);
                        _ctx.UpdateSubresource(sc, _cb!);
                        _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                        _ctx.OMSetRenderTargets(passes.Count == 0 ? backRtv : _tmpRtv[1]!);
                        _ctx.PSSetShader(_psRcas);
                        _ctx.PSSetShaderResources(0, new[] { _tmpView[0]!, _identityLut!.Value.view });
                        _ctx.Draw(3, 0);
                    }
                    if (passes.Count > 0) { input = _tmpView[rcas ? 1 : 0]!; slot = rcas ? 0 : 1; }
                }
                for (int i = 0; i < passes.Count; i++)
                {
                    var l = passes[i];
                    bool last = i == passes.Count - 1;
                    var lut = l.Kind == LayerKind.ColorLookup && _luts.TryGetValue(l.GetS("file"), out var lt) ? lt : _identityLut!.Value;
                    _ctx.UpdateSubresource(ConstantsFor(l, _w, _h, lut.size), _cb!);
                    _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! }); // unbind before reusing as target
                    _ctx.OMSetRenderTargets(last ? backRtv : _tmpRtv[(i + slot) % 2]!);
                    _ctx.PSSetShader(_ps[l.Kind]);
                    _ctx.PSSetShaderResources(0, new[] { input, lut.view });
                    _ctx.Draw(3, 0);
                    if (timing) _ctx.End(ts!.Stamps[i + 1]);
                    if (!last) input = _tmpView[(i + slot) % 2]!;
                }
                if (scaling) DrawCursor(backRtv, srcRect, dstRect);
                if (timing) { _ctx.End(ts!.Disjoint); ts.Pending = true; }
                _timingIdx = (_timingIdx + 1) % _timing.Length;

                _swap.Present(1, PresentFlags.None);
                if (!Visible)
                {
                    Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
                    Visible = true;
                    _owner.SetError(_m.DeviceName, "");
                }
            }

            (ID3D11Texture2D, ID3D11ShaderResourceView) CoefTexture(float[] v)
            {
                unsafe
                {
                    fixed (float* d = v)
                    {
                        var t = _dev!.CreateTexture2D(new Texture2DDescription
                        {
                            Width = 2, Height = 64, MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                        }, new[] { new SubresourceData((IntPtr)d, 2 * 16) });
                        return (t, _dev.CreateShaderResourceView(t));
                    }
                }
            }

            void ReleaseGraphTextures()
            {
                foreach (var t in _graphTex.Values) { t.rtv.Dispose(); t.srv.Dispose(); t.tex.Dispose(); }
                _graphTex.Clear();
                _graphKey = "";
            }

            /// <summary>
            /// Crops the window out of the capture at native size ("SOURCE"), runs the graph's passes on named
            /// RGBA16F targets and returns "OUT" (2×). sc is rewritten so the next draw places OUT on the destination rect.
            /// </summary>
            ID3D11ShaderResourceView RunGraph(ShaderGraph g, ScaleMath.Rect src, ref Constants sc)
            {
                int sw = (int)src.W, sh = (int)src.H;
                var key = $"{g.Name}|{sw}x{sh}";
                if (key != _graphKey)
                {
                    ReleaseGraphTextures();
                    void Make(string name, int w, int h)
                    {
                        var t = _dev!.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                        });
                        _graphTex[name] = (t, _dev.CreateShaderResourceView(t), _dev.CreateRenderTargetView(t), w, h);
                    }
                    Make("SOURCE", sw, sh);
                    foreach (var ps in g.Passes)
                        if (!_graphTex.ContainsKey(ps.Output)) Make(ps.Output, (int)Math.Round(sw * ps.Scale), (int)Math.Round(sh * ps.Scale));
                    foreach (var ps in g.Passes)
                        if (!_psGraph.ContainsKey(ps.Entry)) _psGraph[ps.Entry] = _dev!.CreatePixelShader(Bytecode(ps.Entry));
                    _graphKey = key;
                }
                var none = new ID3D11ShaderResourceView[] { null!, null!, null!, null! };
                // crop
                var s0 = _graphTex["SOURCE"];
                sc.Texel = new Vector4(1f / _w, 1f / _h, 0, 1);
                sc.A = new Vector4((float)(src.X / _w), (float)(src.Y / _h), (float)(src.W / _w), (float)(src.H / _h));
                _ctx!.UpdateSubresource(sc, _cb!);
                _ctx.PSSetShaderResources(0, none);
                _ctx.OMSetRenderTargets(s0.rtv);
                _ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, sw, sh));
                _ctx.PSSetShader(_psCrop);
                _ctx.PSSetShaderResources(0, new[] { _srcView!, _identityLut!.Value.view });
                _ctx.Draw(3, 0);
                // passes
                Vector4 Size(int w, int h) => new(w, h, 1f / w, 1f / h);
                _ctx.PSSetConstantBuffer(0, _cbGraph);
                foreach (var ps in g.Passes)
                {
                    var o = _graphTex[ps.Output];
                    var ins = ps.Inputs.Select(n => _graphTex[n]).ToArray();
                    _ctx.UpdateSubresource(new GraphConstants { OutSize = Size(o.w, o.h), In0Size = Size(ins[0].w, ins[0].h), SrcSize = Size(sw, sh) }, _cbGraph!);
                    _ctx.PSSetShaderResources(0, none);
                    _ctx.OMSetRenderTargets(o.rtv);
                    _ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, o.w, o.h));
                    _ctx.PSSetShader(_psGraph[ps.Entry]);
                    _ctx.PSSetShaderResources(0, ins.Select(t => t.srv).ToArray());
                    _ctx.Draw(3, 0);
                }
                _ctx.PSSetShaderResources(0, none);
                _ctx.PSSetConstantBuffer(0, _cb);
                _ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _w, _h));
                var outT = _graphTex["OUT"];
                sc.Texel = new Vector4(1f / outT.w, 1f / outT.h, 0, 1);
                sc.A = new Vector4(0, 0, 1, 1);
                return outT.srv;
            }

            void ReleaseNisOut()
            {
                _nisOutUav?.Dispose(); _nisOutUav = null; _nisOutView?.Dispose(); _nisOutView = null; _nisOut?.Dispose(); _nisOut = null;
                _nisW = _nisH = 0;
            }

            /// <summary>
            /// NVIDIA NIS on the source rect of the capture into an output ≤ 2× the source (NIS's limit),
            /// then sc is rewritten so the next draw places that output on the destination rect.
            /// </summary>
            void NisScale(ScaleMath.Rect src, ScaleMath.Rect dst, float sharpness, ref Constants sc)
            {
                int ow = (int)Math.Min(dst.W, src.W * 2), oh = (int)Math.Min(dst.H, src.H * 2);
                ow = Math.Max(ow, (int)src.W); oh = Math.Max(oh, (int)src.H);
                if (_nisOut == null || ow != _nisW || oh != _nisH)
                {
                    ReleaseNisOut();
                    _nisOut = _dev!.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)ow, Height = (uint)oh, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                    });
                    _nisOutView = _dev.CreateShaderResourceView(_nisOut);
                    _nisOutUav = _dev.CreateUnorderedAccessView(_nisOut);
                    _nisW = ow; _nisH = oh;
                }
                var c = NisConfig(sharpness, (uint)src.X, (uint)src.Y, (uint)src.W, (uint)src.H, (uint)_w, (uint)_h, (uint)ow, (uint)oh);
                _ctx!.UpdateSubresource(c, _cbNis!);
                _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                _ctx.OMSetRenderTargets(new ID3D11RenderTargetView[] { null! });
                _ctx.CSSetShader(_csNis);
                _ctx.CSSetConstantBuffer(0, _cbNis);
                _ctx.CSSetSampler(0, _linear!);
                _ctx.CSSetShaderResources(0, new[] { _srcView!, _nisCoefSView!, _nisCoefUView! });
                _ctx.CSSetUnorderedAccessView(0, _nisOutUav!);
                _ctx.Dispatch((uint)((ow + 31) / 32), (uint)((oh + 23) / 24), 1);
                _ctx.CSSetUnorderedAccessView(0, null!);
                _ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null! });
                _ctx.CSSetShader(null);
                // place: whole NIS output → destination rect
                sc.Texel = new Vector4(1f / ow, 1f / oh, 0, 1);
                sc.A = new Vector4(0, 0, 1, 1);
            }

            /// <summary>Client area of the scaled window, in this monitor's pixels (false = hide, with a reason).</summary>
            bool ScaleSource(ScaleSpec s, out ScaleMath.Rect r, out string why)
            {
                r = default; why = "";
                if (!IsWindow(s.Hwnd)) { why = "a janela escalada foi fechada"; return false; }
                if (IsIconic(s.Hwnd)) { why = "a janela escalada está minimizada"; return false; }
                if (!GetClientRect(s.Hwnd, out var cr) || cr.Width < 16 || cr.Height < 16) { why = "a janela escalada é pequena demais"; return false; }
                var pt = new POINT();
                ClientToScreen(s.Hwnd, ref pt);
                double x = pt.X - _m.Bounds.Left, y = pt.Y - _m.Bounds.Top;
                if (x < 0 || y < 0 || x + cr.Width > _w || y + cr.Height > _h) { why = "a janela escalada precisa estar inteira dentro deste monitor"; return false; }
                r = new ScaleMath.Rect(x, y, cr.Width, cr.Height);
                return true;
            }

            /// <summary>The real cursor is clipped to the source window (hidden); a copy is drawn on the scaled image.</summary>
            void DrawCursor(ID3D11RenderTargetView target, ScaleMath.Rect src, ScaleMath.Rect dst)
            {
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return; // hidden by the game
                double sx = ci.pt.X - _m.Bounds.Left, sy = ci.pt.Y - _m.Bounds.Top;
                if (sx < src.X || sy < src.Y || sx > src.X + src.W || sy > src.Y + src.H) return;
                if (ci.hCursor != _cursorHandle) LoadCursor(ci.hCursor);
                if (_cursorView == null) return;
                double k = dst.W / src.W;
                var (x, y) = ScaleMath.Map(sx, sy, src, dst);
                float w = (float)(_cursorW * k), h = (float)(_cursorH * k);
                _ctx!.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
                _ctx.OMSetRenderTargets(target);
                _ctx.OMSetBlendState(_blend);
                _ctx.RSSetViewport(new Vortice.Mathematics.Viewport((float)(x - _hotX * k), (float)(y - _hotY * k), w, h));
                _ctx.PSSetShader(_psCursor);
                _ctx.PSSetShaderResources(0, new[] { _cursorView, _identityLut!.Value.view });
                _ctx.Draw(3, 0);
                _ctx.OMSetBlendState(null);
                _ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _w, _h));
            }

            /// <summary>HCURSOR → premultiplied BGRA texture: drawn on black and on white to recover alpha (works for mono and color cursors).</summary>
            unsafe void LoadCursor(IntPtr hc)
            {
                _cursorView?.Dispose(); _cursorView = null; _cursorTex?.Dispose(); _cursorTex = null;
                _cursorHandle = hc;
                if (!GetIconInfo(hc, out var ii)) return;
                if (ii.hbmMask != IntPtr.Zero) Win32.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Win32.DeleteObject(ii.hbmColor);
                _hotX = ii.xHotspot; _hotY = ii.yHotspot;
                int w = Math.Max(16, GetSystemMetrics(13 /* SM_CXCURSOR */)), h = Math.Max(16, GetSystemMetrics(14 /* SM_CYCURSOR */));
                _cursorW = w; _cursorH = h;
                uint[] Draw(uint bg)
                {
                    var screen = Win32.GetDC(IntPtr.Zero);
                    var dc = Win32.CreateCompatibleDC(screen);
                    Win32.ReleaseDC(IntPtr.Zero, screen);
                    var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                    var bmp = Win32.CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
                    var old = Win32.SelectObject(dc, bmp);
                    var px = new Span<uint>((void*)bits, w * h);
                    px.Fill(bg);
                    DrawIconEx(dc, 0, 0, hc, w, h, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
                    var copy = px.ToArray();
                    Win32.SelectObject(dc, old); Win32.DeleteObject(bmp); Win32.DeleteDC(dc);
                    return copy;
                }
                var onBlack = Draw(0xFF000000); var onWhite = Draw(0xFFFFFFFF);
                var rgba = new uint[w * h];
                for (int i = 0; i < rgba.Length; i++)
                {
                    uint b = onBlack[i], wh = onWhite[i], o = 0;
                    int a = 255;
                    for (int c = 0; c < 24; c += 8) a = Math.Min(a, 255 - (int)(((wh >> c) & 0xFF) - ((b >> c) & 0xFF)));
                    a = Math.Clamp(a, 0, 255);
                    for (int c = 0; c < 24; c += 8) o |= (uint)Math.Min((int)((b >> c) & 0xFF), a) << c; // on black = color × alpha (premultiplied)
                    rgba[i] = o | (uint)a << 24;
                }
                fixed (uint* d = rgba)
                    _cursorTex = _dev!.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                    }, new[] { new SubresourceData((IntPtr)d, (uint)(w * 4)) });
                _cursorView = _dev.CreateShaderResourceView(_cursorTex);
            }


            unsafe void ReadTimings()
            {
                foreach (var ts in _timing)
                {
                    if (ts == null || !ts.Pending) continue;
                    QueryDataTimestampDisjoint dj;
                    if (_ctx!.GetData(ts.Disjoint, (IntPtr)(&dj), (uint)sizeof(QueryDataTimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Code != 0) continue;
                    ts.Pending = false;
                    if (dj.Disjoint || dj.Frequency == 0) continue;
                    var t = new ulong[ts.Ids.Count + 1];
                    bool ok = true;
                    for (int i = 0; i < t.Length && ok; i++)
                    {
                        ulong v;
                        ok = _ctx.GetData(ts.Stamps[i], (IntPtr)(&v), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code == 0;
                        t[i] = v;
                    }
                    if (!ok) continue;
                    for (int i = 0; i < ts.Ids.Count; i++)
                        _owner.SetTiming(ts.Ids[i], (t[i + 1] - t[i]) * 1000.0 / dj.Frequency);
                }
            }
        }

        // ------------------------------------------------------------------ HLSL

        const string Hlsl = @"
cbuffer C : register(b0)
{
    float4 Texel;   // 1/w, 1/h, lutSize, mix
    float4 A;       // pass parameters
    float4 SR[9];
};
Texture2D Src : register(t0);
Texture3D LutTex : register(t1);
SamplerState Pt : register(s0);
SamplerState Lin : register(s1);

struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V VS(uint id : SV_VertexID)
{
    V o; o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
float3 At(float2 uv, float2 d) { return Src.SampleLevel(Pt, uv + d * Texel.xy, 0).rgb; }

float3 Spatialize(float2 uv, float3 c)
{
    float sharp = A.x, clarity = A.y, dehaze = A.z;
    float3 o = c;
    if (dehaze != 0)
    {
        // dark channel over a small neighbourhood estimates the haze; positive removes it, negative adds it
        float dk = 1;
        [unroll] for (int i = -1; i <= 1; i++) [unroll] for (int j = -1; j <= 1; j++)
        { float3 s = At(uv, float2(i, j) * 4); dk = min(dk, min(s.r, min(s.g, s.b))); }
        if (dehaze > 0) { float t = dk * dehaze * 0.9; o = saturate((o - t) / max(0.05, 1 - t)); }
        else o = lerp(o, float3(0.82, 0.84, 0.86), -dehaze * 0.35);
    }
    if (clarity != 0)
    {
        float blur = 0;
        [unroll] for (int i = 0; i < 8; i++)
        { float a = i * 0.785398; blur += Luma(At(uv, float2(cos(a), sin(a)) * 7)); }
        blur /= 8;
        float l = Luma(c), w = 1 - pow(abs(2 * l - 1), 2);
        o = saturate(o + (l - blur) * clarity * w * 1.6);
    }
    if (sharp > 0)
    {
        // contrast-adaptive sharpening (after AMD CAS): less where the area already has strong contrast
        float3 n = At(uv, float2(0, -1)), s = At(uv, float2(0, 1)), e = At(uv, float2(1, 0)), w = At(uv, float2(-1, 0));
        float3 mn = min(c, min(min(n, s), min(e, w))), mx = max(c, max(max(n, s), max(e, w)));
        float3 amp = sqrt(saturate(min(mn, 1 - mx) / max(mx, 1e-4)));
        float3 wt = -amp * lerp(0.125, 0.2, sharp);
        float3 sh = saturate((c + (n + s + e + w) * wt) / (1 + 4 * wt));
        o = saturate(o + (sh - c));
    }
    return o;
}

float3 ApplyLut(float3 c)
{
    float n = Texel.z;
    float3 uvw = c * ((n - 1) / n) + 0.5 / n;
    return LutTex.SampleLevel(Lin, uvw, 0).rgb;
}

float3 Selective(float3 c)
{
    float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
    float md = c.r + c.g + c.b - mx - mn;
    float w[9];
    w[0] = (c.r == mx) ? mx - md : 0;           // reds
    w[1] = (c.b == mn) ? md - mn : 0;           // yellows
    w[2] = (c.g == mx) ? mx - md : 0;           // greens
    w[3] = (c.r == mn) ? md - mn : 0;           // cyans
    w[4] = (c.b == mx) ? mx - md : 0;           // blues
    w[5] = (c.g == mn) ? md - mn : 0;           // magentas
    w[6] = saturate((mn - 0.5) * 2);            // whites
    w[7] = saturate(1 - (abs(mx - 0.5) + abs(mn - 0.5))); // neutrals
    w[8] = saturate((0.5 - mx) * 2);            // blacks
    float3 ink = 1 - c, d = 0;
    [unroll] for (int i = 0; i < 9; i++)
    {
        float4 a = SR[i];
        float3 adj = float3(a.x, a.y, a.z) + a.w;           // cyan/magenta/yellow ink + black on all three
        float3 delta = A.x > 0.5 ? adj * ink : adj;        // relative scales with the ink already there
        d += clamp(delta, -ink, c) * w[i];
    }
    return saturate(c - d);
}

float3 In(V i) { return Src.SampleLevel(Pt, i.uv, 0).rgb; }

// scale mode: A = source rect in source uv (x, y, w, h), SR[0] = destination rect in output pixels
float4 PS_Scale(V i) : SV_Target
{
    float2 p = (i.pos.xy - SR[0].xy) / SR[0].zw;
    if (any(p < 0) || any(p > 1)) return float4(0, 0, 0, 1);
    return float4(Src.SampleLevel(Lin, A.xy + p * A.zw, 0).rgb, 1);
}
float2 ScaleSrc(V i, out bool inside)
{
    float2 p = (i.pos.xy - SR[0].xy) / SR[0].zw;
    inside = all(p >= 0) && all(p <= 1);
    return (A.xy + p * A.zw) / Texel.xy;   // source position in pixels of the captured monitor
}
float3 Px(float2 pix) { return Src.SampleLevel(Pt, (floor(pix) + 0.5) * Texel.xy, 0).rgb; }

// Catmull-Rom bicubic, 16 taps
float CR(float x) { x = abs(x); return x < 1 ? 1.5*x*x*x - 2.5*x*x + 1 : (x < 2 ? -0.5*x*x*x + 2.5*x*x - 4*x + 2 : 0); }
float4 PS_ScaleBicubic(V i) : SV_Target
{
    bool inside; float2 s = ScaleSrc(i, inside) - 0.5;
    if (!inside) return float4(0, 0, 0, 1);
    float2 f = frac(s), b = floor(s);
    float3 acc = 0; float wsum = 0;
    [unroll] for (int y = -1; y <= 2; y++) [unroll] for (int x = -1; x <= 2; x++)
    { float w = CR(x - f.x) * CR(y - f.y); acc += Px(b + float2(x, y) + 0.5) * w; wsum += w; }
    return float4(saturate(acc / wsum), 1);
}

// Lanczos 3, 36 taps, clamped to the 2x2 neighbourhood to avoid ringing halos
float L3(float x) { x = abs(x); if (x < 1e-4) return 1; if (x >= 3) return 0; float px = 3.14159265 * x; return 3 * sin(px) * sin(px / 3) / (px * px); }
float4 PS_ScaleLanczos(V i) : SV_Target
{
    bool inside; float2 s = ScaleSrc(i, inside) - 0.5;
    if (!inside) return float4(0, 0, 0, 1);
    float2 f = frac(s), b = floor(s);
    float3 acc = 0, mn = 1, mx = 0; float wsum = 0;
    [unroll] for (int y = -2; y <= 3; y++) [unroll] for (int x = -2; x <= 3; x++)
    {
        float3 c = Px(b + float2(x, y) + 0.5);
        float w = L3(x - f.x) * L3(y - f.y);
        acc += c * w; wsum += w;
        if (x >= 0 && x <= 1 && y >= 0 && y <= 1) { mn = min(mn, c); mx = max(mx, c); }
    }
    return float4(clamp(acc / wsum, mn, mx), 1);
}

// crop the source rect (A, in uv) out of the capture at native size: input of the multi-pass upscalers
float4 PS_Crop(V i) : SV_Target { return float4(Src.SampleLevel(Pt, A.xy + i.uv * A.zw, 0).rgb, 1); }

// cursor quad: drawn with its own viewport, premultiplied alpha
float4 PS_Cursor(V i) : SV_Target { return Src.SampleLevel(Lin, i.uv, 0); }
float4 PS_Spatial(V i) : SV_Target { float3 c = In(i); return float4(lerp(c, Spatialize(i.uv, c), Texel.w), 1); }
float4 PS_Lut(V i) : SV_Target { float3 c = In(i); return float4(lerp(c, ApplyLut(c), Texel.w), 1); }
float4 PS_Selective(V i) : SV_Target { float3 c = In(i); return float4(lerp(c, Selective(c), Texel.w), 1); }";

        // ------------------------------------------------------------------ win32 bits

        const string ClassName = "ScreenMasterShader";
        const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        static readonly WndProc _wndProc = (h, m, w, l) => Win32.DefWindowProc(h, m, w, l);
        static bool _classDone;
        static void EnsureClass()
        {
            lock (_wndProc)
            {
                if (_classDone) return;
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = Win32.GetModuleHandle(null),
                    lpszClassName = ClassName,
                };
                Win32.RegisterClassEx(ref wc);
                _classDone = true;
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    }
}
