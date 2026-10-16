using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// asset_cache=keep (Perf settings → Keep loaded assets): keeps the assets the
// game loads over and over in memory, so they stop costing a load each time.
//
// The game has its own cache of assets and loads anything missing from it
// out of the game's package, logging "Asset not cached: <path>". 0.4.2.10
// logs from a Pad 5 had hundreds of those a session, for the same few assets:
// the card tooltip (card_hover_tip.tscn, 67 times in one session), the
// Silent's energy icon, power icons (strength, dexterity, accuracy, ...),
// attack effects; most were followed by a 50-70 ms frame, and a boss's map
// icon (its Spine data) by ~200 ms. Godot itself keeps a loaded resource only
// while something uses it, so once the tooltip closes or the power wears off,
// the next one is read from the package again.
//
// The game's synchronous loads all go through ResourceLoader.Load (GD.Load
// and Load<T> call it), its background ones end in LoadThreadedGet; both get
// a Harmony prefix and postfix. With keep on, an asset that took a real load
// (over SlowMs; Godot's cache answers in microseconds) for the second time is
// held, so from the third time on Godot's cache hands it back at once. At
// most MaxKept assets, the least recently loaded dropped first. Full screens
// (res://scenes/screens/) and textures over 2 megapixels are never held: they
// load behind transitions and are big.
//
// With the frame log on (this setting or not), hitch lines list the frame's
// slow loads and the stats line counts them:
//
//   [Perf] hitch 72 ms: loads 2 41 ms (card_hover_tip.tscn 38 ms, ...); ...
//   [Perf] frames 10s: ... | loads 14 (6 slow, 212 ms), kept 37
internal static class AssetKeep
{
    private const string HarmonyId = "com.sts2perf.assets";
    private const int MaxKept = 200;
    private const double SlowMs = 2;
    private const int MaxTexturePixels = 2 * 1024 * 1024;
    private const string NeverKeep = "res://scenes/screens/";

    private static Harmony _harmony;
    private static bool _patched;
    private static bool _keep;
    private static bool _timing;
    private static int _mainThread;
    private static readonly object Lock = new();
    private static readonly HashSet<string> LoadedBefore = new();
    private static readonly LinkedList<(string Path, Resource Resource)> Kept = new();
    private static readonly Dictionary<string, LinkedListNode<(string Path, Resource Resource)>> KeptByPath = new();
    private static readonly List<(string Name, double Ms)> FrameLoads = new();
    private static int _windowLoads;
    private static int _windowSlow;
    private static double _windowSlowMs;

    // From PerfRuntime.ApplyAll (every 2 s and on settings changes).
    public static void Apply()
    {
        var keep = PerfSettings.Value("asset_cache") == "keep";
        var timing = PerfSettings.FrameLogSeconds > 0;
        if ((keep || timing) && !_patched)
            Patch();
        else if (!keep && !timing && _patched)
            Unpatch();
        lock (Lock)
        {
            _timing = timing;
            if (!timing)
                FrameLoads.Clear();
            if (keep == _keep)
                return;
            _keep = keep;
            if (!keep)
            {
                Kept.Clear();
                KeptByPath.Clear();
            }
        }
        PerfEntry.Log(keep ? $"keep loaded assets: on (up to {MaxKept} assets the game loads again and again)" : "keep loaded assets: off");
    }

    private static void Patch()
    {
        _mainThread = System.Environment.CurrentManagedThreadId;
        _harmony ??= new Harmony(HarmonyId);
        var prefix = new HarmonyMethod(typeof(AssetKeep), nameof(Prefix));
        var postfix = new HarmonyMethod(typeof(AssetKeep), nameof(Postfix));
        var load = ((Func<string, string, ResourceLoader.CacheMode, Resource>)ResourceLoader.Load).Method;
        var threadedGet = ((Func<string, Resource>)ResourceLoader.LoadThreadedGet).Method;
        _harmony.Patch(load, prefix: prefix, postfix: postfix);
        _harmony.Patch(threadedGet, prefix: prefix, postfix: postfix);
        _patched = true;
        PerfEntry.Log("asset loads: timed");
    }

    private static void Unpatch()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _patched = false;
    }

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(string path, Resource __result, long __state)
    {
        try
        {
            var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            lock (Lock)
            {
                if (_timing && System.Environment.CurrentManagedThreadId == _mainThread)
                {
                    _windowLoads++;
                    if (ms >= SlowMs)
                    {
                        _windowSlow++;
                        _windowSlowMs += ms;
                        FrameLoads.Add((FileName(path), ms));
                    }
                }
                if (_keep && __result != null && !string.IsNullOrEmpty(path))
                    Keep(path, __result, ms);
            }
        }
        catch
        {
            // Never let this break a load.
        }
    }

    private static void Keep(string path, Resource resource, double ms)
    {
        if (KeptByPath.TryGetValue(path, out var node))
        {
            Kept.Remove(node);
            Kept.AddFirst(node);
            return;
        }
        // A fast load: something else holds it, Godot's cache answered.
        if (ms < SlowMs)
            return;
        // The first real load: it may never be needed again.
        if (LoadedBefore.Add(path))
            return;
        if (path.StartsWith(NeverKeep, StringComparison.Ordinal)
            || resource is Texture2D texture && texture.GetWidth() * texture.GetHeight() > MaxTexturePixels)
            return;
        KeptByPath[path] = Kept.AddFirst((path, resource));
        if (Kept.Count <= MaxKept)
            return;
        // Dropped, not disposed: the game may hold the same object.
        KeptByPath.Remove(Kept.Last!.Value.Path);
        Kept.RemoveLast();
    }

    private static string FileName(string path) =>
        string.IsNullOrEmpty(path) ? "?" : path[(path.LastIndexOf('/') + 1)..];

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
            if (!_patched)
                return "";
            var text = $" | loads {_windowLoads} ({_windowSlow} slow, {_windowSlowMs:0} ms){(_keep ? $", kept {Kept.Count}" : "")}";
            _windowLoads = _windowSlow = 0;
            _windowSlowMs = 0;
            return text;
        }
    }
}
