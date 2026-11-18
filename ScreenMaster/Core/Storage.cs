using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenMaster.Core
{
    public static class Paths
    {
        /// <summary>%APPDATA%\ScreenMaster; SCREENMASTER_DATA points it elsewhere (UI tests on a throwaway copy).</summary>
        public static readonly string Dir = Environment.GetEnvironmentVariable("SCREENMASTER_DATA") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenMaster");
        public static string Settings => Path.Combine(Dir, "settings.json");
        public static string State => Path.Combine(Dir, "state.json");
        public static string Log => Path.Combine(Dir, "log.txt");
        public static void Ensure() => Directory.CreateDirectory(Dir);
    }

    public static class Log
    {
        static readonly object _lock = new();
        public static void Write(string msg)
        {
            try
            {
                lock (_lock)
                {
                    Paths.Ensure();
                    var fi = new FileInfo(Paths.Log);
                    if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                    File.AppendAllText(Paths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static T? Load<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts);
            }
            catch (Exception ex)
            {
                Log.Write($"load {path} failed: {ex.Message}");
                try { File.Copy(path, path + ".bad", true); } catch { }
                return null;
            }
        }

        public static void Save<T>(string path, T value)
        {
            try
            {
                Paths.Ensure();
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
                File.Move(tmp, path, true);
            }
            catch (Exception ex) { Log.Write($"save {path} failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Crash-safety state. Gamma ramps and NVIDIA vibrance survive process death,
    /// so we persist the originals and an "applied" flag; a launch after an unclean
    /// exit restores them before doing anything else.
    /// </summary>
    public sealed class DisplayState
    {
        public bool Applied { get; set; }
        public Dictionary<string, ushort[]> OriginalRamps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> OriginalDvc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Monitor device → VCP code (hex) → user's value, only for codes a layer currently controls.</summary>
        public Dictionary<string, Dictionary<string, int>> OriginalVcp { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>User's Windows theme [apps, system] while a "Tema do Windows" layer controls it; null otherwise.</summary>
        public int[]? OriginalTheme { get; set; }

        public static DisplayState Load()
        {
            var s = Json.Load<DisplayState>(Paths.State) ?? new DisplayState();
            s.OriginalRamps = new Dictionary<string, ushort[]>(s.OriginalRamps, StringComparer.OrdinalIgnoreCase);
            s.OriginalDvc = new Dictionary<string, int>(s.OriginalDvc, StringComparer.OrdinalIgnoreCase);
            s.OriginalVcp = new Dictionary<string, Dictionary<string, int>>(s.OriginalVcp ?? new(), StringComparer.OrdinalIgnoreCase);
            return s;
        }

        readonly object _saveLock = new();
        public void Save() { lock (_saveLock) Json.Save(Paths.State, this); }
    }
}
