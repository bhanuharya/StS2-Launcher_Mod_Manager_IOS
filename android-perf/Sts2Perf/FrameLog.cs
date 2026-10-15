using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using Godot;

namespace STS2Perf;

// Frame-time stats for benchmarking, every frame_log=N seconds (PerfSettings),
// one [Perf] line in the launcher console and logcat:
//
//   [Perf] frames 10s: 59.8 fps | ms p50 16.7 p95 17.9 p99 24.1 max 41.3 | hitches 3
//          | cap 60 | vsync Enabled | render 2560x1600 of 2560x1600 | resolution full
//          | effects on (hdr2d off) | particles 100%
//          | jit +412 methods 151 ms | gc minor +14 major +0 | nodes 2140 (+380) | draw calls avg 180 max 260
//
// and, while it's on, a line for each frame over HitchMs with what happened
// during it, so a stutter can be pinned on a cause:
//
//   [Perf] hitch 183 ms: jit +412 methods 151 ms (Sts2.Core.Foo x40, ...); gc minor +1; nodes +214 (card x3, shiv_hit_vfx x6) | 210 draw calls
//
// A hitch with JIT work is logged a second or so late: the names of the
// compiled methods arrive after the frame (JitEvents).
//
// Times are main-loop iterations (SceneTree.process_frame), which on Android
// is also the render thread, so they include rendering. "hitches" counts
// frames over 1.5x the median.
//
// jit: the game's .NET code runs on Mono's JIT here, so every method compiles
// the first time it runs; a first-time card or effect can stall a frame on
// that alone (see JitWarmup). gc: Mono's collections, minor (nursery) and
// major; Mono keeps no pause time. nodes: nodes added, named by the scene
// files instanced, so a burst of effects shows what it was.
internal static class FrameLog
{
    private const float HitchMs = 50f;
    private const float SuspendedMs = 3000f; // longer gaps are the app in the background
    private const int HitchLinesPerWindow = 20;

    private static int _seconds;
    private static bool _hooked;
    private static ulong _lastUsec;
    private static ulong _windowStartUsec;
    private static readonly List<float> FrameMs = new(8192);
    private static Counters _frameStart;
    private static Counters _windowStart;
    private static int _hitchLines;
    private static int _hitchesUnlogged;
    private static long _drawCallsSum;
    private static int _drawCallsMax;
    private static int _nodesAddedFrame;
    private static int _nodesAddedWindow;
    private static readonly Dictionary<string, int> ScenesFrame = new();
    private static DateTime _frameStartUtc;
    private static readonly List<(ulong DueUsec, DateTime FromUtc, DateTime ToUtc, float Ms, Counters D, List<string> Causes, string Suffix)> Pending = new();
    private const ulong JitNamesDelayUsec = 1_500_000;

