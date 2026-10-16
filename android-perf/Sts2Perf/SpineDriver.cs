using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace STS2Perf;

// The game's creatures are Spine skeletons (the spine-godot extension's
// SpineSprite), each updated by the engine on every frame: advance the
// animation, pose the bones, rebuild the meshes. That runs on the main thread
// inside "logic", where neither the game's scripts nor its async code show
// it. On a Pad 5 the Phantasmal Gardeners elite (four of them) ran at
// 13.5 fps with 65 ms of logic on every frame while nothing else was going
// on, and the Slumbering Beetle's fight at 17-20 fps while it slept.
//
// With the frame log on, or spine_rate set, the add-on drives the skeletons
// itself: each SpineSprite the engine was updating every frame
// (update_mode Process) is switched to Manual, and the add-on calls its
// update_skeleton(delta) from SceneTree.process_frame, timed:
//
//   [Perf] frames 10s: ... | spine 4 skeletons 41.3 ms/frame (phantasmal_gardener 10.2 ms x4, ...)
//   [Perf] hitch 74 ms: spine 52 ms; ...
//
// spine_rate=half|third (Perf settings → Creature animation): each skeleton
// updates every 2nd or 3rd frame instead, with the time since its last
// update, so animations keep their speed and timing at a lower frame rate.
// The skeletons take turns, so the cost spreads evenly over frames. With
// game (the default) and the frame log off, the engine updates them as it
// always does.
//
// If update_skeleton fails, every skeleton goes back to the engine.
internal static class SpineDriver
{
    private const string SpineClass = "SpineSprite";
    private const int ModeProcess = 0;
    private const int ModeManual = 2;

    private sealed class Driven
    {
        public Node2D Sprite;
        public string Owner;
        public int Phase;
        public double Pending;
    }

    private static bool _active;
    private static bool _failed;
    private static int _rate = 1;
    private static long _frame;
    private static int _nextPhase;
    private static readonly List<Driven> Sprites = new();
    private static readonly HashSet<ulong> Known = new();
    private static double _frameMs;
    private static double _windowMs;
    private static readonly Dictionary<string, (double Ms, int Count)> WindowByOwner = new();

    // From PerfRuntime.ApplyAll once the game's UI is up.
    public static void Apply()
    {
        if (_failed || Engine.GetMainLoop() is not SceneTree tree)
            return;
        var rate = PerfSettings.Value("spine_rate") switch
        {
            "half" => 2,
            "third" => 3,
            _ => 1,
        };
        var active = rate > 1 || PerfSettings.FrameLogSeconds > 0;
        if (active && !_active)
        {
            _active = true;
            tree.NodeAdded += OnNodeAdded;
            tree.ProcessFrame += OnFrame;
            foreach (var node in tree.Root.FindChildren("*", SpineClass, true, false))
                Take(node);
            PerfEntry.Log($"creature animation: driven by the add-on ({Sprites.Count} skeletons now, new ones as they appear)");
        }
        else if (!active && _active)
        {
            Stop(tree, "creature animation: back to the engine");
        }
        if (rate != _rate)
        {
            _rate = rate;
            PerfEntry.Log(rate == 1 ? "creature animation: every frame" : $"creature animation: every {(rate == 2 ? "2nd" : "3rd")} frame");
        }
    }

    private static void OnNodeAdded(Node node)
    {
        try
        {
            // GDExtension classes come through as their engine base class.
            if (node.GetType() == typeof(Node2D) && node.GetClass() == SpineClass)
                Take(node);
        }
        catch
        {
            // Never let this break the game's node setup.
        }
    }

    private static void Take(Node node)
    {
        if (node is not Node2D sprite || !Known.Add(sprite.GetInstanceId()))
            return;
        // Only what the engine was updating every frame; the game may drive
        // some skeletons itself.
        if (sprite.Get("update_mode").AsInt32() != ModeProcess)
            return;
        sprite.Set("update_mode", ModeManual);
        Sprites.Add(new Driven { Sprite = sprite, Owner = OwnerName(sprite), Phase = _nextPhase++ });
    }

    private static string OwnerName(Node node)
    {
        for (var n = node; n != null; n = n.GetParent())
        {
            var scene = n.SceneFilePath;
            if (!string.IsNullOrEmpty(scene))
            {
                var name = scene[(scene.LastIndexOf('/') + 1)..];
                var dot = name.LastIndexOf('.');
                return dot > 0 ? name[..dot] : name;
            }
        }
        return node.Name;
    }

    private static void OnFrame()
    {
        // Without the frame log nobody reads or resets it.
        if (PerfSettings.FrameLogSeconds == 0)
            _frameMs = 0;
        if (Sprites.Count == 0)
            return;
        _frame++;
        var tree = (SceneTree)Engine.GetMainLoop();
        var delta = tree.Root.GetProcessDeltaTime();
        for (var i = Sprites.Count - 1; i >= 0; i--)
        {
            var d = Sprites[i];
            if (!GodotObject.IsInstanceValid(d.Sprite))
            {
                Sprites.RemoveAt(i);
                continue;
            }
            // Paused with the game, as the engine's own update would be.
            if (!d.Sprite.CanProcess())
                continue;
            d.Pending += delta;
            if ((_frame + d.Phase) % _rate != 0)
                continue;
            var start = Stopwatch.GetTimestamp();
            try
            {
                d.Sprite.Call("update_skeleton", d.Pending);
            }
            catch (Exception ex)
            {
                _failed = true;
                Stop(tree, $"creature animation: update_skeleton failed ({ex.Message}), back to the engine");
                return;
            }
            d.Pending = 0;
            var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            _frameMs += ms;
            _windowMs += ms;
            WindowByOwner[d.Owner] = WindowByOwner.TryGetValue(d.Owner, out var s) ? (s.Ms + ms, s.Count + 1) : (ms, 1);
        }
    }

    private static void Stop(SceneTree tree, string why)
    {
        tree.NodeAdded -= OnNodeAdded;
        tree.ProcessFrame -= OnFrame;
        foreach (var d in Sprites)
            if (GodotObject.IsInstanceValid(d.Sprite))
                d.Sprite.Set("update_mode", ModeProcess);
        Sprites.Clear();
        Known.Clear();
        _active = false;
        PerfEntry.Log(why);
    }

    // For FrameLog's hitch line: this frame's skeleton updates, or null.
    public static string FrameSummary() => _frameMs >= 1 ? $"spine {_frameMs:0} ms" : null;

    // From FrameLog after each frame's report.
    public static void ResetFrame() => _frameMs = 0;

    // For FrameLog's stats line; starts a new window.
    public static string TakeWindowSummary(int frames)
    {
        if (!_active || frames == 0)
            return "";
        var top = WindowByOwner.OrderByDescending(kv => kv.Value.Ms).Take(4)
            .Select(kv => $"{kv.Key} {kv.Value.Ms / frames:0.0} ms x{(double)kv.Value.Count / frames * _rate:0.#}");
        var text = $" | spine {Sprites.Count} skeletons {_windowMs / frames:0.0} ms/frame ({string.Join(", ", top)})";
        _windowMs = 0;
        WindowByOwner.Clear();
        return text;
    }
}
