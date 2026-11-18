using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ScreenMaster.Core
{
    /// <summary>
    /// Snapshot undo/redo over everything you edit (presets, agenda, week, rules, location).
    /// A snapshot is taken when edits settle (the debounced save), so one slider drag = one step.
    /// </summary>
    public sealed class UndoStack
    {
        sealed class Snapshot
        {
            public List<Preset> Presets { get; set; } = new();
            public List<DayProfile> Profiles { get; set; } = new();
            public Guid[] Week { get; set; } = new Guid[7];
            public List<AppRule> AppRules { get; set; } = new();
            public Location? Location { get; set; }
        }

        readonly List<string> _states = new();
        int _index = -1;
        const int Max = 150;

        static string Take(Settings s) => JsonSerializer.Serialize(new Snapshot
        {
            Presets = s.Presets, Profiles = s.Profiles, Week = (Guid[])s.Week.Clone(), AppRules = s.AppRules, Location = s.Location,
        }, Json.Opts);

        public bool CanUndo => _index > 0;
        public bool CanRedo => _index < _states.Count - 1;

        /// <summary>Record the current state if it differs from the last recorded one.</summary>
        public void Capture(Settings s)
        {
            var cur = Take(s);
            if (_index >= 0 && _states[_index] == cur) return;
            if (_index < _states.Count - 1) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
            _states.Add(cur);
            if (_states.Count > Max) _states.RemoveAt(0);
            _index = _states.Count - 1;
        }

        public bool Undo(Settings s) => Move(s, -1);
        public bool Redo(Settings s) => Move(s, +1);

        bool Move(Settings s, int delta)
        {
            int target = _index + delta;
            if (target < 0 || target >= _states.Count) return false;
            var snap = JsonSerializer.Deserialize<Snapshot>(_states[target], Json.Opts);
            if (snap == null) return false;
            _index = target;
            s.Presets = snap.Presets;
            s.Profiles = snap.Profiles;
            s.Week = snap.Week;
            s.AppRules = snap.AppRules;
            s.Location = snap.Location;
            return true;
        }
    }
}
