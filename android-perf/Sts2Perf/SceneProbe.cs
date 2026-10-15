using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Perf;

// probe=next_fight (Perf settings → Scene probe): once, a few seconds into
// the next fight, logs what the screen is made of and what each part of it
// costs the GPU, then switches itself back off.
//
// 1. Inventory, one walk of the tree, counting what is expensive to draw on
//    a mobile GPU: visible items covering most of the screen (each one a
//    full-screen fill), shader materials (and which read the screen, which
//    forces a copy of it), CanvasGroups and clip masks (drawn offscreen
//    first), 2D lights, SubViewports (a whole extra render each, with their
//    own GPU time), Spine skeletons; plus hidden nodes that still run code
//    every frame.
// 2. GPU bisect: hides the biggest visible branches one at a time through
//    RenderingServer (the game's own visibility state and signals stay
//    untouched) and measures the root viewport's GPU time without each.
//    Parts of the screen blink out for a moment each, for about half a
//    minute in all.
internal static class SceneProbe
{
    private const int BaselineFrames = 12;
    private const int SettleFrames = 6;
    private const int HiddenFrames = 16;
    private const int MaxCandidates = 18;
    private const double FightSettleSeconds = 5;

    private sealed class Item
    {
        public Node Node;
        public int Parent;
        public int Depth;
        public int Subtree = 1;
        public bool Visible;
        public bool FullScreen;
    }

    private record Candidate(CanvasItem Node, string Label, int Nodes);

    private static ulong _fightSeenUsec;
    private static bool _running;
    private static List<Candidate> _candidates;
    private static int _index;
    private static int _frame;
    private static bool _hidden;
    private static readonly List<float> Samples = new();
    private static readonly List<float> FrameSamples = new();
    private static float _baseline;
    private static ulong _lastFrameUsec;
    private static readonly List<(string Label, int Nodes, float Baseline, float Hidden)> Results = new();
    private static List<Rid> _subViewports = new();
    private static List<string> _subViewportLabels = new();

    // From PerfRuntime's 2-second tick while the setting is armed.
    public static void Tick()
    {
        if (_running || PerfSettings.Value("probe") != "next_fight")
            return;
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        if (FindVisible(tree.Root, "NPlayerHand") == null)
        {
            _fightSeenUsec = 0;
            return;
        }
        var now = Time.GetTicksUsec();
        if (_fightSeenUsec == 0)
        {
            _fightSeenUsec = now;
            PerfEntry.Log($"probe: fight found, starting in {FightSettleSeconds:0} s (don't touch the screen for about 30 s)");
            return;
        }
        if (now - _fightSeenUsec < FightSettleSeconds * 1_000_000)
            return;
        Start(tree);
    }

