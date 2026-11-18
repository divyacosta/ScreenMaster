using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ScreenMaster.Core;
using ScreenMaster.Native;

namespace ScreenMaster.UI
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public sealed class PresetVM : Observable
    {
        public Preset Model { get; }
        public PresetVM(Preset p) { Model = p; }
        static App Ctl => App.Ctl;

        public string Name => Model.Name;
        public override string ToString() => Name;

        public string Summary
        {
            get
            {
                var on = Model.Layers.Count(l => l.Enabled);
                var s = Model.Layers.Count == 0 ? "sem ajustes" : $"{on}/{Model.Layers.Count} ajustes";
                if (Ctl.AppPreset == Model) s += $" · ativo por {Ctl.AppName}";
                return s;
            }
        }

        /// <summary>"agora" (amber, like the agenda profiles) = this preset is the local one on screen. The rule case is in the summary.</summary>
        public Visibility OnScreenVisibility => Ctl.Stack.Local == Model ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AppVisibility => Ctl.Settings.AppRules.Any(r => r.Preset == Model.Name) ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed class ChipVM
    {
        public string Text { get; init; } = "";
        public Brush Background { get; init; } = Brushes.Transparent;
        public Brush Border { get; init; } = Brushes.Transparent;
        public Brush Foreground { get; init; } = Brushes.White;
        public string Tip { get; init; } = "";
        public FontFamily Font { get; init; } = new("Segoe UI");
    }

    public sealed class LayerVM : Observable
    {
        public Layer Model { get; }
        readonly Action _changed;
        string? _error;

        public LayerVM(Layer l, Action changed) { Model = l; _changed = changed; }

        public bool Enabled
        {
            get => Model.Enabled;
            set { if (Model.Enabled == value) return; Model.Enabled = value; Raise(); _changed(); }
        }

        public string Name => Model.DisplayName;
        public override string ToString() => Name;

        Category Cat => Categories.Of(Model.Kind);
        public Brush CategoryBrush => Ui.Hex(Cat.Color);

        public string Detail
        {
            get
            {
                var parts = new List<string> { Cat.Name };
                if (Model.Opacity < 100) parts.Add($"{Model.Opacity:0}%");
                if (Model.Kind == LayerKind.MonitorHardware)
                {
                    var on = new[] { ("brightness", "brilho"), ("contrast", "contraste"), ("temp", "temperatura"), ("gainR", "RGB") }
                        .Where(k => Model.IsOn(k.Item1) || (k.Item1 == "gainR" && (Model.IsOn("gainG") || Model.IsOn("gainB")))).Select(k => k.Item2).ToList();
                    parts.Add(on.Count == 0 ? "nada marcado" : string.Join(", ", on));
                }
                return string.Join(" · ", parts);
            }
        }

        /// <summary>One chip per monitor: lit when the layer acts on it; a lock when the engine is global.</summary>
        public List<ChipVM> Chips
        {
            get
            {
                // Monochrome: lit = text-colored outline, off = faint. Color stays on the category tick only.
                var mons = App.Ctl.Engine.Monitors;
                bool global = !LayerDefs.Get(Model.Kind).PerMonitor;
                var list = new List<ChipVM>();
                foreach (var m in mons)
                {
                    bool on = Model.AppliesTo(m.DeviceName);
                    list.Add(new ChipVM
                    {
                        Text = m.Number,
                        Background = Brushes.Transparent,
                        Border = on ? Ui.Res("Dim") : Ui.Res("Line"),
                        Foreground = on ? Ui.Res("Text") : Ui.Res("Faint"),
                        Tip = $"{m.Short}: {(on ? "este ajuste age aqui" : "não age aqui")}{(global ? " (o Windows aplica Cor em todos)" : "")}",
                    });
                }
                if (global)
                    list.Add(new ChipVM { Text = "", Font = Ui.Icons, Foreground = Ui.Res("Dim"), Tip = "Ajuste de Cor: o Windows aplica em todos os monitores" });
                return list;
            }
        }

        public string? Error
        {
            get => _error;
            set { if (_error == value) return; _error = value; Raise(); Raise(nameof(ErrorVisibility)); }
        }

        public Visibility ErrorVisibility => string.IsNullOrEmpty(_error) ? Visibility.Collapsed : Visibility.Visible;

        public void Refresh() { Raise(nameof(Name)); Raise(nameof(Detail)); Raise(nameof(Enabled)); Raise(nameof(Chips)); }
    }
}
