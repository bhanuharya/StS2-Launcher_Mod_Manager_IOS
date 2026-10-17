using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STS2Perf;

// gc=game|background (Perf settings → Garbage collector), and Mono's own log
// of its collections while the frame log is on. Both take effect the next
// time the app starts.
//
// The game's .NET code runs on Mono, whose garbage collector stops the game
// while it collects. A 0.4.2.17 log from a Pad 5, 24 minutes: 516 minor
// collections (one every ~3 s in fights, the frames they land in mostly
// 45-65 ms) and 49 major ones, those frames ~290 ms on average. Godot starts
// Mono with Mono's defaults, and on Android those collect the old generation
// (major) all at once. background sets MONO_GC_PARAMS=major=marksweep-conc:
// Mono's concurrent collector, which marks on another core while the game
// keeps running and stops it only briefly to start and finish, as .NET's
// own Android apps do by default (AndroidEnableSGenConcurrent). Minor
// collections stay as they are: a bigger nursery was tried in a desktop
// test of Mono with a heap like Godot's (finalizable wrappers, references
// from old objects to new ones), and it made them rarer but each one about
// as much longer, as most of what they copy grows with the nursery.
//
// Mono reads these from the environment once, as it starts, before any of
// the add-on runs. So the add-on writes them to <files dir>/sts2perf_env and
// the app's startup provider (java/com/sts2perf/StartupEnv.java) sets them
// on the next start; it also sets STS2PERF_FILES_DIR, which is how this
// knows the provider is there. If a start with them never reaches the
// add-on, the next start goes without them once (StartupEnv). With the frame log on, the file also turns
// on Mono's log of each collection (MONO_LOG_LEVEL=debug, MONO_LOG_MASK=gc),
// which lands in the log next to the add-on's lines with the real pause and
// what survived it:
//
//   Mono: GC_MINOR: (Nursery full) time 41.20ms, stw 42.01ms promoted 1203K major size: 210432K ...
//   Mono: GC_MAJOR_CONCURRENT_START: (Minor allowance)
internal static class GcTuning
{
    private const string FileName = "sts2perf_env";
    private const string PendingName = "sts2perf_env.pending";
    private static readonly string[] Names = { "MONO_GC_PARAMS", "MONO_LOG_LEVEL", "MONO_LOG_MASK" };
    private static string _written;
    private static bool _reported;

    private static string Dir => Environment.GetEnvironmentVariable("STS2PERF_FILES_DIR");

    // True when the app's startup provider ran this time, so the setting can work.
    public static bool Available => !string.IsNullOrEmpty(Dir);

    // What the settings ask Mono to start with.
    private static Dictionary<string, string> Wanted()
    {
        var env = new Dictionary<string, string>();
        if (PerfSettings.Value("gc") == "background")
            env["MONO_GC_PARAMS"] = "major=marksweep-conc";
        if (PerfSettings.FrameLogSeconds > 0)
        {
            env["MONO_LOG_LEVEL"] = "debug";
            env["MONO_LOG_MASK"] = "gc";
        }
        return env;
    }

    private static string Describe(IReadOnlyDictionary<string, string> env) =>
        env.Count == 0 ? "Mono's defaults" : string.Join(" ", env.Select(kv => $"{kv.Key}={kv.Value}"));

    // From PerfRuntime (every 2 s) and PerfPanel (each tap): keeps the file
    // in step with the settings, and says once what this run started with.
    public static void Sync()
    {
        var wanted = Wanted();
        if (!_reported)
        {
            _reported = true;
            // This start got as far as the add-on (StartupEnv's safety net).
            try
            {
                if (Available)
                    File.Delete(Path.Combine(Dir, PendingName));
            }
            catch
            {
                // Next start skips the options once; nothing worse.
            }
            var now = Names.Select(n => (n, Environment.GetEnvironmentVariable(n)))
                .Where(e => !string.IsNullOrEmpty(e.Item2))
                .ToDictionary(e => e.n, e => e.Item2);
            PerfEntry.Log(
                Available
                    ? $"garbage collector: {PerfSettings.Value("gc")} (this run started with {Describe(now)})"
                    : "garbage collector: the app's startup provider didn't run, so Mono runs with its defaults"
            );
        }
        if (!Available)
            return;
        var text = string.Concat(wanted.Select(kv => $"{kv.Key}={kv.Value}\n"));
        if (text == _written)
            return;
        var path = Path.Combine(Dir, FileName);
        try
        {
            var old = File.Exists(path) ? File.ReadAllText(path) : "";
            if (old != text)
            {
                if (text.Length == 0)
                    File.Delete(path);
                else
                    File.WriteAllText(path, text);
                PerfEntry.Log($"garbage collector: next start with {Describe(wanted)}");
            }
            _written = text;
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"garbage collector: couldn't write {path}: {ex.Message}");
        }
    }
}
