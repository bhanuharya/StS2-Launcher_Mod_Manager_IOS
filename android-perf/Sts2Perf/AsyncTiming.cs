using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// With the frame log on: how long the game's async code runs each frame, and
// which async methods it is.
//
// The game's combat runs as async C# (play a card, an enemy's move, damage,
// powers), waiting on timers, tweens and frames in between. 0.4.2.13 logs
// had every enemy move followed by frames with 45-85 ms of "logic" that no
// script's _Process, scene creation, load, JIT or GC explained. Code resumed
// by a Godot signal (await ToSignal: a timer's timeout, a tween finishing,
// the next frame) runs right inside that logic, from SignalAwaiter; code
// resumed after an awaited Task runs from the GodotSynchronizationContext
// after drawing ("other").
//
// SignalAwaiter.OnCompleted gets a prefix that wraps the continuation it is
// handed in a timer; ExecutePendingContinuations gets a prefix and postfix.
// Outermost runs only, main thread only. Hitch lines get the frame's:
//
//   [Perf] hitch 74 ms: async 61 ms (NCreature.AnimDie 40 ms, Hook.AfterDamage 18 ms); ...
//
// and the stats line the average: "| async 3.1 ms/frame". It includes
// whatever that code calls (scene creation too).
internal static class AsyncTiming
{
    private const string HarmonyId = "com.sts2perf.async";
    private const double NameOverMs = 0.5;

    private static Harmony _harmony;
    private static bool _patched;
    private static int _mainThread;
    private static int _depth;
    private static double _frameMs;
    private static double _windowMs;
    private static readonly Dictionary<string, double> Frame = new();
    private static readonly Dictionary<Type, string> Names = new();

    // From PerfRuntime.ApplyAll: patched while the frame log is on.
    public static void Apply()
    {
        var on = PerfSettings.FrameLogSeconds > 0;
        if (on && !_patched)
        {
            _mainThread = System.Environment.CurrentManagedThreadId;
            _harmony ??= new Harmony(HarmonyId);
            _harmony.Patch(
                AccessTools.Method(typeof(SignalAwaiter), nameof(SignalAwaiter.OnCompleted)),
                prefix: new HarmonyMethod(typeof(AsyncTiming), nameof(WrapContinuation))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(GodotSynchronizationContext), nameof(GodotSynchronizationContext.ExecutePendingContinuations)),
                prefix: new HarmonyMethod(typeof(AsyncTiming), nameof(Begin)),
                postfix: new HarmonyMethod(typeof(AsyncTiming), nameof(EndPending))
            );
            _patched = true;
            PerfEntry.Log("async game code: timed");
        }
        else if (!on && _patched)
        {
            _harmony.UnpatchAll(HarmonyId);
            _patched = false;
            ResetFrame();
        }
    }

    private static void WrapContinuation(ref Action continuation)
    {
        var inner = continuation;
        if (inner == null)
            return;
        continuation = () =>
        {
            Begin(out var start);
            try
            {
                inner();
            }
            finally
            {
                End(start, inner);
            }
        };
    }

    private static void Begin(out long __state)
    {
        __state = 0;
        if (System.Environment.CurrentManagedThreadId == _mainThread && _depth++ == 0)
            __state = Stopwatch.GetTimestamp();
    }

    private static void EndPending(long __state) => End(__state, null);

    private static void End(long start, Action continuation)
    {
        if (System.Environment.CurrentManagedThreadId != _mainThread)
            return;
        if (_depth > 0)
            _depth--;
        if (start == 0)
            return;
        try
        {
            var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            _frameMs += ms;
            _windowMs += ms;
            if (ms < NameOverMs)
                return;
            var name = continuation == null ? "awaited tasks" : Describe(continuation.Target);
            Frame[name] = Frame.TryGetValue(name, out var sum) ? sum + ms : ms;
        }
        catch
        {
            // Never let timing break the game's code.
        }
    }

    // "NFoo.PlayAsync" from an async method's state machine box
    // (AsyncStateMachineBox<TResult, Foo+<PlayAsync>d__7>), else the type.
    private static string Describe(object target)
    {
        if (target == null)
            return "?";
        var type = target.GetType();
        if (Names.TryGetValue(type, out var known))
            return known;
        var machine = type.IsGenericType ? type.GetGenericArguments().LastOrDefault(t => t.Name.StartsWith("<", StringComparison.Ordinal)) : null;
        string name;
        if (machine != null)
        {
            var close = machine.Name.IndexOf('>');
            name = $"{machine.DeclaringType?.Name}.{(close > 1 ? machine.Name[1..close] : machine.Name)}";
        }
        else
        {
            name = type.DeclaringType != null ? $"{type.DeclaringType.Name}.{type.Name}" : type.Name;
        }
        Names[type] = name;
        return name;
    }

    // For FrameLog's hitch line: this frame's async code, or null.
    public static string FrameSummary()
    {
        if (_frameMs < 1)
            return null;
        var top = Frame.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value:0} ms");
        return $"async {_frameMs:0} ms ({string.Join(", ", top)})";
    }

    // Each frame; a run that threw still reached its finally.
    public static void ResetFrame()
    {
        Frame.Clear();
        _frameMs = 0;
        _depth = 0;
    }

    // For FrameLog's stats line; starts a new window.
    public static string TakeWindowSummary(int frames)
    {
        if (!_patched || frames == 0)
            return "";
        var text = $" | async {_windowMs / frames:0.0} ms/frame";
        _windowMs = 0;
        return text;
    }
}
