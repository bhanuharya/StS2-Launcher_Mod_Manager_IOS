using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Perf;

// probe=next_fight (Perf settings → Scene probe): once, a few seconds into
// the next fight, logs what the screen is made of and what each part of it
// costs the GPU, then switches itself back off.
//
// 1. Inventory, one walk of the tree, of what is expensive to draw on a
//    mobile GPU: visible items covering most of the screen (each one a
//    full-screen fill), shader materials (which read the screen, and their
//    parameters), CanvasGroups and clip masks (drawn offscreen first, each
//    listed), 2D lights, SubViewports (a whole extra render each, with their
//    own GPU time), Spine skeletons; plus hidden nodes that still run code
//    every frame.
// 2. GPU bisect: hides the biggest visible branches one at a time through
//    RenderingServer (the game's own visibility state and signals stay
//    untouched) and measures the root viewport's GPU time without each.
// 3. Experiments: the same measurement with one candidate fix applied at a
//    time (card CanvasGroups off, clip masks off, a shader removed, the
//    background layers or the transition overlay hidden), to size each fix
//    before it becomes a setting.
// Parts of the screen blink or change for a moment each, for about a minute.
//
// Snapshot(), called by FrameLog when GPU frames spike, logs what is on
// screen at that moment the same way, for slowdowns too short for the probe.
// With probe=spikes it then tests, while the slowdown lasts, what the GPU
// time is with one thing hidden at a time: scenes added in the last few
// seconds first (the enemy-turn banner, cards flying off), then the biggest
// branches. A few tests per slowdown, for the next five slowdowns.
internal static class SceneProbe
{
    private const int BaselineFrames = 12;
    private const int SettleFrames = 6;
    private const int MeasureFrames = 16;
    private const int MaxBisect = 16;
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

    private sealed record Step(string Section, string Label, int Nodes, Action Apply, Action Undo);

