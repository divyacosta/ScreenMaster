using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenMaster.Core
{
    public enum Anchor { None, Sunrise, Sunset }

    /// <summary>An automation breakpoint. T = minutes from midnight (0..1440).</summary>
    public sealed class Keyframe
    {
        public double T { get; set; }
        public double V { get; set; }
        /// <summary>Bend of the segment that starts at this point: -1..1 (0 = straight).</summary>
        public double Curve { get; set; }
        public Anchor Anchor { get; set; }
        /// <summary>Minutes from the anchor (sunrise/sunset) when anchored.</summary>
        public double Offset { get; set; }
    }

    /// <summary>Automation of one parameter of a track's layer ("opacity" or a ParamDef key).</summary>
    public sealed class Lane
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Param { get; set; } = "opacity";
        public List<Keyframe> Points { get; set; } = new();
        /// <summary>Ableton-style override: you moved the value by hand; automation paused until "Voltar à automação".</summary>
        public bool Overridden { get; set; }
        /// <summary>Weekdays this lane runs on: bit 0 = Monday … bit 6 = Sunday. The same parameter may have several lanes for different days.</summary>
        public int Days { get; set; } = 0x7F;

        public bool OnDay(DayOfWeek d) => (Days & (1 << ScheduleBlock.DayIndex(d))) != 0;
    }

    /// <summary>A layer living on the agenda, with automation lanes for its parameters.</summary>
    public sealed class AgendaTrack
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Layer Layer { get; set; } = new();
        public List<Lane> Lanes { get; set; } = new();
        public bool Collapsed { get; set; }
    }

    /// <summary>Automation lanes attached to a layer of a preset (by layer id). Acts only while that preset is the local one.</summary>
    public sealed class LinkedAutomation
    {
        public Guid LayerId { get; set; }
        public List<Lane> Lanes { get; set; } = new();
    }

    /// <summary>A day profile ("Dias úteis", "Fim de semana"): its own tracks + automation of preset layers.</summary>
    public sealed class DayProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Todos os dias";
        public string Color { get; set; } = "#3B8EEA";
        public List<AgendaTrack> Tracks { get; set; } = new();
        public List<LinkedAutomation> Linked { get; set; } = new();

        public LinkedAutomation? LinkedFor(Guid layerId) => Linked.FirstOrDefault(l => l.LayerId == layerId);

        /// <summary>The lane of this parameter that runs today (or the first one, if none runs today).</summary>
        public Lane? LaneFor(Guid layerId, string param)
        {
            var lanes = LinkedFor(layerId)?.Lanes.Where(l => l.Param == param).ToList();
            if (lanes == null || lanes.Count == 0) return null;
            return lanes.LastOrDefault(l => l.OnDay(DateTime.Now.DayOfWeek)) ?? lanes[0];
        }

        public Lane AddLinked(Guid layerId, string param)
        {
            var la = LinkedFor(layerId);
            if (la == null) { la = new LinkedAutomation { LayerId = layerId }; Linked.Add(la); }
            var lane = la.Lanes.FirstOrDefault(l => l.Param == param);
            if (lane == null) { lane = new Lane { Param = param }; la.Lanes.Add(lane); }
            return lane;
        }

        public void RemoveLinked(Guid layerId, string param)
        {
            var la = LinkedFor(layerId);
            if (la == null) return;
            la.Lanes.RemoveAll(l => l.Param == param);
            if (la.Lanes.Count == 0) Linked.Remove(la);
        }
    }

    public sealed class AppRule
    {
        public string Process { get; set; } = "";
        /// <summary>Preset to use as local while the program is in front; null = keep the local one.</summary>
        public string? Preset { get; set; }
        public bool PauseAgenda { get; set; }
        /// <summary>Agenda profile to switch to while the program is in front; null = keep the chosen one.</summary>
        public Guid? Profile { get; set; }
        /// <summary>Scale this program's window to the whole screen while it is in front (modo escala).</summary>
        public bool Scale { get; set; }
    }

    public sealed class Location
    {
        public string Name { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    public static class Automation
    {
        /// <summary>Profile identity colors. No amber: amber means "live / now" everywhere in the app.</summary>
        public static readonly string[] ProfileColors = { ThemeColor, "#3B8EEA", "#2E9E6B", "#C94F7C", "#8A5CE6", "#2AA7B8", "#7A8FA6" };
        /// <summary>The app's theme color (lime): the default for profiles.</summary>
        public const string ThemeColor = "#C6F24E";
        public static readonly string[] ProfileColorNames = { "Tema", "Azul", "Verde", "Rosa", "Roxo", "Turquesa", "Cinza" };

        /// <summary>Default color for a new profile: the theme color, or the first one no profile uses yet.</summary>
        public static string NextProfileColor(IEnumerable<DayProfile> existing)
            => ProfileColors.FirstOrDefault(c => existing.All(o => !o.Color.Equals(c, StringComparison.OrdinalIgnoreCase))) ?? ThemeColor;

        /// <summary>Range/kind of an automatable parameter. Colors and curves aren't automatable.</summary>
        public static (double min, double max, bool stepped, string unit)? Range(LayerKind kind, string param)
        {
            if (param == "opacity") return (0, 100, false, "%");
            var p = LayerDefs.Get(kind).Params.FirstOrDefault(x => x.Key == param);
            if (p == null) return null;
            return p.Type switch
            {
                ParamType.Slider => (p.Min, p.Max, false, p.Unit),
                ParamType.Toggle => (0, 1, true, ""),
                ParamType.Choice => (0, (p.Choices?.Length ?? 1) - 1, true, ""),
                _ => null,
            };
        }

        public static IEnumerable<(string key, string label)> Automatable(LayerKind kind)
        {
            yield return ("opacity", "Opacidade");
            foreach (var p in LayerDefs.Get(kind).Params)
                if (p.Type is ParamType.Slider or ParamType.Toggle || (p.Type == ParamType.Choice && p.Key is not ("channel" or "range")))
                    yield return (p.Key, p.Label);
        }

        public static string Label(LayerKind kind, string param)
            => param == "opacity" ? "Opacidade" : LayerDefs.Get(kind).Params.FirstOrDefault(p => p.Key == param)?.Label ?? param;

        public static string Format(LayerKind kind, string param, double v)
        {
            if (param == "opacity") return $"{v:0}%";
            var p = LayerDefs.Get(kind).Params.FirstOrDefault(x => x.Key == param);
            if (p == null) return v.ToString("0.##");
            return p.Type switch
            {
                ParamType.Toggle => v >= 0.5 ? "ligado" : "desligado",
                ParamType.Choice => p.Choices != null && (int)Math.Round(v) is var i && i >= 0 && i < p.Choices.Length ? p.Choices[i] : v.ToString("0"),
                _ => (p.Step < 1 ? v.ToString("0.00") : v.ToString("0")) + p.Unit,
            };
        }

        /// <summary>Actual time of a keyframe on a given date (anchors resolve against sunrise/sunset).</summary>
        public static double TimeOf(Keyframe k, DateTime date, Location? loc)
        {
            if (k.Anchor == Anchor.None || loc == null) return k.T;
            var sun = Sun.Times(date, loc.Lat, loc.Lon);
            if (sun == null) return k.T;
            double baseT = k.Anchor == Anchor.Sunrise ? sun.Value.rise : sun.Value.set;
            return ((baseT + k.Offset) % 1440 + 1440) % 1440;
        }

        static double Shape(double t, double curve)
        {
            if (Math.Abs(curve) < 1e-3) return t;
            double e = Math.Pow(4, -curve); // curve>0 bows up (fast start), <0 bows down
            return Math.Pow(t, e);
        }

        /// <summary>Value of a lane at minute-of-day (24h loop: the end of the day wraps to its start).</summary>
        public static double? Eval(Lane lane, double minute, DateTime date, Location? loc, bool stepped)
        {
            if (lane.Points.Count == 0) return null;
            var pts = lane.Points.Select(k => (t: TimeOf(k, date, loc), k)).OrderBy(p => p.t).ToList();
            if (pts.Count == 1) return pts[0].k.V;
            // segment [a, b] around minute; the day wraps (last point connects to the first of the next day)
            int next = pts.FindIndex(p => p.t > minute);
            (double t, Keyframe k) a, b;
            double ta, tb;
            if (next == -1) { a = pts[^1]; ta = a.t; b = pts[0]; tb = b.t + 1440; }
            else if (next == 0) { a = pts[^1]; ta = a.t - 1440; b = pts[0]; tb = b.t; }
            else { a = pts[next - 1]; ta = a.t; b = pts[next]; tb = b.t; }
            if (stepped) return a.k.V;
            double span = Math.Max(1e-6, tb - ta);
            double u = Math.Clamp((minute - ta) / span, 0, 1);
            return a.k.V + (b.k.V - a.k.V) * Shape(u, a.k.Curve);
        }

        /// <summary>Evaluated copy of a track's layer at a time (overridden lanes keep the manual value).</summary>
        public static Layer EvalTrack(AgendaTrack tr, DateTime when, Location? loc, bool hardwarePerMinute)
            => EvalLayer(tr.Layer, tr.Lanes, when, loc, hardwarePerMinute);

        /// <summary>Evaluated copy of any layer under a set of lanes.</summary>
        public static Layer EvalLayer(Layer source, IEnumerable<Lane> lanes, DateTime when, Location? loc, bool hardwarePerMinute)
        {
            var l = source.CloneKeepId();
            double minute = when.Hour * 60 + when.Minute + when.Second / 60.0;
            foreach (var lane in ActiveLanes(lanes, when.DayOfWeek))
            {
                if (lane.Overridden) continue;
                var r = Range(l.Kind, lane.Param);
                if (r == null) continue;
                double m = l.Kind == LayerKind.MonitorHardware && hardwarePerMinute ? Math.Floor(minute) : minute;
                var v = Eval(lane, m, when.Date, loc, r.Value.stepped);
                if (v == null) continue;
                double val = Math.Clamp(v.Value, r.Value.min, r.Value.max);
                if (lane.Param == "opacity") l.Opacity = val;
                else
                {
                    l.P[lane.Param] = r.Value.stepped ? Math.Round(val) : val;
                    if (LayerDefs.Get(l.Kind).Params.FirstOrDefault(p => p.Key == lane.Param)?.Optional == true) l.P[lane.Param + "_on"] = 1;
                }
            }
            return l;
        }

        /// <summary>
        /// The agenda profile acting at this moment: a program rule (while that program is in front),
        /// else one forced by hand until midnight, else the weekday's profile. Null = no agenda that day.
        /// </summary>
        public static DayProfile? ProfileFor(Settings s, DateTime date, Guid? overrideId = null)
        {
            if (s.Profiles.Count == 0) return null;
            DayProfile? Find(Guid id) => id == Guid.Empty ? null : s.Profiles.FirstOrDefault(p => p.Id == id);
            if (overrideId is Guid o && Find(o) is { } op) return op;
            if (s.ForcedProfile is Guid f && s.ForcedUntil is DateTime until && date < until && Find(f) is { } fp) return fp;
            return DayOwner(s, ScheduleBlock.DayIndex(date.DayOfWeek));
        }

        /// <summary>Profile that owns a weekday (0 = Monday), or null.</summary>
        public static DayProfile? DayOwner(Settings s, int day)
            => s.Week is { Length: 7 } w && w[day] != Guid.Empty ? s.Profiles.FirstOrDefault(p => p.Id == w[day]) : null;

        /// <summary>Weekday mask (bit 0 = Monday) of a profile.</summary>
        public static int DaysOf(Settings s, DayProfile p)
        {
            int m = 0;
            for (int d = 0; d < 7; d++) if (s.Week is { Length: 7 } w && w[d] == p.Id) m |= 1 << d;
            return m;
        }

        /// <summary>True while a profile is forced by hand (until midnight).</summary>
        public static bool IsForced(Settings s, DateTime now)
            => s.ForcedProfile is Guid f && s.ForcedUntil is DateTime u && now < u && s.Profiles.Any(p => p.Id == f);

        /// <summary>
        /// Lanes that run on this weekday: per parameter, the lanes whose day chips include it.
        /// Several lanes of the same parameter on the same day is a conflict; the last one wins (the UI warns).
        /// </summary>
        public static IEnumerable<Lane> ActiveLanes(IEnumerable<Lane> lanes, DayOfWeek day)
        {
            var byParam = new Dictionary<string, Lane>();
            foreach (var lane in lanes)
                if (lane.OnDay(day)) byParam[lane.Param] = lane;
            return byParam.Values;
        }

        /// <summary>Lanes of the same parameter that share at least one weekday with this lane.</summary>
        public static IEnumerable<Lane> Conflicts(IEnumerable<Lane> lanes, Lane lane)
            => lanes.Where(l => l != lane && l.Param == lane.Param && (l.Days & lane.Days) != 0);

        public static readonly string[] DayLetters = { "S", "T", "Q", "Q", "S", "S", "D" };
        public static readonly string[] DayNames = { "Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado", "Domingo" };

        public static string DaysText(int mask)
        {
            if ((mask & 0x7F) == 0x7F) return "todos os dias";
            if (mask == 0x1F) return "seg a sex";
            if (mask == 0x60) return "fim de semana";
            if (mask == 0) return "nenhum dia";
            return string.Join(", ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => DayNames[i].Substring(0, 3).ToLowerInvariant()));
        }

        /// <summary>Converts a v2 timeline block into tracks with an opacity ramp (fade in/out).</summary>
        public static IEnumerable<AgendaTrack> FromBlock(ScheduleBlock b, Preset p)
        {
            double fade = Math.Min(b.Fade, b.Length / 2.0);
            foreach (var layer in p.Layers)
            {
                var l = layer.Clone();
                double op = l.Opacity;
                var lane = new Lane { Param = "opacity" };
                double s = b.Start, e = b.Start + b.Length;
                void K(double t, double v) => lane.Points.Add(new Keyframe { T = ((t % 1440) + 1440) % 1440, V = v });
                K(s, 0); K(s + fade, op); K(e - fade, op); K(e, 0);
                yield return new AgendaTrack { Layer = l, Lanes = { lane } };
            }
        }
    }

    /// <summary>Sunrise / sunset (NOAA approximation), local minutes from midnight.</summary>
    public static class Sun
    {
        public static (double rise, double set)? Times(DateTime date, double lat, double lon)
        {
            int n = date.DayOfYear;
            double gamma = 2 * Math.PI / 365 * (n - 1);
            double eqTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma) - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
            double decl = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma) - 0.006758 * Math.Cos(2 * gamma)
                        + 0.000907 * Math.Sin(2 * gamma) - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
            double latR = lat * Math.PI / 180;
            double cosH = (Math.Cos(90.833 * Math.PI / 180) / (Math.Cos(latR) * Math.Cos(decl))) - Math.Tan(latR) * Math.Tan(decl);
            if (cosH < -1 || cosH > 1) return null; // polar day/night
            double ha = Math.Acos(cosH) * 180 / Math.PI;
            double tz = TimeZoneInfo.Local.GetUtcOffset(date.Date.AddHours(12)).TotalMinutes;
            double rise = 720 - 4 * (lon + ha) - eqTime + tz;
            double set = 720 - 4 * (lon - ha) - eqTime + tz;
            return (((rise % 1440) + 1440) % 1440, ((set % 1440) + 1440) % 1440);
        }

        public static readonly Location[] Cities =
        {
            new() { Name = "São Paulo", Lat = -23.55, Lon = -46.63 }, new() { Name = "Rio de Janeiro", Lat = -22.91, Lon = -43.17 },
            new() { Name = "Belo Horizonte", Lat = -19.92, Lon = -43.94 }, new() { Name = "Brasília", Lat = -15.79, Lon = -47.88 },
            new() { Name = "Curitiba", Lat = -25.43, Lon = -49.27 }, new() { Name = "Porto Alegre", Lat = -30.03, Lon = -51.23 },
            new() { Name = "Florianópolis", Lat = -27.59, Lon = -48.55 }, new() { Name = "Salvador", Lat = -12.97, Lon = -38.50 },
            new() { Name = "Recife", Lat = -8.05, Lon = -34.88 }, new() { Name = "Fortaleza", Lat = -3.73, Lon = -38.52 },
            new() { Name = "Belém", Lat = -1.46, Lon = -48.49 }, new() { Name = "Manaus", Lat = -3.12, Lon = -60.02 },
            new() { Name = "Goiânia", Lat = -16.68, Lon = -49.25 }, new() { Name = "Campinas", Lat = -22.91, Lon = -47.06 },
            new() { Name = "Vitória", Lat = -20.32, Lon = -40.34 }, new() { Name = "Natal", Lat = -5.79, Lon = -35.21 },
            new() { Name = "Lisboa", Lat = 38.72, Lon = -9.14 }, new() { Name = "Porto", Lat = 41.15, Lon = -8.61 },
            new() { Name = "Londres", Lat = 51.51, Lon = -0.13 }, new() { Name = "Nova York", Lat = 40.71, Lon = -74.01 },
            new() { Name = "Miami", Lat = 25.76, Lon = -80.19 }, new() { Name = "Buenos Aires", Lat = -34.60, Lon = -58.38 },
            new() { Name = "Santiago", Lat = -33.45, Lon = -70.67 }, new() { Name = "Cidade do México", Lat = 19.43, Lon = -99.13 },
            new() { Name = "Madri", Lat = 40.42, Lon = -3.70 }, new() { Name = "Paris", Lat = 48.86, Lon = 2.35 },
            new() { Name = "Berlim", Lat = 52.52, Lon = 13.40 }, new() { Name = "Tóquio", Lat = 35.68, Lon = 139.69 },
        };
    }
}
