using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenMaster.Core
{
    public enum LayerKind
    {
        // Matrix engine (global)
        HueSaturation, ColorBalance, BlackWhite, PhotoFilter, ChannelMixer, Invert,
        // Driver engine (per monitor)
        Vibrance,
        // Gamma engine (per monitor)
        BrightnessContrast, Levels, Curves, Exposure, Posterize, Threshold,
        // Overlay engine (per monitor)
        Grain, SolidColor, Gradient, Vignette,
        // Monitor hardware via DDC/CI (per monitor)
        MonitorHardware,
        // v5 additions
        Temperature, LiftGammaGain, ColorBlind, Guides, WindowsTheme,
        // v8 additions
        Pattern, GradientMap,
        // removed in v9 (shader mode): kept only so old settings files still load; App.Migrate drops these layers
        ColorLookup, SelectiveColor, Sharpen, Scale
    }

    public enum EngineKind { Matrix, Gamma, Driver, Overlay, Hardware, System }

    public enum ParamType { Slider, Toggle, Color, Choice }

    /// <param name="Optional">Shows a checkbox; the value only applies when "{Key}_on" is 1.</param>
    public sealed record ParamDef(string Key, string Label, ParamType Type, double Min = 0, double Max = 100,
        double Default = 0, double Step = 1, string? DefaultText = null, string[]? Choices = null, string Unit = "",
        bool Optional = false);

    public sealed record Category(EngineKind Engine, string Name, string Subtitle, string Color);

    public static class Categories
    {
        public static readonly Category[] All =
        {
            new(EngineKind.Gamma, "Tons", "placa de vídeo · por monitor", "#2E9E6B"),
            new(EngineKind.Matrix, "Cor", "Windows · todos os monitores", "#8A5CE6"),
            new(EngineKind.Driver, "NVIDIA", "driver · por monitor", "#76B900"),
            new(EngineKind.Overlay, "Sobreposição", "por cima da tela · por monitor", "#D07A35"),
            new(EngineKind.Hardware, "Monitor", "interno do monitor · um por ajuste", "#3B8EEA"),
            new(EngineKind.System, "Sistema", "configuração do Windows", "#2AA7B8"),
        };

        public static Category Of(EngineKind e) => All.First(c => c.Engine == e);
        public static Category Of(LayerKind k) => Of(LayerDefs.Get(k).Engine);
        public const string SoonColor = "#55555C";
    }

    public sealed class LayerDef
    {
        public LayerKind Kind { get; init; }
        public string Name { get; init; } = "";
        public string Group { get; init; } = "";
        public EngineKind Engine { get; init; }
        public string Hint { get; init; } = "";
        public ParamDef[] Params { get; init; } = Array.Empty<ParamDef>();
        public bool HasCurve { get; init; }
        /// <summary>Only one per monitor makes sense (the engine keeps just one value): the picker disables duplicates.</summary>
        public bool Unique { get; init; }
        /// <summary>Acts on exactly one monitor, chosen in the inspector (Monitor adjustment).</summary>
        public bool SingleMonitor { get; init; }
        public bool PerMonitor => Engine is not (EngineKind.Matrix or EngineKind.System);
    }

    public static class LayerDefs
    {
        /// <summary>Layer kinds that no longer exist (shader mode, removed in v9).</summary>
        public static bool Removed(LayerKind k) => k is LayerKind.ColorLookup or LayerKind.SelectiveColor or LayerKind.Sharpen or LayerKind.Scale;
        public static readonly string[] Channels = { "RGB", "Vermelho", "Verde", "Azul" };

        // Must be declared before All (static fields initialize in textual order).
        /// <summary>Choices for the hardware "temp" param, mapped to MCCS VCP 0x14 values.</summary>
        public static readonly string[] HardwareTemps = { "5000K (quente)", "6500K (padrão)", "7500K", "9300K (fria)", "Usuário" };
        public static readonly int[] HardwareTempVcp = { 0x04, 0x05, 0x06, 0x08, 0x0B };
        public static readonly string[] ColorBlindTypes = { "Protanopia (sem vermelho)", "Deuteranopia (sem verde)", "Tritanopia (sem azul)", "Acromatopsia (sem cor)" };
        public static readonly string[] GuideModes = { "Colunas", "Grade", "Centro", "Terços" };
        public static readonly string[] ThemeModes = { "Escuro", "Claro" };
        public static readonly string[] PatternTypes = { "Pontos", "Linhas", "Xadrez", "Scanlines", "Trama cruzada", "Diagonais" };

        public static readonly LayerDef[] All =
        {
            // ------- Light / Color and vibrance -------
            new() { Kind = LayerKind.BrightnessContrast, Name = "Brilho/Contraste", Group = "Light", Engine = EngineKind.Gamma,
                Params = new[] {
                    new ParamDef("brightness", "Brilho", ParamType.Slider, -100, 100),
                    new ParamDef("contrast", "Contraste", ParamType.Slider, -100, 100),
                } },
            new() { Kind = LayerKind.Vibrance, Name = "Vibração", Group = "Light", Engine = EngineKind.Driver, Unique = true,
                Hint = "Digital Vibrance da NVIDIA (o mesmo do VibranceGUI). 50% = normal.",
                Params = new[] {
                    new ParamDef("level", "Vibração", ParamType.Slider, 0, 100, 50, 1, Unit: "%"),
                } },

            // ------- Tonal (gamma) -------
            new() { Kind = LayerKind.Levels, Name = "Níveis", Group = "Tones", Engine = EngineKind.Gamma,
                Params = new[] {
                    new ParamDef("channel", "Canal", ParamType.Choice, Choices: Channels),
                    new ParamDef("inBlack", "Entrada: preto", ParamType.Slider, 0, 253, 0),
                    new ParamDef("gamma", "Entrada: meio-tom", ParamType.Slider, 0.10, 9.99, 1.0, 0.01),
                    new ParamDef("inWhite", "Entrada: branco", ParamType.Slider, 2, 255, 255),
                    new ParamDef("outBlack", "Saída: preto", ParamType.Slider, 0, 255, 0),
                    new ParamDef("outWhite", "Saída: branco", ParamType.Slider, 0, 255, 255),
                } },
            new() { Kind = LayerKind.Curves, Name = "Curvas", Group = "Tones", Engine = EngineKind.Gamma, HasCurve = true,
                Hint = "Clique para adicionar ponto, arraste para mover, clique direito para remover.",
                Params = new[] { new ParamDef("channel", "Canal", ParamType.Choice, Choices: Channels) } },
            new() { Kind = LayerKind.Exposure, Name = "Exposição", Group = "Tones", Engine = EngineKind.Gamma,
                Params = new[] {
                    new ParamDef("exposure", "Exposição", ParamType.Slider, -5, 5, 0, 0.01),
                    new ParamDef("offset", "Deslocamento", ParamType.Slider, -0.5, 0.5, 0, 0.001),
                    new ParamDef("gamma", "Correção de gama", ParamType.Slider, 0.01, 9.99, 1, 0.01),
                } },

            // ------- Color (matrix) -------
            new() { Kind = LayerKind.HueSaturation, Name = "Matiz/Saturação", Group = "Color", Engine = EngineKind.Matrix,
                Params = new[] {
                    new ParamDef("hue", "Matiz", ParamType.Slider, -180, 180, 0, 1, Unit: "°"),
                    new ParamDef("saturation", "Saturação", ParamType.Slider, -100, 100),
                    new ParamDef("lightness", "Luminosidade", ParamType.Slider, -100, 100),
                } },
            new() { Kind = LayerKind.ColorBalance, Name = "Equilíbrio de cores", Group = "Color", Engine = EngineKind.Matrix,
                Params = new[] {
                    new ParamDef("cr", "Ciano ↔ Vermelho", ParamType.Slider, -100, 100),
                    new ParamDef("mg", "Magenta ↔ Verde", ParamType.Slider, -100, 100),
                    new ParamDef("yb", "Amarelo ↔ Azul", ParamType.Slider, -100, 100),
                    new ParamDef("preserve", "Preservar luminosidade", ParamType.Toggle, 0, 1, 1),
                } },
            new() { Kind = LayerKind.BlackWhite, Name = "Preto e branco", Group = "Color", Engine = EngineKind.Matrix,
                Params = new[] {
                    new ParamDef("r", "Vermelhos", ParamType.Slider, -200, 300, 40, 1, Unit: "%"),
                    new ParamDef("g", "Verdes", ParamType.Slider, -200, 300, 40, 1, Unit: "%"),
                    new ParamDef("b", "Azuis", ParamType.Slider, -200, 300, 20, 1, Unit: "%"),
                    new ParamDef("tint", "Tonalizar", ParamType.Toggle, 0, 1, 0),
                    new ParamDef("tintColor", "Cor do tom", ParamType.Color, DefaultText: "#E1D3B3"),
                } },
            new() { Kind = LayerKind.PhotoFilter, Name = "Filtro de fotos", Group = "Color", Engine = EngineKind.Matrix,
                Params = new[] {
                    new ParamDef("color", "Cor do filtro", ParamType.Color, DefaultText: "#EC8A00"),
                    new ParamDef("density", "Densidade", ParamType.Slider, 1, 100, 25, 1, Unit: "%"),
                    new ParamDef("preserve", "Preservar luminosidade", ParamType.Toggle, 0, 1, 1),
                } },
            new() { Kind = LayerKind.ChannelMixer, Name = "Misturador de canais", Group = "Color", Engine = EngineKind.Matrix,
                Params = new[] {
                    new ParamDef("rr", "Saída R ← Vermelho", ParamType.Slider, -200, 200, 100, 1, Unit: "%"),
                    new ParamDef("rg", "Saída R ← Verde", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("rb", "Saída R ← Azul", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("rc", "Saída R constante", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("gr", "Saída G ← Vermelho", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("gg", "Saída G ← Verde", ParamType.Slider, -200, 200, 100, 1, Unit: "%"),
                    new ParamDef("gb", "Saída G ← Azul", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("gc", "Saída G constante", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("br", "Saída B ← Vermelho", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("bg", "Saída B ← Verde", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("bb", "Saída B ← Azul", ParamType.Slider, -200, 200, 100, 1, Unit: "%"),
                    new ParamDef("bc", "Saída B constante", ParamType.Slider, -200, 200, 0, 1, Unit: "%"),
                    new ParamDef("mono", "Monocromático (usa a linha R)", ParamType.Toggle, 0, 1, 0),
                } },

            // ------- Special -------
            new() { Kind = LayerKind.Invert, Name = "Inverter", Group = "Special", Engine = EngineKind.Matrix },
            new() { Kind = LayerKind.Posterize, Name = "Posterizar", Group = "Special", Engine = EngineKind.Gamma,
                Hint = "Efeito forte: o Windows pode recusar sem liberar a faixa de gama.",
                Params = new[] { new ParamDef("levels", "Níveis", ParamType.Slider, 2, 255, 4) } },
            new() { Kind = LayerKind.Threshold, Name = "Limiar", Group = "Special", Engine = EngineKind.Gamma,
                Hint = "Efeito forte: o Windows pode recusar sem liberar a faixa de gama.",
                Params = new[] { new ParamDef("level", "Limiar", ParamType.Slider, 1, 255, 128) } },

            // ------- Overlay -------
            new() { Kind = LayerKind.Grain, Name = "Granulação", Group = "Overlay", Engine = EngineKind.Overlay, Unique = true,
                Params = new[] {
                    new ParamDef("amount", "Quantidade", ParamType.Slider, 0, 100, 15),
                    new ParamDef("size", "Tamanho do grão", ParamType.Slider, 1, 4, 1, 1),
                    new ParamDef("fps", "Velocidade (quadros/s, 0 = parado)", ParamType.Slider, 0, 240, 60),
                    new ParamDef("color", "Grão colorido", ParamType.Toggle, 0, 1, 0),
                } },
            new() { Kind = LayerKind.SolidColor, Name = "Cor sólida", Group = "Overlay", Engine = EngineKind.Overlay,
                Params = new[] { new ParamDef("color", "Cor", ParamType.Color, DefaultText: "#FF8800") } },
            new() { Kind = LayerKind.Gradient, Name = "Degradê", Group = "Overlay", Engine = EngineKind.Overlay,
                Params = new[] {
                    new ParamDef("color1", "Cor inicial", ParamType.Color, DefaultText: "#2040FF"),
                    new ParamDef("alpha1", "Opacidade inicial", ParamType.Slider, 0, 100, 100, 1, Unit: "%"),
                    new ParamDef("color2", "Cor final", ParamType.Color, DefaultText: "#FF6020"),
                    new ParamDef("alpha2", "Opacidade final", ParamType.Slider, 0, 100, 0, 1, Unit: "%"),
                    new ParamDef("angle", "Ângulo", ParamType.Slider, -180, 180, 90, 1, Unit: "°"),
                } },
            new() { Kind = LayerKind.Vignette, Name = "Vinheta", Group = "Overlay", Engine = EngineKind.Overlay,
                Params = new[] {
                    new ParamDef("amount", "Intensidade", ParamType.Slider, 0, 100, 40),
                    new ParamDef("size", "Tamanho", ParamType.Slider, 0, 100, 60),
                    new ParamDef("feather", "Suavidade", ParamType.Slider, 1, 100, 60),
                } },

            // ------- Monitor hardware (DDC/CI) -------
            new() { Kind = LayerKind.MonitorHardware, Name = "Monitor", Group = "Monitor", Engine = EngineKind.Hardware, Unique = true, SingleMonitor = true,
                Hint = "Escolha o monitor e marque só o que este preset deve mudar. Ao sair do preset, o monitor volta ao que estava. Para o outro monitor, adicione outro ajuste Monitor.",
                Params = new[] {
                    new ParamDef("brightness", "Brilho", ParamType.Slider, 0, 100, 40, 1, Unit: "%", Optional: true),
                    new ParamDef("contrast", "Contraste", ParamType.Slider, 0, 100, 50, 1, Unit: "%", Optional: true),
                    new ParamDef("sharpness", "Nitidez", ParamType.Slider, 0, 100, 50, 1, Unit: "%", Optional: true),
                    new ParamDef("temp", "Temperatura de cor", ParamType.Choice, 0, 4, 1, Choices: HardwareTemps, Optional: true),
                    new ParamDef("gainR", "Ganho vermelho", ParamType.Slider, 0, 100, 100, 1, Unit: "%", Optional: true),
                    new ParamDef("gainG", "Ganho verde", ParamType.Slider, 0, 100, 100, 1, Unit: "%", Optional: true),
                    new ParamDef("gainB", "Ganho azul", ParamType.Slider, 0, 100, 100, 1, Unit: "%", Optional: true),
                    new ParamDef("blackR", "Nível de preto vermelho", ParamType.Slider, 0, 100, 50, 1, Unit: "%", Optional: true),
                    new ParamDef("blackG", "Nível de preto verde", ParamType.Slider, 0, 100, 50, 1, Unit: "%", Optional: true),
                    new ParamDef("blackB", "Nível de preto azul", ParamType.Slider, 0, 100, 50, 1, Unit: "%", Optional: true),
                    new ParamDef("volume", "Volume", ParamType.Slider, 0, 100, 50, 1, Unit: "%", Optional: true),
                } },

            // ------- v5 additions -------
            new() { Kind = LayerKind.Temperature, Name = "Temperatura", Group = "Tones", Engine = EngineKind.Gamma,
                Hint = "Como a Luz noturna, mas sob seu controle: 6500K = neutro, abaixo esquenta, acima esfria. Funciona em jogos tela cheia.",
                Params = new[] { new ParamDef("kelvin", "Temperatura", ParamType.Slider, 1900, 10000, 6500, 50, Unit: "K") } },
            new() { Kind = LayerKind.LiftGammaGain, Name = "Lift / Gamma / Gain", Group = "Tones", Engine = EngineKind.Gamma,
                Hint = "As três rodas de cor do DaVinci: sombras (lift), meios-tons (gamma) e altas luzes (gain), cada uma com nível e cor.",
                Params = new[] {
                    new ParamDef("liftLevel", "Sombras", ParamType.Slider, -100, 100),
                    new ParamDef("liftColor", "Cor das sombras", ParamType.Color, DefaultText: "#808080"),
                    new ParamDef("liftTint", "Força da cor nas sombras", ParamType.Slider, 0, 100, 0, 1, Unit: "%"),
                    new ParamDef("gammaLevel", "Meios-tons", ParamType.Slider, -100, 100),
                    new ParamDef("gammaColor", "Cor dos meios-tons", ParamType.Color, DefaultText: "#808080"),
                    new ParamDef("gammaTint", "Força da cor nos meios-tons", ParamType.Slider, 0, 100, 0, 1, Unit: "%"),
                    new ParamDef("gainLevel", "Altas luzes", ParamType.Slider, -100, 100),
                    new ParamDef("gainColor", "Cor das altas luzes", ParamType.Color, DefaultText: "#808080"),
                    new ParamDef("gainTint", "Força da cor nas altas luzes", ParamType.Slider, 0, 100, 0, 1, Unit: "%"),
                } },
            new() { Kind = LayerKind.ColorBlind, Name = "Simular daltonismo", Group = "Color", Engine = EngineKind.Matrix,
                Hint = "Mostra a tela como uma pessoa com daltonismo a veria (matrizes de Machado et al., 2009). A opacidade controla a severidade.",
                Params = new[] { new ParamDef("type", "Tipo", ParamType.Choice, 0, 3, 1, Choices: ColorBlindTypes) } },
            new() { Kind = LayerKind.Guides, Name = "Guias e grade", Group = "Overlay", Engine = EngineKind.Overlay,
                Hint = "Linhas de referência por cima de qualquer programa, para conferir alinhamento. Não aparecem em prints quando “Esconder de prints” está ligado.",
                Params = new[] {
                    new ParamDef("mode", "Tipo", ParamType.Choice, 0, 3, 0, Choices: GuideModes),
                    new ParamDef("columns", "Colunas", ParamType.Slider, 1, 24, 12),
                    new ParamDef("gutter", "Espaço entre colunas", ParamType.Slider, 0, 80, 24, 1, Unit: "px"),
                    new ParamDef("margin", "Margem lateral", ParamType.Slider, 0, 480, 80, 1, Unit: "px"),
                    new ParamDef("grid", "Tamanho da grade", ParamType.Slider, 4, 128, 8, 1, Unit: "px"),
                    new ParamDef("fill", "Preencher colunas", ParamType.Toggle, 0, 1, 1),
                    new ParamDef("color", "Cor", ParamType.Color, DefaultText: "#FF4D6A"),
                } },
            new() { Kind = LayerKind.Pattern, Name = "Padrão", Group = "Overlay", Engine = EngineKind.Overlay,
                Params = new[] {
                    new ParamDef("type", "Tipo", ParamType.Choice, 0, 5, 0, Choices: PatternTypes),
                    new ParamDef("size", "Tamanho", ParamType.Slider, 2, 64, 6, 1, Unit: "px"),
                    new ParamDef("thickness", "Espessura", ParamType.Slider, 1, 32, 1, 1, Unit: "px"),
                    new ParamDef("color", "Cor", ParamType.Color, DefaultText: "#000000"),
                } },
            new() { Kind = LayerKind.GradientMap, Name = "Mapa de degradê", Group = "Color", Engine = EngineKind.Gamma,
                Hint = "Efeito forte: o Windows pode recusar sem liberar a faixa de gama. A parte preto e branco vale para todos os monitores; em tela cheia exclusiva só a parte de cor funciona.",
                Params = new[] {
                    new ParamDef("color1", "Sombras", ParamType.Color, DefaultText: "#1B1036"),
                    new ParamDef("mid", "Usar cor nos meios-tons", ParamType.Toggle, 0, 1, 0),
                    new ParamDef("color2", "Meios-tons", ParamType.Color, DefaultText: "#C2457A"),
                    new ParamDef("color3", "Altas luzes", ParamType.Color, DefaultText: "#FFD9A0"),
                    new ParamDef("reverse", "Inverter", ParamType.Toggle, 0, 1, 0),
                } },
            new() { Kind = LayerKind.WindowsTheme, Name = "Tema do Windows", Group = "Sistema", Engine = EngineKind.System, Unique = true,
                Hint = "Troca o tema claro/escuro do Windows. Combine com a agenda: claro de dia, escuro à noite. Ao sair do preset, volta ao tema que estava.",
                Params = new[] {
                    new ParamDef("theme", "Tema", ParamType.Choice, 0, 1, 0, Choices: ThemeModes),
                    new ParamDef("apps", "Aplicativos", ParamType.Toggle, 0, 1, 1),
                    new ParamDef("system", "Barra de tarefas e menus", ParamType.Toggle, 0, 1, 1),
                } },
        };


        public static LayerDef Get(LayerKind k) => All.First(d => d.Kind == k);

        /// <summary>Monitor adjustment param → MCCS VCP code (to hide what a monitor doesn't support and to read current values).</summary>
        public static readonly Dictionary<string, byte> HardwareCodes = new()
        {
            ["brightness"] = 0x10, ["contrast"] = 0x12, ["sharpness"] = 0x87, ["temp"] = 0x14,
            ["gainR"] = 0x16, ["gainG"] = 0x18, ["gainB"] = 0x1A,
            ["blackR"] = 0x6C, ["blackG"] = 0x6E, ["blackB"] = 0x70, ["volume"] = 0x62,
        };

        /// <summary>
        /// Why this kind can't be added again to this list (null = allowed). Unique kinds keep only one
        /// value per monitor in their engine, so a second one would silently cancel the first.
        /// </summary>
        public static string? BlockReason(LayerKind kind, IEnumerable<Layer> existing, IReadOnlyList<string> monitors)
        {
            var def = Get(kind);
            if (!def.Unique) return null;
            var same = existing.Where(l => l.Kind == kind).ToList();
            if (same.Count == 0) return null;
            if (!def.PerMonitor) return $"Já existe um ajuste “{def.Name}” aqui. Só pode haver um.";
            var free = FreeMonitors(kind, same, monitors);
            if (free.Count > 0) return null;
            return def.SingleMonitor
                ? "Todos os monitores já têm um ajuste Monitor aqui. Edite o que já existe."
                : $"Já existe “{def.Name}” para todos os monitores aqui. Mude os monitores do ajuste existente se precisar separar.";
        }

        /// <summary>Monitors not yet covered by a unique kind (the new layer gets these).</summary>
        public static List<string> FreeMonitors(LayerKind kind, IEnumerable<Layer> sameKind, IReadOnlyList<string> monitors)
            => monitors.Where(m => !sameKind.Any(l => l.Kind == kind && l.AppliesTo(m))).ToList();
    }

    public sealed class Layer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public LayerKind Kind { get; set; }
        public string? Name { get; set; }
        public bool Enabled { get; set; } = true;
        public double Opacity { get; set; } = 100;
        /// <summary>GDI device names ("\\.\DISPLAY1"). Empty = all monitors.</summary>
        public List<string> Monitors { get; set; } = new();
        /// <summary>Legacy (v1) single-monitor field, migrated into <see cref="Monitors"/> on load.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Monitor { get; set; }
        public Dictionary<string, double> P { get; set; } = new();
        public Dictionary<string, string> S { get; set; } = new();
        /// <summary>Curves: channel index ("0".."3") -> list of [x,y] in 0..255.</summary>
        public Dictionary<string, List<double[]>> Curves { get; set; } = new();

        public static Layer Create(LayerKind kind)
        {
            var l = new Layer { Kind = kind };
            foreach (var p in LayerDefs.Get(kind).Params)
            {
                if (p.Type == ParamType.Color) l.S[p.Key] = p.DefaultText ?? "#FFFFFF";
                else l.P[p.Key] = p.Default;
            }
            if (kind == LayerKind.SolidColor) l.Opacity = 10;
            if (kind == LayerKind.Gradient) l.Opacity = 15;
            if (kind == LayerKind.Guides) l.Opacity = 35;
            if (kind == LayerKind.Pattern) l.Opacity = 15;
            return l;
        }

        public double Get(string key)
        {
            if (P.TryGetValue(key, out var v)) return v;
            var def = LayerDefs.Get(Kind).Params.FirstOrDefault(p => p.Key == key);
            return def?.Default ?? 0;
        }

        public string GetS(string key)
        {
            if (S.TryGetValue(key, out var v)) return v;
            var def = LayerDefs.Get(Kind).Params.FirstOrDefault(p => p.Key == key);
            return def?.DefaultText ?? "#FFFFFF";
        }

        public List<double[]> GetCurve(int channel)
        {
            if (!Curves.TryGetValue(channel.ToString(), out var pts) || pts.Count < 2)
            {
                pts = new List<double[]> { new double[] { 0, 0 }, new double[] { 255, 255 } };
                Curves[channel.ToString()] = pts;
            }
            return pts;
        }

        public bool AllMonitors => Monitors.Count == 0;

        public bool AppliesTo(string device)
            => !LayerDefs.Get(Kind).PerMonitor || Monitors.Count == 0 || Monitors.Any(m => string.Equals(m, device, StringComparison.OrdinalIgnoreCase));

        public bool IsOn(string key) => Get(key + "_on") > 0.5;

        public void Migrate()
        {
            if (Monitor != null)
            {
                if (Monitor != "*" && Monitors.Count == 0) Monitors.Add(Monitor);
                Monitor = null;
            }
        }

        public Layer Clone() => CloneKeepId(newId: true);

        /// <summary>Deep copy. With newId=false the copy keeps the Id (used for the effective stack, so errors map back).</summary>
        public Layer CloneKeepId(bool newId = false)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            var c = System.Text.Json.JsonSerializer.Deserialize<Layer>(json)!;
            if (newId) c.Id = Guid.NewGuid();
            return c;
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? AutoName : Name!;

        /// <summary>Name when you haven't given one: the kind's name, plus the monitor for one-monitor adjustments ("Monitor · DELL P2418D").</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string AutoName
        {
            get
            {
                var def = LayerDefs.Get(Kind);
                if (!def.SingleMonitor || Monitors.Count != 1) return def.Name;
                var mon = Native.MonitorNames.Get(Monitors[0]);
                return mon.Length > 0 ? $"{def.Name} · {mon}" : def.Name;
            }
        }
    }

    public sealed class Preset
    {
        public string Name { get; set; } = "Novo preset";
        /// <summary>Bottom-first order, like Photoshop: index 0 is applied first.</summary>
        public List<Layer> Layers { get; set; } = new();
        /// <summary>Legacy (v2) program list, migrated into Settings.AppRules.</summary>
        public List<string> Apps { get; set; } = new();
    }

    /// <summary>A block on the day timeline. Times are minutes from midnight; End &lt; Start means it crosses midnight.</summary>
    public sealed class ScheduleBlock
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Preset { get; set; } = "";
        public int Start { get; set; } = 22 * 60;
        public int End { get; set; } = 7 * 60;
        /// <summary>Bit 0 = Monday … bit 6 = Sunday. The day a block belongs to is the day it starts.</summary>
        public int Days { get; set; } = 0x7F;
        /// <summary>Fade in after Start and fade out before End, in minutes.</summary>
        public int Fade { get; set; } = 20;
        public bool Enabled { get; set; } = true;

        public int Length => End > Start ? End - Start : End + 1440 - Start;
        public bool OnDay(DayOfWeek d) => (Days & (1 << DayIndex(d))) != 0;
        public static int DayIndex(DayOfWeek d) => ((int)d + 6) % 7; // Monday = 0
    }

    public sealed class Settings
    {
        public List<Preset> Presets { get; set; } = new();
        /// <summary>The local (manual) preset — the base of the stack.</summary>
        public string ActivePreset { get; set; } = "";
        public bool MasterEnabled { get; set; } = true;
        public bool ScheduleEnabled { get; set; } = true;
        /// <summary>The local preset's layers on/off, independent of the agenda (the "Camadas" switch).</summary>
        public bool LayersEnabled { get; set; } = true;
        public DateTime? SchedulePausedUntil { get; set; }
        /// <summary>v2 timeline blocks — migrated into <see cref="Profiles"/> on load (v3).</summary>
        public List<ScheduleBlock> Schedule { get; set; } = new();
        /// <summary>Agenda day profiles, each with automated tracks.</summary>
        public List<DayProfile> Profiles { get; set; } = new();
        /// <summary>Agenda profile of each weekday (0 = Monday). A day belongs to one profile at most; Guid.Empty = no agenda that day.</summary>
        public Guid[] Week { get; set; } = new Guid[7];
        /// <summary>Legacy (v5): profile chosen by hand. Read only by the v6 migration.</summary>
        public Guid ActiveProfile { get; set; }
        /// <summary>Profile forced by hand instead of the weekday's one, until <see cref="ForcedUntil"/> (next midnight).</summary>
        public Guid? ForcedProfile { get; set; }
        public DateTime? ForcedUntil { get; set; }
        public List<AppRule> AppRules { get; set; } = new();
        public Location? Location { get; set; }
        public bool TimelineExpanded { get; set; } = true;
        public double AgendaHeight { get; set; } = 260;
        public int Version { get; set; }
        public bool HelpVisible { get; set; } = true;
        public bool AutoSwitch { get; set; } = true;
        public bool HideOverlayFromCapture { get; set; } = true;
        public bool StartMinimized { get; set; }
        public bool CloseToTray { get; set; } = true;
    }
}