    private static Node FindVisible(Node root, string typeName)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.GetType().Name == typeName)
                return node is CanvasItem ci && !ci.IsVisibleInTree() ? null : node;
            foreach (var child in node.GetChildren())
                stack.Push(child);
        }
        return null;
    }

    private static void Start(SceneTree tree)
    {
        _running = true;
        try
        {
            var items = Walk(tree.Root);
            Inventory(tree, items);
            _candidates = PickCandidates(items);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"probe: inventory failed: {ex}");
            Finish();
            return;
        }
        if (_candidates.Count == 0)
        {
            Finish();
            return;
        }
        RenderingServer.ViewportSetMeasureRenderTime(tree.Root.GetViewportRid(), true);
        _index = -1;
        _frame = 0;
        _hidden = false;
        Samples.Clear();
        FrameSamples.Clear();
        Results.Clear();
        _lastFrameUsec = 0;
        tree.ProcessFrame += OnFrame;
        PerfEntry.Log($"probe: GPU bisect over {_candidates.Count} parts of the screen");
    }

    private static List<Item> Walk(Node root)
    {
        var items = new List<Item>();
        var stack = new Stack<(Node Node, int Parent, int Depth)>();
        stack.Push((root, -1, 0));
        var screen = ((SceneTree)Engine.GetMainLoop()).Root.GetVisibleRect();
        var screenArea = Math.Max(1f, screen.Size.X * screen.Size.Y);
        while (stack.Count > 0)
        {
            var (node, parent, depth) = stack.Pop();
            var item = new Item { Node = node, Parent = parent, Depth = depth };
            if (node is CanvasItem ci)
            {
                item.Visible = ci.IsVisibleInTree();
                if (item.Visible && Draws(ci) && CoveredArea(ci, screen) >= screenArea * 0.5f)
                    item.FullScreen = true;
            }
            var index = items.Count;
            items.Add(item);
            // SubViewports render on their own; their children aren't on this screen.
            foreach (var child in node.GetChildren())
                stack.Push((child, index, depth + 1));
        }
        for (var i = items.Count - 1; i > 0; i--)
            items[items[i].Parent].Subtree += items[i].Subtree;
        return items;
    }

    // Items that fill pixels themselves (containers and plain nodes don't).
    private static bool Draws(CanvasItem ci) =>
        ci is TextureRect or ColorRect or NinePatchRect or Sprite2D or Panel or PanelContainer or BackBufferCopy
        || ci.Material != null;

    private static float CoveredArea(CanvasItem ci, Rect2 screen)
    {
        Rect2 rect;
        switch (ci)
        {
            case Control control:
                rect = control.GetGlobalRect();
                break;
            case Sprite2D sprite:
                rect = sprite.GlobalTransform * sprite.GetRect();
                break;
            default:
                return 0;
        }
        var clipped = rect.Intersection(screen);
        return clipped.Size.X * clipped.Size.Y;
    }

    private static string Label(Node node)
    {
        var scene = node.SceneFilePath;
        var file = string.IsNullOrEmpty(scene) ? "" : $", {scene[(scene.LastIndexOf('/') + 1)..]}";
        var type = node.GetType() == typeof(GodotObject) || node.GetType().Namespace == "Godot" ? node.GetClass() : node.GetType().Name;
        var parent = node.GetParent();
        var path = parent != null && parent.GetParent() != null ? $"{parent.Name}/{node.Name}" : node.Name.ToString();
        return $"{path} ({type}{file})";
    }

    private static void Inventory(SceneTree tree, List<Item> items)
    {
        var visible = 0;
        var hiddenProcessing = new Dictionary<string, int>();
        var shaders = new Dictionary<string, (int Count, bool ScreenRead)>();
        var counts = new Dictionary<string, int>();
        var fullScreen = new List<string>();
        _subViewports = new List<Rid>();
        _subViewportLabels = new List<string>();
        void Count(string what) => counts[what] = counts.TryGetValue(what, out var n) ? n + 1 : 1;

        foreach (var item in items)
        {
            var node = item.Node;
            var cls = node.GetClass();
            if (cls == "SpineSprite")
                Count("Spine skeletons");
            switch (node)
            {
                case SubViewport sub:
                    _subViewports.Add(sub.GetViewportRid());
                    _subViewportLabels.Add($"{Label(sub)} {sub.Size.X}x{sub.Size.Y} update {sub.RenderTargetUpdateMode}");
                    RenderingServer.ViewportSetMeasureRenderTime(sub.GetViewportRid(), true);
                    break;
                case Light2D light when light.IsVisibleInTree():
                    Count(light.ShadowEnabled ? "2D lights with shadows" : "2D lights");
                    break;
                case CanvasModulate:
                    Count("CanvasModulate");
                    break;
            }
            if (node is not CanvasItem ci)
                continue;
            if (!item.Visible)
            {
                if (node.IsProcessing() || node.IsPhysicsProcessing())
                {
                    var key = SceneOf(node);
                    hiddenProcessing[key] = hiddenProcessing.TryGetValue(key, out var n) ? n + 1 : 1;
                }
                continue;
            }
            visible++;
            if (ci is CanvasGroup)
                Count("CanvasGroups (offscreen)");
            if (ci is BackBufferCopy)
                Count("BackBufferCopy (screen copies)");
            if (ci.ClipChildren != CanvasItem.ClipChildrenMode.Disabled)
                Count("clip masks (offscreen)");
            if (ci is GpuParticles2D or CpuParticles2D)
                Count("particle emitters");
            if (ci.Material is ShaderMaterial { Shader: { } shader })
            {
                var key = string.IsNullOrEmpty(shader.ResourcePath) ? $"inline in {SceneOf(node)}" : shader.ResourcePath;
                var code = shader.Code ?? "";
                var screenRead = code.Contains("hint_screen_texture") || code.Contains("SCREEN_TEXTURE");
                var had = shaders.TryGetValue(key, out var s) ? s : (0, false);
                shaders[key] = (had.Item1 + 1, had.Item2 || screenRead);
            }
            if (item.FullScreen && fullScreen.Count < 25)
                fullScreen.Add(Label(node) + (ci.Material != null ? " +material" : ""));
        }

        PerfEntry.Log(
            $"probe: {items.Count} nodes, {visible} visible canvas items, {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)} draw calls"
                + $" | {string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}"
        );
        PerfEntry.Log($"probe: {fullScreen.Count} visible items cover half the screen or more (each a full-screen fill): {string.Join("; ", fullScreen)}");
        PerfEntry.Log(
            $"probe: shaders on visible items: "
                + string.Join("; ", shaders.OrderByDescending(kv => kv.Value.Count).Take(20).Select(kv => $"{kv.Key} x{kv.Value.Count}{(kv.Value.ScreenRead ? " (reads the screen)" : "")}"))
        );
        PerfEntry.Log(
            $"probe: hidden but running code every frame: {hiddenProcessing.Values.Sum()} nodes ("
                + string.Join(", ", hiddenProcessing.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}"))
                + ")"
        );
        PerfEntry.Log($"probe: {_subViewports.Count} SubViewports: {string.Join("; ", _subViewportLabels)}");
        PerfEntry.Log(
            "probe: top of the tree: "
                + string.Join("; ", items.Where(i => i.Depth is >= 2 and <= 4 && i.Subtree >= 30).OrderByDescending(i => i.Subtree).Take(20)
                    .Select(i => $"{Label(i.Node)} {i.Subtree} nodes{(i.Visible ? "" : " hidden")}"))
        );
    }

    // The node's own scene file, or the nearest ancestor's.
    private static string SceneOf(Node node)
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
        return node.GetClass();
    }

    // The biggest visible branches, skipping wrappers that hold nearly the
    // same nodes as a branch already picked, plus every full-screen item and
    // the player's hand.
    private static List<Candidate> PickCandidates(List<Item> items)
    {
        var total = items.Count;
        var picked = new List<int>();
        bool Covered(int i)
        {
            for (var p = items[i].Parent; p >= 0; p = items[p].Parent)
                if (picked.Contains(p) && items[p].Subtree < items[i].Subtree * 1.3f)
                    return true;
            return false;
        }
        bool Usable(int i) => items[i].Node is CanvasItem && items[i].Visible && items[i].Subtree < total * 0.9f;

        foreach (var i in Enumerable.Range(0, items.Count).Where(i => Usable(i) && items[i].Subtree >= 10).OrderByDescending(i => items[i].Subtree))
        {
            if (picked.Count >= MaxCandidates - 4)
                break;
            if (!Covered(i))
                picked.Add(i);
        }
        foreach (var i in Enumerable.Range(0, items.Count).Where(i => Usable(i) && (items[i].FullScreen || items[i].Node.GetType().Name == "NPlayerHand")))
        {
            if (picked.Count >= MaxCandidates)
                break;
            if (!picked.Contains(i))
                picked.Add(i);
        }
        return picked.Select(i => new Candidate((CanvasItem)items[i].Node, Label(items[i].Node), items[i].Subtree)).ToList();
    }

    private static void OnFrame()
    {
        try
        {
            Step();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"probe failed: {ex.Message}");
            Restore();
            Finish();
        }
    }

    private static void Step()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var now = Time.GetTicksUsec();
        var frameMs = _lastFrameUsec == 0 ? 0 : (now - _lastFrameUsec) / 1000f;
        _lastFrameUsec = now;
        var gpu = (float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(tree.Root.GetViewportRid());
        _frame++;

        if (!_hidden)
        {
            // Measuring the baseline before the next candidate.
            if (_frame > 2)
                Samples.Add(gpu);
            if (_frame < BaselineFrames)
                return;
            _baseline = Median(Samples);
            if (_index == -1)
                LogSubViewports();
            Samples.Clear();
            FrameSamples.Clear();
            _index++;
            while (_index < _candidates.Count && !GodotObject.IsInstanceValid(_candidates[_index].Node))
                _index++;
            if (_index >= _candidates.Count)
            {
                Report();
                Finish();
                return;
            }
            RenderingServer.CanvasItemSetVisible(_candidates[_index].Node.GetCanvasItem(), false);
            _hidden = true;
            _frame = 0;
            return;
        }

        if (_frame > SettleFrames)
        {
            Samples.Add(gpu);
            FrameSamples.Add(frameMs);
        }
        if (_frame < SettleFrames + HiddenFrames)
            return;
        var candidate = _candidates[_index];
        Results.Add((candidate.Label, candidate.Nodes, _baseline, Median(Samples)));
        Restore();
        Samples.Clear();
        _frame = 0;
    }

    private static void Restore()
    {
        if (!_hidden || _candidates == null || _index < 0 || _index >= _candidates.Count)
            return;
        var node = _candidates[_index].Node;
        if (GodotObject.IsInstanceValid(node))
            RenderingServer.CanvasItemSetVisible(node.GetCanvasItem(), node.Visible);
        _hidden = false;
    }

    private static void LogSubViewports()
    {
        if (_subViewports.Count == 0)
            return;
        var parts = new List<string>();
        for (var i = 0; i < _subViewports.Count; i++)
            parts.Add($"{_subViewportLabels[i]}: gpu {RenderingServer.ViewportGetMeasuredRenderTimeGpu(_subViewports[i]):0.0} ms");
        PerfEntry.Log($"probe: SubViewport GPU time (on top of the screen's {_baseline:0.0} ms): {string.Join("; ", parts)}");
    }

    private static void Report()
    {
        PerfEntry.Log($"probe: screen GPU time {Results.FirstOrDefault().Baseline:0.0} ms per frame; hiding each part:");
        foreach (var r in Results.OrderByDescending(r => r.Baseline - r.Hidden))
            PerfEntry.Log($"probe:   {r.Baseline - r.Hidden,5:+0.0;-0.0} ms saved without {r.Label}, {r.Nodes} nodes ({r.Baseline:0.0} -> {r.Hidden:0.0})");
    }

    private static void Finish()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame -= OnFrame;
            if (PerfSettings.FrameLogSeconds == 0)
                RenderingServer.ViewportSetMeasureRenderTime(tree.Root.GetViewportRid(), false);
        }
        foreach (var rid in _subViewports)
            RenderingServer.ViewportSetMeasureRenderTime(rid, false);
        _running = false;
        _fightSeenUsec = 0;
        _candidates = null;
        PerfSettings.Set("probe", "off");
        PerfEntry.Log("probe: done, Scene probe is off again");
    }

    private static float Median(List<float> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
