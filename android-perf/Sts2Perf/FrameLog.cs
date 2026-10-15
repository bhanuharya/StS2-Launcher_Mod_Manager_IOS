using System;
using System.Collections.Generic;
using Godot;

namespace STS2Perf;

// Frame-time stats for benchmarking, every frame_log=N seconds (PerfSettings),
// one [Perf] line in the launcher console and logcat:
//
//   [Perf] frames 10s: 59.8 fps | ms p50 16.7 p95 17.9 p99 24.1 max 41.3 | hitches 3
//          | cap 60 | vsync Enabled | render 2560x1600 of 2560x1600 | resolution full
//          | effects on (hdr2d off) | particles 100%
//          | gc g0 +14 g1 +1 g2 +0 paused 3 ms
//
// Times are main-loop iterations (SceneTree.process_frame), which on Android
// is also the render thread, so they include rendering. "hitches" counts
// frames over 1.5x the median. The gc part is collections and total GC pause
// during the window, to tell .NET pauses apart from GPU and load hitches.
internal static class FrameLog
{
    private static int _seconds;
    private static bool _hooked;
    private static ulong _lastUsec;
    private static ulong _windowStartUsec;
    private static readonly List<float> FrameMs = new(8192);
    private static int _gcG0;
    private static int _gcG1;
    private static int _gcG2;
    private static double _gcPauseMs;
    private static bool _gcSeen;

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
            FrameMs.Clear();
            _gcSeen = false; // skip a half-window of GC counts
            if (seconds > 0 && !_hooked)
            {
                tree.ProcessFrame += OnFrame;
                _hooked = true;
            }
            else if (seconds == 0 && _hooked)
            {
                tree.ProcessFrame -= OnFrame;
                _hooked = false;
            }
            PerfEntry.Log(seconds > 0 ? $"frame log every {seconds}s" : "frame log off");
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"frame log failed: {ex.Message}");
        }
    }

    private static void OnFrame()
    {
        var now = Time.GetTicksUsec();
        if (_lastUsec == 0)
        {
            _lastUsec = _windowStartUsec = now;
            return;
        }
        FrameMs.Add((now - _lastUsec) / 1000f);
        _lastUsec = now;
        if (now - _windowStartUsec < (ulong)_seconds * 1_000_000UL)
            return;

        var elapsed = (now - _windowStartUsec) / 1_000_000f;
        _windowStartUsec = now;
        try
        {
            Report(elapsed);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"frame log report failed: {ex.Message}");
        }
        FrameMs.Clear();
    }

    private static void Report(float elapsed)
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

        var gc = "";
        var (g0, g1, g2, pauseMs) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds);
        if (_gcSeen)
            gc = $" | gc g0 +{g0 - _gcG0} g1 +{g1 - _gcG1} g2 +{g2 - _gcG2} paused {Math.Max(0, pauseMs - _gcPauseMs):0} ms";
        (_gcG0, _gcG1, _gcG2, _gcPauseMs, _gcSeen) = (g0, g1, g2, pauseMs, true);

        PerfEntry.Log(
            $"frames {elapsed:0}s: {FrameMs.Count / elapsed:0.0} fps"
                + $" | ms p50 {median:0.0} p95 {P(0.95):0.0} p99 {P(0.99):0.0} max {FrameMs[^1]:0.0}"
                + $" | hitches {hitches} | cap {Engine.MaxFps} | vsync {DisplayServer.WindowGetVsyncMode()}"
                + $" | render {render.X:0}x{render.Y:0} of {screen.X}x{screen.Y}"
                + $" | resolution {PerfSettings.Value("resolution")}"
                + $" | effects {PerfSettings.Value("effects")} (hdr2d {(root.UseHdr2D ? "on" : "off")})"
                + $" | particles {PerfSettings.ParticlesPercent}%"
                + gc
        );
    }
}