    private static ulong _fightSeenUsec;
    private static bool _running;
    private static List<Step> _steps;
    private static int _index;
    private static int _frame;
    private static bool _applied;
    private static readonly List<float> Samples = new();
    private static float _baseline;
    private static float _firstBaseline;
    private static readonly List<(Step Step, float Baseline, float With)> Results = new();
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
            PerfEntry.Log($"probe: fight found, starting in {FightSettleSeconds:0} s (don't touch the screen for about a minute)");
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
            Inventory(items, "probe");
            _steps = Bisect(items).Concat(Experiments(items)).ToList();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"probe: inventory failed: {ex}");
            Finish();
            return;
        }
        if (_steps.Count == 0)
        {
            Finish();
            return;
        }
        RenderingServer.ViewportSetMeasureRenderTime(tree.Root.GetViewportRid(), true);
        _index = -1;
        _frame = 0;
        _applied = false;
        _firstBaseline = 0;
        Samples.Clear();
        Results.Clear();
        tree.ProcessFrame += OnFrame;
        PerfEntry.Log($"probe: measuring {_steps.Count} parts and fixes (settings now: {PerfSettings.Summary()})");
    }

    // ---- inventory -------------------------------------------------------

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
        var type = node.GetType().Namespace == "Godot" ? node.GetClass() : node.GetType().Name;
        var parent = node.GetParent();
        var path = parent != null && parent.GetParent() != null ? $"{parent.Name}/{node.Name}" : node.Name.ToString();
        return $"{path} ({type}{file})";
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

    private static string ShaderPath(Shader shader, Node owner) =>
        string.IsNullOrEmpty(shader.ResourcePath) ? $"inline in {SceneOf(owner)}" : shader.ResourcePath;

    private static bool ReadsScreen(Shader shader)
    {
        var code = shader.Code ?? "";
        return code.Contains("hint_screen_texture") || code.Contains("SCREEN_TEXTURE");
    }

    // "name=value" for a material's non-texture parameters.
    private static string Parameters(ShaderMaterial material)
    {
        var parts = new List<string>();
        foreach (var uniform in material.Shader.GetShaderUniformList())
        {
            var name = uniform.AsGodotDictionary()["name"].AsString();
            var value = material.GetShaderParameter(name);
            parts.Add(value.VariantType == Variant.Type.Object ? $"{name}=<texture>" : $"{name}={value}");
            if (parts.Count >= 12)
                break;
        }
        return string.Join(" ", parts);
    }

    private static void Inventory(List<Item> items, string tag)
    {
        var visible = 0;
        var hiddenProcessing = new Dictionary<string, int>();
        var shaders = new Dictionary<string, (int Count, bool ScreenRead, string Example)>();
        var counts = new Dictionary<string, int>();
        var fullScreen = new List<string>();
        var groups = new List<string>();
        var clips = new List<string>();
        var visibleScenes = new Dictionary<string, int>();
        _subViewports = new List<Rid>();
        _subViewportLabels = new List<string>();
        void Count(string what) => counts[what] = counts.TryGetValue(what, out var n) ? n + 1 : 1;

        foreach (var item in items)
        {
            var node = item.Node;
            if (node.GetClass() == "SpineSprite" && item.Visible)
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
            if (!string.IsNullOrEmpty(node.SceneFilePath))
            {
                var scene = SceneOf(node);
                visibleScenes[scene] = visibleScenes.TryGetValue(scene, out var n) ? n + 1 : 1;
            }
            if (ci is CanvasGroup cg)
            {
                Count("CanvasGroups (offscreen)");
                if (groups.Count < 12)
                    groups.Add($"{Label(cg)} in {SceneOf(cg.GetParent() ?? cg)}{(cg.UseMipmaps ? " +mipmaps" : "")}{(cg.Material is ShaderMaterial { Shader: { } s } ? $" shader {ShaderPath(s, cg)}" : "")}");
            }
            if (ci is BackBufferCopy)
                Count("BackBufferCopy (screen copies)");
            if (ci.ClipChildren != CanvasItem.ClipChildrenMode.Disabled)
            {
                Count("clip masks (offscreen)");
                if (clips.Count < 12)
                    clips.Add($"{Label(ci)} in {SceneOf(ci.GetParent() ?? ci)} {ci.ClipChildren}");
            }
            if (ci is GpuParticles2D or CpuParticles2D)
                Count("particle emitters");
            if (ci.Material is ShaderMaterial { Shader: { } shader } material)
            {
                var key = ShaderPath(shader, node);
                if (shaders.TryGetValue(key, out var s))
                    shaders[key] = (s.Count + 1, s.ScreenRead, s.Example);
                else
                    shaders[key] = (1, ReadsScreen(shader), $"{Label(node)}: {Parameters(material)}");
            }
            if (item.FullScreen && fullScreen.Count < 25)
                fullScreen.Add(Label(node) + (ci.Material != null ? " +material" : ""));
        }

        PerfEntry.Log(
            $"{tag}: {items.Count} nodes, {visible} visible canvas items, {RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)} draw calls"
                + $" | {string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}"
        );
        PerfEntry.Log($"{tag}: visible scenes: {string.Join(", ", visibleScenes.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{kv.Key} x{kv.Value}"))}");
        PerfEntry.Log($"{tag}: CanvasGroups: {string.Join("; ", groups)}");
        PerfEntry.Log($"{tag}: clip masks: {string.Join("; ", clips)}");
        PerfEntry.Log($"{tag}: {fullScreen.Count} visible items cover half the screen or more: {string.Join("; ", fullScreen)}");
        foreach (var (path, s) in shaders.OrderByDescending(kv => kv.Value.Count).Take(12))
            PerfEntry.Log($"{tag}: shader {path} x{s.Count}{(s.ScreenRead ? " (reads the screen)" : "")}, e.g. {s.Example}");
        if (tag != "probe")
            return;
        PerfEntry.Log(
            $"{tag}: hidden but running code every frame: {hiddenProcessing.Values.Sum()} nodes ("
                + string.Join(", ", hiddenProcessing.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}"))
                + ")"
        );
        PerfEntry.Log($"{tag}: {_subViewports.Count} SubViewports: {string.Join("; ", _subViewportLabels)}");
    }

    // ---- what to measure -------------------------------------------------

    private static Step Hide(string section, CanvasItem node, string label, int nodes) =>
        new(section, label, nodes,
            () =>
            {
                // Cards and effects come and go while the probe runs.
                if (GodotObject.IsInstanceValid(node))
                    RenderingServer.CanvasItemSetVisible(node.GetCanvasItem(), false);
            },
            () =>
            {
                if (GodotObject.IsInstanceValid(node))
                    RenderingServer.CanvasItemSetVisible(node.GetCanvasItem(), node.Visible);
            });

    private static Step Batch(string label, List<CanvasItem> nodes, Action<CanvasItem> apply, Action<CanvasItem> undo) =>
        new("fix", $"{label} ({nodes.Count} items)", nodes.Count,
            () => { foreach (var n in nodes) if (GodotObject.IsInstanceValid(n)) apply(n); },
            () => { foreach (var n in nodes) if (GodotObject.IsInstanceValid(n)) undo(n); });

    // The biggest visible branches, skipping wrappers that hold nearly the
    // same nodes as a branch already picked, plus the player's hand.
    private static IEnumerable<Step> Bisect(List<Item> items) =>
        Branches(items).Select(i => Hide("part", (CanvasItem)items[i].Node, Label(items[i].Node), items[i].Subtree));

    private static List<int> Branches(List<Item> items)
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
            if (picked.Count >= MaxBisect - 1)
                break;
            if (!Covered(i))
                picked.Add(i);
        }
        foreach (var i in Enumerable.Range(0, items.Count).Where(i => Usable(i) && items[i].Node.GetType().Name == "NPlayerHand"))
            if (!picked.Contains(i))
                picked.Add(i);
        return picked;
    }

    private static IEnumerable<Step> Experiments(List<Item> items)
    {
        var visible = items.Where(i => i.Visible && i.Node is CanvasItem).Select(i => (CanvasItem)i.Node).ToList();

        var cardGroups = visible.OfType<CanvasGroup>().Where(CardFx.InCard).Cast<CanvasItem>().ToList();
        if (cardGroups.Count > 0)
            yield return Batch("card CanvasGroups drawn directly (card_fx=fast)", cardGroups,
                n => CardFx.SetGroup((CanvasGroup)n, false),
                n => CardFx.SetGroup((CanvasGroup)n, PerfSettings.Value("card_fx") != "fast"));

        var otherGroups = visible.OfType<CanvasGroup>().Where(g => !CardFx.InCard(g)).Cast<CanvasItem>().ToList();
        if (otherGroups.Count > 0)
            yield return Batch("other CanvasGroups drawn directly", otherGroups,
                n => RenderingServer.CanvasItemSetCanvasGroupMode(n.GetCanvasItem(), RenderingServer.CanvasGroupMode.Disabled),
                n => { var g = (CanvasGroup)n; RenderingServer.CanvasItemSetCanvasGroupMode(g.GetCanvasItem(), RenderingServer.CanvasGroupMode.Transparent, g.ClearMargin, true, g.FitMargin, g.UseMipmaps); });

        var clips = visible.Where(n => n.ClipChildren != CanvasItem.ClipChildrenMode.Disabled).ToList();
        if (clips.Count > 0)
            yield return Batch("clip masks off", clips,
                n => RenderingServer.CanvasItemSetCanvasGroupMode(n.GetCanvasItem(), RenderingServer.CanvasGroupMode.Disabled),
                n => RenderingServer.CanvasItemSetCanvasGroupMode(n.GetCanvasItem(), (RenderingServer.CanvasGroupMode)(int)n.ClipChildren));

        foreach (var shader in visible.Select(n => n.Material).OfType<ShaderMaterial>().Where(m => m.Shader != null)
                     .GroupBy(m => m.Shader).OrderByDescending(g => g.Count()).Take(4))
        {
            var users = visible.Where(n => n.Material is ShaderMaterial m && m.Shader == shader.Key).ToList();
            var name = string.IsNullOrEmpty(shader.Key.ResourcePath) ? "an inline shader" : shader.Key.ResourcePath[(shader.Key.ResourcePath.LastIndexOf('/') + 1)..];
            yield return Batch($"{name} removed", users,
                n => RenderingServer.CanvasItemSetMaterial(n.GetCanvasItem(), new Rid()),
                n => RenderingServer.CanvasItemSetMaterial(n.GetCanvasItem(), n.Material?.GetRid() ?? new Rid()));
        }

        var layers = items.Where(i => i.FullScreen && i.Node is TextureRect && !string.IsNullOrEmpty(i.Node.SceneFilePath)).Select(i => (CanvasItem)i.Node).ToList();
        if (layers.Count > 0)
            yield return Batch("background layers hidden", layers,
                n => RenderingServer.CanvasItemSetVisible(n.GetCanvasItem(), false),
                n => RenderingServer.CanvasItemSetVisible(n.GetCanvasItem(), n.Visible));
        foreach (var layer in layers)
            yield return Hide("layer", layer, Label(layer), 1);

        var transitions = visible.Where(n => n.GetType().Name == "NTransition").ToList();
        if (transitions.Count > 0)
            yield return Batch("transition overlay hidden", transitions,
                n => RenderingServer.CanvasItemSetVisible(n.GetCanvasItem(), false),
                n => RenderingServer.CanvasItemSetVisible(n.GetCanvasItem(), n.Visible));
    }

    // ---- measuring -------------------------------------------------------

    private static void OnFrame()
    {
        try
        {
            Step_();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"probe failed: {ex.Message}");
            Undo();
            Finish();
        }
    }

    private static void Step_()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var gpu = (float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(tree.Root.GetViewportRid());
        _frame++;

        if (!_applied)
        {
            // The baseline right before the next step.
            if (_frame > 2)
                Samples.Add(gpu);
            if (_frame < BaselineFrames)
                return;
            _baseline = Median(Samples);
            if (_index == -1)
            {
                _firstBaseline = _baseline;
                LogSubViewports();
            }
            Samples.Clear();
            _index++;
            if (_index >= _steps.Count)
            {
                Report();
                Finish();
                return;
            }
            _steps[_index].Apply();
            _applied = true;
            _frame = 0;
            return;
        }

        if (_frame > SettleFrames)
            Samples.Add(gpu);
        if (_frame < SettleFrames + MeasureFrames)
            return;
        Results.Add((_steps[_index], _baseline, Median(Samples)));
        Undo();
        Samples.Clear();
        _frame = 0;
    }

    private static void Undo()
    {
        if (!_applied || _steps == null || _index < 0 || _index >= _steps.Count)
            return;
        _steps[_index].Undo();
        _applied = false;
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
        PerfEntry.Log($"probe: screen GPU time {_firstBaseline:0.0} ms per frame");
        foreach (var section in new[] { ("part", "hiding each part"), ("layer", "hiding each background layer"), ("fix", "candidate fixes") })
        {
            var rows = Results.Where(r => r.Step.Section == section.Item1).OrderByDescending(r => r.Baseline - r.With).ToList();
            if (rows.Count == 0)
                continue;
            PerfEntry.Log($"probe: {section.Item2}:");
            foreach (var r in rows)
                PerfEntry.Log($"probe:   {r.Baseline - r.With,5:+0.0;-0.0} ms: {r.Step.Label}{(r.Step.Section == "part" ? $", {r.Step.Nodes} nodes" : "")} ({r.Baseline:0.0} -> {r.With:0.0})");
        }
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
        _steps = null;
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

    // ---- spikes ----------------------------------------------------------

    private static int _snapshots;
    private static ulong _lastSnapshotUsec;

    // From FrameLog when the GPU has been slow for a few frames in a row:
    // what's on screen right now. A few per session, 10 s apart.
    public static void Snapshot(float gpuMs)
    {
        var now = Time.GetTicksUsec();
        if (_running || _snapshots >= 6 || (_lastSnapshotUsec != 0 && now - _lastSnapshotUsec < 10_000_000))
            return;
        _snapshots++;
        _lastSnapshotUsec = now;
        try
        {
            var recent = FrameLog.ScenesAddedWithin(5);
            PerfEntry.Log(
                $"spike: GPU {gpuMs:0} ms per frame for several frames; added in the last 5 s: "
                    + string.Join(", ", recent.GroupBy(r => r.Name).Select(g => $"{g.Key} x{g.Count()}"))
            );
            var items = Walk(((SceneTree)Engine.GetMainLoop()).Root);
            Inventory(items, "spike");
            if (PerfSettings.Value("probe") == "spikes" && !_spikeRunning && _spikeRuns < MaxSpikeRuns)
                StartSpikeTests(gpuMs, items, recent);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"spike snapshot failed: {ex.Message}");
        }
    }

    // ---- slow moments (probe=spikes) ------------------------------------

    private const int SpikeSettle = 3;
    private const int SpikeMeasure = 2;
    private const int SpikeGap = 3;
    private const int MaxSpikeRuns = 5;
    private const int MaxTestsPerSpike = 6;
    private const float SpikeOverMs = 40;

    private static int _spikeRuns;
    private static bool _spikeRunning;
    private static readonly HashSet<string> SpikeTested = new();
    private static List<(CanvasItem Node, string Key, string Label)> _spikeCandidates;
    private static int _spikeIndex;
    private static int _spikeFrame;
    private static bool _spikeHidden;
    private static float _spikeBase;
    private static (string Label, float Base, float With)? _spikePending;
    private static readonly List<float> SpikeSamples = new();

    private static void StartSpikeTests(float gpu, List<Item> items, List<(Node Node, string Name)> recent)
    {
        var candidates = new List<(CanvasItem, string, string)>();
        void Add(Node node, string key)
        {
            if (node is CanvasItem ci && ci.IsVisibleInTree() && !SpikeTested.Contains(key) && candidates.All(c => c.Item2 != key))
                candidates.Add((ci, key, Label(node)));
        }
        foreach (var (node, name) in recent)
            Add(node, $"{name}:{node.GetType().Name}");
        foreach (var i in Branches(items))
            Add(items[i].Node, Label(items[i].Node));
        _spikeCandidates = candidates.Take(MaxTestsPerSpike).ToList();
        if (_spikeCandidates.Count == 0)
            return;
        _spikeRuns++;
        _spikeRunning = true;
        _spikeIndex = -1;
        _spikeFrame = SpikeGap;
        _spikeHidden = false;
        _spikePending = null;
        _spikeBase = gpu;
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += OnSpikeFrame;
        PerfEntry.Log($"spike tests ({_spikeRuns}/{MaxSpikeRuns}): hiding one at a time: {string.Join("; ", _spikeCandidates.Select(c => c.Label))}");
    }

    private static void OnSpikeFrame()
    {
        try
        {
            SpikeStep();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"spike tests failed: {ex.Message}");
            SpikeRestore();
            SpikeStop();
        }
    }

    private static void SpikeStep()
    {
        var gpu = (float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(((SceneTree)Engine.GetMainLoop()).Root.GetViewportRid());
        _spikeFrame++;
        if (!_spikeHidden)
        {
            if (_spikeFrame < SpikeGap)
                return;
            // GPU time with everything back: still slow, or the slowdown ended?
            var stillSlow = gpu >= SpikeOverMs;
            if (_spikePending is { } p)
            {
                PerfEntry.Log(
                    stillSlow
                        ? $"spike test: without {p.Label}: GPU {p.With:0.0} ms (slow frames {p.Base:0.0} ms, {p.Base - p.With:+0.0;-0.0} saved)"
                        : $"spike test: without {p.Label}: GPU {p.With:0.0} ms, but the slowdown ended meanwhile, so not conclusive"
                );
                _spikePending = null;
            }
            if (!stillSlow)
            {
                PerfEntry.Log($"spike tests: slowdown over (GPU {gpu:0.0} ms)");
                SpikeStop();
                return;
            }
            _spikeBase = gpu;
            _spikeIndex++;
            while (_spikeIndex < _spikeCandidates.Count && !GodotObject.IsInstanceValid(_spikeCandidates[_spikeIndex].Node))
                _spikeIndex++;
            if (_spikeIndex >= _spikeCandidates.Count)
            {
                SpikeStop();
                return;
            }
            RenderingServer.CanvasItemSetVisible(_spikeCandidates[_spikeIndex].Node.GetCanvasItem(), false);
            _spikeHidden = true;
            _spikeFrame = 0;
            SpikeSamples.Clear();
            return;
        }
        if (_spikeFrame > SpikeSettle)
            SpikeSamples.Add(gpu);
        if (_spikeFrame < SpikeSettle + SpikeMeasure)
            return;
        var candidate = _spikeCandidates[_spikeIndex];
        SpikeTested.Add(candidate.Key);
        _spikePending = (candidate.Label, _spikeBase, SpikeSamples.Average());
        SpikeRestore();
        _spikeFrame = 0;
    }

    private static void SpikeRestore()
    {
        if (!_spikeHidden || _spikeCandidates == null || _spikeIndex < 0 || _spikeIndex >= _spikeCandidates.Count)
            return;
        var node = _spikeCandidates[_spikeIndex].Node;
        if (GodotObject.IsInstanceValid(node))
            RenderingServer.CanvasItemSetVisible(node.GetCanvasItem(), node.Visible);
        _spikeHidden = false;
    }

    private static void SpikeStop()
    {
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= OnSpikeFrame;
        _spikeRunning = false;
        _spikeCandidates = null;
        if (_spikeRuns >= MaxSpikeRuns && PerfSettings.Value("probe") == "spikes")
        {
            PerfSettings.Set("probe", "off");
            PerfEntry.Log("spike tests: done, Scene probe is off again");
        }
    }
}
