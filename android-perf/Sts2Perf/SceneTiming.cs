using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// With the frame log on: how long the game's code takes to create scenes
// (PackedScene.Instantiate) and put them in the tree (Node.AddChild, which
// runs their _EnterTree and _Ready), per scene, on the main thread.
//
// Every enemy hit in the 0.4.2.10 logs was a 50-65 ms frame (block broken
// 68-91 ms) that added ~6 effect scenes (slash, hit spark, damage number,
// specks, glow) with no JIT, GC or load behind it. This says whether
// creating those is the time:
//
//   [Perf] hitch 67 ms: instancing 6 31 ms (vfx_attack_slash 12 ms, ...); ...
//   [Perf] frames 10s: ... | instancing 2.4 ms/frame
//
// Only the outermost call is timed, so a scene that creates others in its
// _Ready counts once, with everything it created.
internal static class SceneTiming
{
    private const string HarmonyId = "com.sts2perf.scenes";
    private const double NameOverMs = 0.3;

    private static Harmony _harmony;
    private static bool _patched;
    private static int _mainThread;
    private static int _depth;
    private static readonly Dictionary<string, double> Frame = new();
    private static int _frameCount;
    private static double _frameMs;
    private static double _windowMs;

    // From PerfRuntime.ApplyAll: patched while the frame log is on.
    public static void Apply()
    {
        var on = PerfSettings.FrameLogSeconds > 0;
        if (on && !_patched)
        {
            _mainThread = System.Environment.CurrentManagedThreadId;
            _harmony ??= new Harmony(HarmonyId);
            var prefix = new HarmonyMethod(typeof(SceneTiming), nameof(Prefix));
            var instantiate = typeof(PackedScene).GetMethods().First(m => m.Name == nameof(PackedScene.Instantiate) && !m.IsGenericMethodDefinition);
            var addChild = AccessTools.Method(typeof(Node), nameof(Node.AddChild), new[] { typeof(Node), typeof(bool), typeof(Node.InternalMode) });
            _harmony.Patch(instantiate, prefix: prefix, postfix: new HarmonyMethod(typeof(SceneTiming), nameof(AfterInstantiate)));
            _harmony.Patch(addChild, prefix: prefix, postfix: new HarmonyMethod(typeof(SceneTiming), nameof(AfterAddChild)));
            _patched = true;
            PerfEntry.Log("scene creation: timed");
        }
        else if (!on && _patched)
        {
            _harmony.UnpatchAll(HarmonyId);
            _patched = false;
            ResetFrame();
        }
    }

    // Outermost calls on the main thread only; 0 means not timed.
    private static void Prefix(out long __state)
    {
        __state = 0;
        if (System.Environment.CurrentManagedThreadId != _mainThread)
            return;
        if (_depth++ == 0)
            __state = Stopwatch.GetTimestamp();
    }

    private static void AfterInstantiate(PackedScene __instance, long __state)
    {
        if (End(__state) is { } ms)
            Record(ms, ms >= NameOverMs ? __instance.ResourcePath : null);
    }

    private static void AfterAddChild(Node node, long __state)
    {
        if (End(__state) is { } ms)
            Record(ms, ms < NameOverMs || node == null ? null : node.SceneFilePath is { Length: > 0 } scene ? scene : node.GetType().Name);
    }

    private static double? End(long state)
    {
        if (System.Environment.CurrentManagedThreadId != _mainThread)
            return null;
        if (_depth > 0)
            _depth--;
        return state == 0 ? null : (Stopwatch.GetTimestamp() - state) * 1000.0 / Stopwatch.Frequency;
    }

    // name: the scene's path, or null for a call too quick to name.
    private static void Record(double ms, string path)
    {
        try
        {
            _frameCount++;
            _frameMs += ms;
            _windowMs += ms;
            if (ms < NameOverMs)
                return;
            var name = path ?? "?";
            name = name[(name.LastIndexOf('/') + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot > 0)
                name = name[..dot];
            Frame[name] = Frame.TryGetValue(name, out var sum) ? sum + ms : ms;
        }
        catch
        {
            // Never let timing break the game's scene setup.
        }
    }

    // For FrameLog's hitch line: this frame's scene creation, or null.
    public static string FrameSummary()
    {
        if (_frameMs < 1)
            return null;
        var top = Frame.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value:0} ms");
        return $"instancing {_frameCount} {_frameMs:0} ms ({string.Join(", ", top)})";
    }

    // Each frame. A call that threw never reached its postfix: start over.
    public static void ResetFrame()
    {
        Frame.Clear();
        _frameCount = 0;
        _frameMs = 0;
        _depth = 0;
    }

    // For FrameLog's stats line; starts a new window.
    public static string TakeWindowSummary(int frames)
    {
        if (!_patched || frames == 0)
            return "";
        var text = $" | instancing {_windowMs / frames:0.0} ms/frame";
        _windowMs = 0;
        return text;
    }
}