    // Called at startup and whenever the settings file changes.
    public static void Configure()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;
            var seconds = PerfSettings.FrameLogSeconds;
            if (seconds == _seconds)
                return;
            _seconds = seconds;
            _lastUsec = 0;
            if (seconds > 0 && !_hooked)
            {
                tree.ProcessFrame += OnFrame;
                tree.NodeAdded += OnNodeAdded;
                _hooked = true;
            }
            else if (seconds == 0 && _hooked)
            {
                tree.ProcessFrame -= OnFrame;
                tree.NodeAdded -= OnNodeAdded;
                _hooked = false;
            }
            PerfEntry.Log(
                seconds > 0
                    ? $"frame log every {seconds}s, hitches over {HitchMs:0} ms with their causes (jit methods available: {JitInfo.GetCompiledMethodCount() > 0})"
                    : "frame log off"
            );
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"frame log failed: {ex.Message}");
        }
    }

    private static void OnNodeAdded(Node node)
    {
        _nodesAddedFrame++;
        try
        {
            // Only instanced scenes have a file; their children don't, which
            // keeps the names to one per effect, card, or creature.
            var path = node.SceneFilePath;
            if (string.IsNullOrEmpty(path))
                return;
            var name = path[(path.LastIndexOf('/') + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot > 0)
                name = name[..dot];
            ScenesFrame[name] = ScenesFrame.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        catch
        {
            // Never let the log break the game's node setup.
        }
    }

    private static void OnFrame()
    {
        try
        {
            Sample();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"frame log failed: {ex.Message}");
            _lastUsec = 0;
        }
        _nodesAddedFrame = 0;
        ScenesFrame.Clear();
    }

    private static void Sample()
    {
        var now = Time.GetTicksUsec();
        var nowUtc = DateTime.UtcNow;
        var counters = Counters.Take();
        FlushPending(now);
        if (_lastUsec == 0)
        {
            _lastUsec = _windowStartUsec = now;
            _frameStartUtc = nowUtc;
            _frameStart = _windowStart = counters;
            ResetWindow();
            return;
        }
        var ms = (now - _lastUsec) / 1000f;
        _lastUsec = now;

        if (ms >= SuspendedMs)
        {
            PerfEntry.Log($"frame log: {ms / 1000:0.0} s gap (app in the background?), starting a new window");
            _windowStartUsec = now;
            _frameStartUtc = nowUtc;
            _frameStart = _windowStart = counters;
            ResetWindow();
            return;
        }

        FrameMs.Add(ms);
        _nodesAddedWindow += _nodesAddedFrame;
        var drawCalls = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        _drawCallsSum += drawCalls;
        _drawCallsMax = Math.Max(_drawCallsMax, drawCalls);
        if (ms >= HitchMs)
        {
            if (_hitchLines < HitchLinesPerWindow)
            {
                _hitchLines++;
                LogHitch(now, _frameStartUtc, nowUtc, ms, counters.Since(_frameStart), drawCalls);
            }
            else
            {
                _hitchesUnlogged++;
            }
        }
        _frameStart = counters;
        _frameStartUtc = nowUtc;

        if (now - _windowStartUsec < (ulong)_seconds * 1_000_000UL)
            return;
        var elapsed = (now - _windowStartUsec) / 1_000_000f;
        Report(elapsed, counters.Since(_windowStart), counters);
        _windowStartUsec = now;
        _windowStart = counters;
        ResetWindow();
    }

    private static void ResetWindow()
    {
        FrameMs.Clear();
        _hitchLines = _hitchesUnlogged = 0;
        _drawCallsSum = _drawCallsMax = 0;
        _nodesAddedWindow = 0;
    }

    private static void LogHitch(ulong nowUsec, DateTime fromUtc, DateTime toUtc, float ms, Counters d, int drawCalls)
    {
        var causes = HitchCauses(d);
        var suffix = drawCalls > 0 ? $" | {drawCalls} draw calls" : "";
        if (d.JitMethods > 0 && JitEvents.Running)
            Pending.Add((nowUsec + JitNamesDelayUsec, fromUtc, toUtc, ms, d, causes, suffix));
        else
            WriteHitch(ms, d, causes, suffix, null);
    }

    private static void FlushPending(ulong nowUsec)
    {
        // A little slack: event times and frame times come from separate clocks.
        var slack = TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < Pending.Count; i++)
        {
            var p = Pending[i];
            if (nowUsec < p.DueUsec)
                continue;
            WriteHitch(p.Ms, p.D, p.Causes, p.Suffix, JitEvents.TopTypes(p.FromUtc - slack, p.ToUtc + slack, 4));
            Pending.RemoveAt(i--);
        }
    }

    private static void WriteHitch(float ms, Counters d, List<string> causes, string suffix, string jitTypes)
    {
        if (d.JitMethods > 0)
            causes.Insert(
                0,
                $"jit +{d.JitMethods} methods{(d.JitMs >= 1 ? $" {d.JitMs:0} ms" : "")}"
                    + (string.IsNullOrEmpty(jitTypes) ? "" : $" ({jitTypes})")
            );
        var text = causes.Count > 0 ? string.Join("; ", causes) : "no jit, gc, shader, texture or node change (game code or GPU)";
        PerfEntry.Log($"hitch {ms:0} ms: {text}{suffix}");
    }

    // Everything but the JIT part, which may wait for method names.
    private static List<string> HitchCauses(Counters d)
    {
        var causes = new List<string>();
        if (d.GcMinor > 0 || d.GcMajor > 0)
            causes.Add($"gc minor +{d.GcMinor} major +{d.GcMajor}");
        if (d.Pipelines > 0)
            causes.Add($"shaders compiled +{d.Pipelines:0}");
        var textureMb = d.TextureBytes / 1048576;
        if (Math.Abs(textureMb) >= 0.1)
            causes.Add($"texture memory {textureMb:+0.0;-0.0} MB");
        if (_nodesAddedFrame > 0)
        {
            var scenes = string.Join(
                ", ",
                ScenesFrame.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} x{kv.Value}")
            );
            causes.Add($"nodes +{_nodesAddedFrame}{(scenes.Length > 0 ? $" ({scenes})" : "")}");
        }
        return causes;
    }

    private static void Report(float elapsed, Counters d, Counters now)
    {
        if (FrameMs.Count == 0)
            return;
        FrameMs.Sort();
        float P(double q) => FrameMs[Math.Min(FrameMs.Count - 1, (int)(q * FrameMs.Count))];
        var median = P(0.5);
        var hitches = 0;
        for (var i = FrameMs.Count - 1; i >= 0 && FrameMs[i] > median * 1.5f; i--)
            hitches++;

        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var screen = DisplayServer.WindowGetSize();
        var render = root.ContentScaleMode == Window.ContentScaleModeEnum.Viewport
            ? root.GetVisibleRect().Size
            : (Vector2)screen;

        PerfEntry.Log(
            $"frames {elapsed:0}s: {FrameMs.Count / elapsed:0.0} fps"
                + $" | ms p50 {median:0.0} p95 {P(0.95):0.0} p99 {P(0.99):0.0} max {FrameMs[^1]:0.0}"
                + $" | hitches {hitches}{(_hitchesUnlogged > 0 ? $" ({_hitchesUnlogged} over {HitchMs:0} ms not itemized)" : "")}"
                + $" | cap {Engine.MaxFps} | vsync {DisplayServer.WindowGetVsyncMode()}"
                + $" | render {render.X:0}x{render.Y:0} of {screen.X}x{screen.Y}"
                + $" | resolution {PerfSettings.Value("resolution")}"
                + $" | effects {PerfSettings.Value("effects")} (hdr2d {(root.UseHdr2D ? "on" : "off")})"
                + $" | particles {PerfSettings.ParticlesPercent}%"
                + $" | jit +{d.JitMethods} methods{(d.JitMs >= 1 ? $" {d.JitMs:0} ms" : "")}"
                + $" | gc minor +{d.GcMinor} major +{d.GcMajor}"
                + $" | nodes {now.Nodes:0} (+{_nodesAddedWindow})"
                + (_drawCallsMax > 0 ? $" | draw calls avg {_drawCallsSum / FrameMs.Count} max {_drawCallsMax}" : "")
        );
    }

    // Cumulative counters, sampled once per frame.
    private readonly record struct Counters(
        long JitMethods,
        double JitMs,
        int GcMinor,
        int GcMajor,
        double Pipelines,
        double TextureBytes,
        double Nodes
    )
    {
        public static Counters Take() =>
            new(
                JitInfo.GetCompiledMethodCount(),
                JitInfo.GetCompilationTime().TotalMilliseconds,
                GC.CollectionCount(0),
                GC.CollectionCount(GC.MaxGeneration),
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas)
                    + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh)
                    + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface)
                    + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw),
                Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed),
                Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)
            );

        public Counters Since(Counters before) =>
            new(
                JitMethods - before.JitMethods,
                JitMs - before.JitMs,
                GcMinor - before.GcMinor,
                GcMajor - before.GcMajor,
                Pipelines - before.Pipelines,
                TextureBytes - before.TextureBytes,
                Nodes - before.Nodes
            );
    }
}
