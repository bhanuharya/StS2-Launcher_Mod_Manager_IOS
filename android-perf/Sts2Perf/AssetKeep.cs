using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// asset_cache=keep (Perf settings → Keep loaded assets): the game stops
// throwing away the assets it had to load on the spot, so they load once per
// session instead of once per room.
//
// The game's AssetCache (MegaCrit.Sts2.Core.Assets) holds what each room
// preloads. Anything else a room asks for is loaded right then, logged as
// "Asset not cached: <path>", and remembered as "missed"; on the next room
// change PreloadManager calls AssetCache.UnloadMissedCacheAssets(), which
// removes every missed asset and Disposes it. So the card tooltip
// (card_hover_tip.tscn), the energy icon, power icons and some attack effects
// are read from the game's package again in every room: 0.4.2.10 logs from a
// Pad 5 had 482 such loads in 80 minutes, most followed by a 50-70 ms frame
// (a boss's map icon ~200 ms). With keep on, UnloadMissedCacheAssets does
// nothing, so a missed asset stays in the game's cache: a few hundred small
// assets at most, the ones the game needs again and again.
//
// (0.4.2.12-0.4.2.13 held references to repeatedly loaded assets instead,
// which couldn't work: the game Disposes the very objects held.)
//
// With the frame log on (this setting or not), every ResourceLoader.Load and
// LoadThreadedGet is timed; hitch lines list the frame's slow loads and the
// stats line counts them:
//
//   [Perf] hitch 72 ms: loads 2 41 ms (card_hover_tip.tscn 38 ms, ...); ...
//   [Perf] frames 10s: ... | loads 14 (6 slow, 212 ms)
internal static class AssetKeep
{
    private const string TimingId = "com.sts2perf.assets";
    private const string KeepId = "com.sts2perf.assets.keep";
    private const double SlowMs = 2;

    private static Harmony _timingHarmony;
    private static Harmony _keepHarmony;
    private static bool _timing;
    private static bool _keep;
    private static int _skipped;
    private static bool _keepUnavailable;
    private static int _mainThread;
    private static readonly object Lock = new();
    private static readonly List<(string Name, double Ms)> FrameLoads = new();
    private static int _windowLoads;
    private static int _windowSlow;
    private static double _windowSlowMs;

    // From PerfRuntime.ApplyAll (every 2 s and on settings changes).
    public static void Apply()
    {
        var timing = PerfSettings.FrameLogSeconds > 0;
        if (timing != _timing)
        {
            if (timing)
                PatchTiming();
            else
                _timingHarmony.UnpatchAll(TimingId);
            lock (Lock)
            {
                _timing = timing;
                FrameLoads.Clear();
            }
        }
        var keep = PerfSettings.Value("asset_cache") == "keep";
        if (keep != _keep && !_keepUnavailable)
        {
            if (keep && !PatchKeep())
                return;
            if (!keep)
                _keepHarmony.UnpatchAll(KeepId);
            _keep = keep;
            PerfEntry.Log(keep ? "keep loaded assets: on (the game no longer unloads assets it loaded on the spot)" : "keep loaded assets: off");
        }
    }

    private static void PatchTiming()
    {
        _mainThread = System.Environment.CurrentManagedThreadId;
        _timingHarmony ??= new Harmony(TimingId);
        var prefix = new HarmonyMethod(typeof(AssetKeep), nameof(Prefix));
        var postfix = new HarmonyMethod(typeof(AssetKeep), nameof(Postfix));
        var load = ((Func<string, string, ResourceLoader.CacheMode, Resource>)ResourceLoader.Load).Method;
        var threadedGet = ((Func<string, Resource>)ResourceLoader.LoadThreadedGet).Method;
        _timingHarmony.Patch(load, prefix: prefix, postfix: postfix);
        _timingHarmony.Patch(threadedGet, prefix: prefix, postfix: postfix);
        PerfEntry.Log("asset loads: timed");
    }

    private static bool PatchKeep()
    {
        var unload = AccessTools.Method("MegaCrit.Sts2.Core.Assets.AssetCache:UnloadMissedCacheAssets");
        if (unload == null)
        {
            PerfEntry.Log("keep loaded assets: AssetCache.UnloadMissedCacheAssets not found in this game version");
            _keepUnavailable = true;
            return false;
        }
        _keepHarmony ??= new Harmony(KeepId);
        _keepHarmony.Patch(unload, prefix: new HarmonyMethod(typeof(AssetKeep), nameof(SkipUnload)));
        return true;
    }

    private static bool SkipUnload()
    {
        if (++_skipped == 1)
            PerfEntry.Log("keep loaded assets: kept the game's on-the-spot loads instead of unloading them (room change)");
        return false;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(string path, long __state)
    {
        try
        {
            if (System.Environment.CurrentManagedThreadId != _mainThread)
                return;
            var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            lock (Lock)
            {
                if (!_timing)
                    return;
                _windowLoads++;
                if (ms < SlowMs)
                    return;
                _windowSlow++;
                _windowSlowMs += ms;
                FrameLoads.Add((string.IsNullOrEmpty(path) ? "?" : path[(path.LastIndexOf('/') + 1)..], ms));
            }
        }
        catch
        {
            // Never let this break a load.
        }
    }

    // For FrameLog's hitch line: this frame's slow loads, or null.
    public static string FrameSummary()
    {
        lock (Lock)
        {
            if (FrameLoads.Count == 0)
                return null;
            var top = FrameLoads.OrderByDescending(l => l.Ms).Take(4).Select(l => $"{l.Name} {l.Ms:0} ms");
            return $"loads {FrameLoads.Count} {FrameLoads.Sum(l => l.Ms):0} ms ({string.Join(", ", top)})";
        }
    }

    public static void ResetFrame()
    {
        lock (Lock)
            FrameLoads.Clear();
    }

    // For FrameLog's stats line; starts a new window.
    public static string TakeWindowSummary()
    {
        lock (Lock)
        {
            if (!_timing)
                return "";
            var text = $" | loads {_windowLoads} ({_windowSlow} slow, {_windowSlowMs:0} ms)";
            _windowLoads = _windowSlow = 0;
            _windowSlowMs = 0;
            return text;
        }
    }
}
