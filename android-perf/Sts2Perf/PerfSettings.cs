using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace STS2Perf;

// The perf build's settings: user://sts2perf.cfg (the app's private files
// dir), one key=value per line, '#' starts a comment line. The launcher's
// Perf settings panel writes it, and so can a script over adb; PerfRuntime
// re-reads it every couple of seconds, so changes apply while the game runs.
//
//   resolution=full|reduced     reduced: render at the layout size
//   effects=on|off              off: HDR 2D, FXAA, debanding off
//   max_fps=0|<n>               0 = the game's FPS limit setting
//   vsync=game|on|off|adaptive|mailbox
//   particles=<0-100>           % of particles emitted (100 = as designed)
//   frame_log=<seconds>         frame-time stats line every N s, plus a
//                               line per hitch with its causes; 0 = off
//   jit_warmup=off|on           on: compile the game's code at the main menu
//                               (JitWarmup; experimental)
//   probe=off|next_fight|spikes next_fight: SceneProbe runs once in the next
//                               fight, then sets this back to off; spikes:
//                               tests during the next five slowdowns
//   idle_overlay=draw|skip      skip: don't draw the idle transition layer
//   card_fx=game|fast           fast: cards draw without their offscreen
//                               CanvasGroup pass (CardFx)
//   card_trails=on|off          off: no trails behind flying cards
//   root.<property>=<value>     any property of the game's root Window
//   engine.<property>=<value>   any property of Engine
//
// graphics=fast|sharp from 0.4.2.1-0.4.2.3 still works when resolution and
// effects are absent. Defaults leave everything as upstream ships it.
//
// Lazy: OS.GetUserDataDir() is a Godot call and must not run from
// PerfEntry.Apply.
internal static class PerfSettings
{
    private const string FileName = "sts2perf.cfg";
    private static Dictionary<string, string> _values = new();
    private static DateTime _mtime;
    private static bool _loaded;

    public static string FilePath => Path.Combine(OS.GetUserDataDir(), FileName);

    public static bool ReducedResolution => Value("resolution") == "reduced";
    public static bool EffectsOff => Value("effects") == "off";
    public static int MaxFps => Int("max_fps", 0, 0, 1000);
    public static string Vsync => Value("vsync");
    public static int ParticlesPercent => Int("particles", 100, 0, 100);
    public static int FrameLogSeconds => Int("frame_log", 0, 0, 600);
    public static bool JitWarmup => Value("jit_warmup") == "on";

    public static IEnumerable<(string Target, string Property, string Value)> PropertyOverrides
    {
        get
        {
            EnsureLoaded();
            foreach (var (key, value) in _values)
            {
                var dot = key.IndexOf('.');
                if (dot > 0 && key[..dot] is "root" or "engine")
                    yield return (key[..dot], key[(dot + 1)..], value);
            }
        }
    }

    // A named setting's effective value, lower-cased, defaults applied.
    public static string Value(string key)
    {
        EnsureLoaded();
        var fast = Raw("graphics") == "fast";
        return key switch
        {
            "resolution" => Raw("resolution") ?? (fast ? "reduced" : "full"),
            "effects" => Raw("effects") ?? (fast ? "off" : "on"),
            "max_fps" => Raw("max_fps") ?? "0",
            "vsync" => Raw("vsync") ?? "game",
            "particles" => Raw("particles") ?? "100",
            "frame_log" => Raw("frame_log") ?? "0",
            "jit_warmup" => Raw("jit_warmup") ?? "off",
            "probe" => Raw("probe") ?? "off",
            "card_fx" => Raw("card_fx") ?? "game",
            "card_trails" => Raw("card_trails") ?? "on",
            "idle_overlay" => Raw("idle_overlay") ?? "draw",
            _ => Raw(key),
        };
    }

    public static void Set(string key, string value)
    {
        EnsureLoaded();
        _values[key] = value;
        // Written explicitly now, so the legacy key no longer decides anything.
        if (key is "resolution" or "effects")
        {
            _values.TryAdd("resolution", Value("resolution"));
            _values.TryAdd("effects", Value("effects"));
            _values.Remove("graphics");
        }
        try
        {
            File.WriteAllLines(FilePath, _values.Select(kv => $"{kv.Key}={kv.Value}"));
            _mtime = File.GetLastWriteTimeUtc(FilePath);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"settings write failed: {ex.Message}");
        }
    }

    // Re-reads the file if it changed on disk. True when any value changed.
    public static bool Reload()
    {
        try
        {
            var mtime = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            if (_loaded && mtime == _mtime)
                return false;
            _loaded = true;
            _mtime = mtime;

            var fresh = new Dictionary<string, string>();
            if (mtime != DateTime.MinValue)
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                        continue;
                    var parts = trimmed.Split('=', 2);
                    if (parts.Length == 2)
                        fresh[parts[0].Trim().ToLowerInvariant()] = parts[1].Trim();
                }
            }
            var changed =
                fresh.Count != _values.Count
                || fresh.Any(kv => !_values.TryGetValue(kv.Key, out var v) || v != kv.Value);
            _values = fresh;
            return changed;
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"settings read failed: {ex.Message}");
            return false;
        }
    }

    public static string Summary()
    {
        var parts = new List<string>
        {
            $"resolution {Value("resolution")}",
            $"effects {Value("effects")}",
            $"fps cap {(MaxFps > 0 ? MaxFps.ToString() : "game")}",
            $"vsync {Vsync}",
            $"particles {ParticlesPercent}%",
            $"cards {Value("card_fx")}",
            $"card trails {Value("card_trails")}",
            $"idle overlay {Value("idle_overlay")}",
            $"frame log {(FrameLogSeconds > 0 ? FrameLogSeconds + "s" : "off")}",
            $"jit warm-up {Value("jit_warmup")}",
            $"probe {Value("probe")}",
        };
        parts.AddRange(PropertyOverrides.Select(o => $"{o.Target}.{o.Property}={o.Value}"));
        return string.Join(", ", parts);
    }

    private static void EnsureLoaded()
    {
        if (!_loaded)
            Reload();
    }

    private static string Raw(string key) =>
        _values.TryGetValue(key, out var v) ? v.ToLowerInvariant() : null;

    private static int Int(string key, int fallback, int min, int max) =>
        int.TryParse(Value(key), out var n) ? Math.Clamp(n, min, max) : fallback;
}
