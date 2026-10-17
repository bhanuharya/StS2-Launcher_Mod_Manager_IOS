using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STS2Perf;

// Mono's own log of each garbage collection, while the frame log is on,
// from the next time the app starts:
//
//   Mono: GC_MINOR: (Nursery full) time 6.86ms, stw 8.11ms promoted 603K major size: 67744K ...
//   Mono: GC_MAJOR_CONCURRENT_START: (LOS overflow)
//   Mono: GC_MAJOR: (user request) time 104.51ms, stw 110.07ms ...
//
// stw is how long the game was stopped. Mono reads its log settings
// (MONO_LOG_LEVEL=debug, MONO_LOG_MASK=gc) from the environment once, as it
// starts, before any of the add-on runs. So the add-on writes them to
// <files dir>/sts2perf_env and the app's startup provider
// (java/com/sts2perf/StartupEnv.java) sets them on the next start; it also
// sets STS2PERF_FILES_DIR, which is how this knows the provider is there.
// If a start with them never reaches the add-on, the next start goes
// without them once (StartupEnv).
//
// What the first such log (0.4.2.18, Pad 5) showed: minor collections stop
// the game for 6-10 ms, sometimes 17-50; the major heap is ~60-70 MB; and
// Mono here already collects it concurrently (GC_MAJOR_CONCURRENT_*). The
// long ones are the game's own GC.Collect() after each room's assets load
// (PreloadManager), 100-150 ms during the room transition. 0.4.2.18 had a
// setting for MONO_GC_PARAMS=major=marksweep-conc, which turned out to be
// what Mono does anyway; it's gone, and the file no longer sets it.
internal static class GcTuning
{
    private const string FileName = "sts2perf_env";
    private const string PendingName = "sts2perf_env.pending";
    private static readonly string[] Names = { "MONO_GC_PARAMS", "MONO_LOG_LEVEL", "MONO_LOG_MASK" };
    private static string _written;
    private static bool _reported;

    private static string Dir => Environment.GetEnvironmentVariable("STS2PERF_FILES_DIR");

    // True when the app's startup provider ran this time.
    public static bool Available => !string.IsNullOrEmpty(Dir);

    // What the settings ask Mono to start with.
    private static Dictionary<string, string> Wanted()
    {
        var env = new Dictionary<string, string>();
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
                    ? $"garbage collector: this run started with {Describe(now)}"
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
