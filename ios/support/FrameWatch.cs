using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace Sts2Ios;

// Logs each frame that takes much longer than normal, with what changed
// during it, so a godot.log sent back from the phone shows what the short
// freezes are: graphics pipelines compiled on first use, textures loaded, or
// .NET garbage collection. Plus a one-line summary every minute.
internal static class FrameWatch
{
    private const double HitchMs = 100;
    private const double SuspendedMs = 3000; // longer gaps are the app being in the background
    private const double SummaryMs = 60_000;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Snapshot _prev;
    private static double _lastFrame;
    private static double _periodStart;
    private static int _frames;
    private static int _hitches;
    private static double _worst;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Hooks.Note("frame watch: no SceneTree, not started");
            return;
        }
        _prev = Snapshot.Take();
        _lastFrame = _periodStart = Clock.Elapsed.TotalMilliseconds;
        tree.ProcessFrame += OnFrame;
        Hooks.Note($"frame watch: logging frames over {HitchMs:0} ms, summary every {SummaryMs / 1000:0} s");
    }

    private static void OnFrame()
    {
        var now = Clock.Elapsed.TotalMilliseconds;
        var frame = now - _lastFrame;
        _lastFrame = now;
        var snap = Snapshot.Take();

        if (frame >= SuspendedMs)
        {
            Hooks.Note($"frame watch: {frame / 1000:0.0} s gap (app was in the background?)");
            _periodStart = now;
            _frames = _hitches = 0;
            _worst = 0;
        }
        else
        {
            _frames++;
            _worst = Math.Max(_worst, frame);
            if (frame >= HitchMs)
            {
                _hitches++;
                Hooks.Note($"hitch {frame:0} ms: {snap.ChangesSince(_prev)}");
            }
        }
        _prev = snap;

        var period = now - _periodStart;
        if (period >= SummaryMs)
        {
            Hooks.Note(
                $"frames {period / 1000:0} s: {_frames * 1000 / period:0.0} fps avg, worst {_worst:0} ms, {_hitches} over {HitchMs:0} ms"
            );
            _periodStart = now;
            _frames = _hitches = 0;
            _worst = 0;
        }
    }

    private readonly record struct Snapshot(
        double Canvas,
        double Mesh,
        double Surface,
        double Draw,
        double TextureBytes,
        double Resources,
        int Gen0,
        int Gen1,
        int Gen2,
        TimeSpan GcPause
    )
    {
        public static Snapshot Take() =>
            new(
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas),
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh),
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface),
                Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw),
                Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed),
                Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                GC.GetTotalPauseDuration()
            );

        public string ChangesSince(Snapshot before)
        {
            var parts = new List<string>();

            var pipelines = new List<string>();
            AddCount(pipelines, "canvas", Canvas - before.Canvas);
            AddCount(pipelines, "mesh", Mesh - before.Mesh);
            AddCount(pipelines, "surface", Surface - before.Surface);
            AddCount(pipelines, "draw", Draw - before.Draw);
            if (pipelines.Count > 0)
                parts.Add($"pipelines compiled ({string.Join(", ", pipelines)})");

            var textureMb = (TextureBytes - before.TextureBytes) / 1048576;
            if (Math.Abs(textureMb) >= 0.1)
                parts.Add($"texture memory {textureMb:+0.0;-0.0} MB");

            var resources = Resources - before.Resources;
            if (resources != 0)
                parts.Add($"resources {resources:+0;-0}");

            if (Gen0 != before.Gen0 || Gen1 != before.Gen1 || Gen2 != before.Gen2)
                parts.Add(
                    $"GC gen0 +{Gen0 - before.Gen0} gen1 +{Gen1 - before.Gen1} gen2 +{Gen2 - before.Gen2}, "
                        + $"paused {(GcPause - before.GcPause).TotalMilliseconds:0} ms"
                );

            return parts.Count > 0
                ? string.Join("; ", parts)
                : "no pipeline, texture, resource or GC change (CPU work or the OS)";
        }

        private static void AddCount(List<string> into, string name, double delta)
        {
            if (delta > 0)
                into.Add($"{name} {delta:0}");
        }
    }
}
