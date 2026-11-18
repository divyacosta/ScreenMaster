using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ScreenMaster.Core
{
    /// <summary>
    /// What is on screen and why. Two independent tracks:
    ///   Local  — the preset you picked (or the one a program rule switched in)
    ///   Agenda — the day profile's automated tracks, evaluated at the current (or simulated) time
    /// </summary>
    public sealed class StackState
    {
        public Preset Local { get; init; } = null!;
        public string? LocalApp { get; init; }
        public DayProfile? Profile { get; init; }
        public List<(AgendaTrack track, Layer layer)> Agenda { get; init; } = new();
        public bool AgendaOff { get; init; }
        public bool LayersOff { get; init; }
        public string? AgendaPausedReason { get; init; }
        public DateTime When { get; init; }
        public bool Simulated { get; init; }
        public bool Bypass { get; init; }
        public List<Layer> Effective { get; init; } = new();
        public string Signature { get; init; } = "";

        public string Describe()
        {
            if (Local == null) return "";
            var s = Local.Name + (LocalApp != null ? $" ({LocalApp})" : "");
            if (Agenda.Count > 0) s += $" + agenda {Profile?.Name}";
            if (AgendaPausedReason != null) s += $" · agenda pausada ({AgendaPausedReason})";
            if (Bypass) s += " · antes/depois";
            return s;
        }
    }

    public static class Stack
    {
        public static bool AgendaRunning(Settings s, DateTime now)
            => s.ScheduleEnabled && !(s.SchedulePausedUntil is DateTime p && p > now);

        /// <param name="when">Time to evaluate the agenda at (the simulated time while scrubbing).</param>
        /// <param name="pausedByApp">Name of the program whose rule pauses the agenda, if any.</param>
        public static StackState Resolve(Settings s, Preset local, string? localApp, string? pausedByApp, DateTime when, bool simulated, bool bypass, Guid? profileOverride = null, DateTime? hardwareWhen = null)
        {
            var real = DateTime.Now;
            var eff = new List<Layer>();

            var agenda = new List<(AgendaTrack, Layer)>();
            string? pausedReason = null;
            bool running = AgendaRunning(s, real) || simulated;
            if (!running && s.ScheduleEnabled) pausedReason = $"até {s.SchedulePausedUntil:HH:mm}";
            if (pausedByApp != null && !simulated) { pausedReason = pausedByApp; running = false; }

            var profile = Automation.ProfileFor(s, when, profileOverride);
            var linkedSig = new StringBuilder();

            // Local preset — its layers may be automated by the agenda (linked automation).
            // the Camadas switch turns the local preset off without touching the agenda
            foreach (var l in s.LayersEnabled ? local.Layers : new List<Layer>())
            {
                var linked = running ? profile?.LinkedFor(l.Id) : null;
                if (linked == null || linked.Lanes.Count == 0) { eff.Add(l.CloneKeepId()); continue; }
                var t = simulated && l.Kind == LayerKind.MonitorHardware ? hardwareWhen ?? real : when;
                var ev = Automation.EvalLayer(l, linked.Lanes, t, s.Location, hardwarePerMinute: true);
                eff.Add(ev);
                linkedSig.Append('|').Append(ev.Id).Append(':').Append(ev.Opacity.ToString("F1", CultureInfo.InvariantCulture));
                foreach (var kv in ev.P.OrderBy(k => k.Key)) linkedSig.Append(',').Append(kv.Value.ToString("F2", CultureInfo.InvariantCulture));
            }
            if (running && profile != null)
            {
                foreach (var tr in profile.Tracks)
                {
                    if (!tr.Layer.Enabled) continue;
                    // a track only acts on the weekdays its lanes run on
                    if (!tr.Lanes.Any(l => l.OnDay(when.DayOfWeek))) continue;
                    // Scrubbing never drives the monitor's own hardware (slow, writes to its memory); a pinned time does.
                    var t = simulated && tr.Layer.Kind == LayerKind.MonitorHardware ? hardwareWhen ?? real : when;
                    var ev = Automation.EvalTrack(tr, t, s.Location, hardwarePerMinute: true);
                    agenda.Add((tr, ev));
                    eff.Add(ev);
                }
            }

            // before/after: keep what is slow or global to flip (monitor hardware, Windows theme)
            if (bypass) eff = eff.Where(l => l.Kind is LayerKind.MonitorHardware or LayerKind.WindowsTheme).ToList();

            var sig = new StringBuilder();
            sig.Append(local.Name).Append('|').Append(s.LayersEnabled).Append('|').Append(localApp).Append('|').Append(profile?.Id).Append('|').Append(bypass).Append(linkedSig);
            foreach (var (_, l) in agenda)
            {
                sig.Append('|').Append(l.Id).Append(':').Append(l.Opacity.ToString("F1", CultureInfo.InvariantCulture));
                foreach (var kv in l.P.OrderBy(k => k.Key)) sig.Append(',').Append(kv.Value.ToString("F2", CultureInfo.InvariantCulture));
            }

            return new StackState
            {
                Local = local, LocalApp = localApp, Profile = profile, Agenda = agenda,
                AgendaOff = !s.ScheduleEnabled, LayersOff = !s.LayersEnabled, AgendaPausedReason = pausedReason,
                When = when, Simulated = simulated, Bypass = bypass,
                Effective = eff, Signature = sig.ToString(),
            };
        }
    }
}
