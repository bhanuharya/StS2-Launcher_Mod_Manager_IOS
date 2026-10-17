using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Perf;

// Some of the game's effects have particles that bounce off things: the slime
// splash every slime's attack plays on the player (vfx_slime_impact, also the
// Foul Potion's), likely the coins in treasure rooms. GPU particles collide
// against the screen's 2D signed distance field, and for every frame one such emitter
// is drawn, the engine rebuilds that field from scratch first
// (RendererViewport::_draw_viewport → render_sdf + render_target_sdf_process),
// even when the emitter has no particles out. With Godot's defaults the field
// covers the screen plus 20% on every side, 3584x2240 on a 2560x1600 screen,
// worked on at half that resolution with eleven jump-flood compute passes. On
// a Pad 5 every frame took ~97 ms on the GPU (10 fps) for 1-6 s after each
// slime move, and the field's buffers showed up as exactly +26.8 MB of texture
// memory on the first splash of a session. Godot 4.5.1 on a desktop: one
// colliding emitter with no particles out, 28.6 → 286 ms per frame.
//
// The field is built from the LightOccluder2D nodes on screen. With none, it's
// "nothing anywhere" and no particle ever collides. collision_map=light
// (Perf settings → Particle collisions):
// - while no occluder is in the scene, or particles are at 0% (nothing is
//   emitted, but the emitters are still drawn, which is what asks for the
//   field), each new emitter that collides gets a copy of its
//   ParticleProcessMaterial with collision off: the same motion, and no
//   field to rebuild. If an occluder shows up while particles are on, they
//   get theirs back. The slime splash brings its own occluder (a floor for
//   the goo, it seems): in a 0.4.2.17 log every splash's emitters were
//   switched back as it appeared, so with particles on it gets the light
//   field below, ~20 ms GPU frames instead of ~97 ms.
// - the root viewport keeps the field, for whatever still needs it, at a
//   quarter of the screen's resolution with no margin (Viewport.sdf_scale 25%,
//   sdf_oversize 100%): an eighth of the pixels for every pass (the desktop
//   test above: 286 → 77 ms).
// game puts back what the game had.
//
// With the frame log on, frames in which an emitter that needs the field is
// drawn are counted, and hitches list them:
//
//   [Perf] frames 10s: ... | collision map 64 frames, light (vfx_slime_impact)
//   [Perf] hitch 98 ms: collision map (vfx_slime_impact x2); ...
internal static class CollisionMap
{
    private const string OriginalMeta = "sts2perf_collision_material";

    private static (Viewport.SdfOversizeEnum Oversize, Viewport.SdfScaleEnum Scale)? _stock;
    private static bool _light;
    private static bool _particlesOff;
    private static bool _tracking;
    private static bool _frameHooked;
    private static readonly List<GpuParticles2D> Emitters = new();
    private static readonly List<LightOccluder2D> Occluders = new();
    private static readonly Dictionary<ulong, bool> ShaderCollides = new();
    private static readonly Dictionary<ulong, ParticleProcessMaterial> NoCollision = new();
    private static readonly HashSet<string> SkipLogged = new();
    private static readonly Dictionary<string, int> FrameScenes = new();
    private static readonly HashSet<string> WindowScenes = new();
    private static int _windowFrames;
    private static int _windowSkipped;

    // From PerfRuntime.ApplyAll (every 2 s and on settings changes).
    public static void Apply(Window root)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        var light = PerfSettings.Value("collision_map") == "light";
        if (light)
        {
            if (root.SdfOversize != Viewport.SdfOversizeEnum.Oversize100Percent
                || root.SdfScale != Viewport.SdfScaleEnum.Scale25Percent)
            {
                _stock ??= (root.SdfOversize, root.SdfScale);
                root.SdfOversize = Viewport.SdfOversizeEnum.Oversize100Percent;
                root.SdfScale = Viewport.SdfScaleEnum.Scale25Percent;
                PerfEntry.Log(
                    "particle collisions: light (off while there's nothing to collide with; "
                        + $"map at 25% of the screen, no margin; game had {Describe(_stock.Value.Oversize, _stock.Value.Scale)})"
                );
            }
        }
        else if (_stock is { } stock && (root.SdfOversize, root.SdfScale) != stock)
        {
            (root.SdfOversize, root.SdfScale) = stock;
            PerfEntry.Log($"particle collisions: as the game does ({Describe(stock.Oversize, stock.Scale)})");
        }
        var particlesOff = PerfSettings.ParticlesPercent == 0;
        if (_light && (!light || (_particlesOff && !particlesOff && AnyOccluder())))
            RestoreAll();
        var skipAll = light && particlesOff && !(_light && _particlesOff);
        _light = light;
        _particlesOff = particlesOff;

        var track = light || PerfSettings.FrameLogSeconds > 0;
        if (track && !_tracking)
        {
            _tracking = true;
            tree.NodeAdded += OnNodeAdded;
            foreach (var node in tree.Root.FindChildren("*", "LightOccluder2D", true, false))
                OnNodeAdded(node);
            foreach (var node in tree.Root.FindChildren("*", "GPUParticles2D", true, false))
                OnNodeAdded(node);
        }
        else if (skipAll)
        {
            // Particles just went to 0%: the emitters already here, too.
            foreach (var gpu in Emitters)
                if (GodotObject.IsInstanceValid(gpu) && !gpu.HasMeta(OriginalMeta))
                    Skip(gpu);
        }
        else if (!track && _tracking)
        {
            _tracking = false;
            tree.NodeAdded -= OnNodeAdded;
            Emitters.Clear();
            Occluders.Clear();
        }

        var count = PerfSettings.FrameLogSeconds > 0;
        if (count && !_frameHooked)
            tree.ProcessFrame += OnFrame;
        else if (!count && _frameHooked)
            tree.ProcessFrame -= OnFrame;
        if (count != _frameHooked)
        {
            _frameHooked = count;
            FrameScenes.Clear();
            WindowScenes.Clear();
            _windowFrames = 0;
            _windowSkipped = 0;
        }
    }

    private static string Describe(Viewport.SdfOversizeEnum oversize, Viewport.SdfScaleEnum scale)
    {
        var area = oversize switch
        {
            Viewport.SdfOversizeEnum.Oversize120Percent => "screen + 20% each side",
            Viewport.SdfOversizeEnum.Oversize150Percent => "screen + 50% each side",
            Viewport.SdfOversizeEnum.Oversize200Percent => "screen + 100% each side",
            _ => "screen",
        };
        var res = scale switch
        {
            Viewport.SdfScaleEnum.Scale100Percent => "100%",
            Viewport.SdfScaleEnum.Scale50Percent => "50%",
            _ => "25%",
        };
        return $"{area} at {res}";
    }

    private static void OnNodeAdded(Node node)
    {
        try
        {
            switch (node)
            {
                case GpuParticles2D gpu when Collides(gpu.ProcessMaterial):
                    if (Emitters.Count >= 32)
                        Emitters.RemoveAll(e => !GodotObject.IsInstanceValid(e) || !e.IsInsideTree());
                    if (!Emitters.Contains(gpu))
                        Emitters.Add(gpu);
                    if (_light && (_particlesOff || !AnyOccluder()))
                        Skip(gpu);
                    break;
                case LightOccluder2D occluder:
                    Occluders.Add(occluder);
                    if (occluder.SdfCollision && !_particlesOff)
                        RestoreAll();
                    break;
            }
        }
        catch
        {
            // Never let this break the game's node setup.
        }
    }

    private static bool Collides(Material material)
    {
        switch (material)
        {
            case ParticleProcessMaterial process:
                return process.CollisionMode != ParticleProcessMaterial.CollisionModeEnum.Disabled;
            case ShaderMaterial { Shader: { } shader }:
                var id = shader.GetInstanceId();
                if (!ShaderCollides.TryGetValue(id, out var collides))
                    ShaderCollides[id] = collides = shader.Code.Contains("COLLISION");
                return collides;
            default:
                return false;
        }
    }

    private static bool AnyOccluder()
    {
        Occluders.RemoveAll(o => !GodotObject.IsInstanceValid(o) || !o.IsInsideTree());
        return Occluders.Any(o => o.SdfCollision);
    }

    // Collision off on this emitter: a copy of its process material (shared
    // by every instance of the effect) with collision disabled, made once.
    private static void Skip(GpuParticles2D gpu)
    {
        if (gpu.ProcessMaterial is not ParticleProcessMaterial original)
            return;
        var id = original.GetInstanceId();
        if (!NoCollision.TryGetValue(id, out var copy) || !GodotObject.IsInstanceValid(copy))
        {
            copy = (ParticleProcessMaterial)original.Duplicate();
            copy.CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Disabled;
            NoCollision[id] = copy;
        }
        gpu.SetMeta(OriginalMeta, original);
        gpu.ProcessMaterial = copy;
        _windowSkipped++;
        var scene = SceneName(gpu);
        if (SkipLogged.Add(scene))
            PerfEntry.Log(
                _particlesOff
                    ? $"particle collisions: particles are off, so {scene}'s emitters skip collision"
                    : $"particle collisions: {scene} has nothing to collide with here, its particles skip collision"
            );
    }

    private static void RestoreAll()
    {
        var restored = 0;
        foreach (var gpu in Emitters)
        {
            if (!GodotObject.IsInstanceValid(gpu) || !gpu.HasMeta(OriginalMeta))
                continue;
            gpu.ProcessMaterial = gpu.GetMeta(OriginalMeta).As<ParticleProcessMaterial>();
            gpu.RemoveMeta(OriginalMeta);
            restored++;
        }
        if (restored > 0)
            PerfEntry.Log($"particle collisions: {restored} emitters collide again");
    }

    private static void OnFrame()
    {
        FrameScenes.Clear();
        for (var i = Emitters.Count - 1; i >= 0; i--)
        {
            var gpu = Emitters[i];
            if (!GodotObject.IsInstanceValid(gpu))
            {
                Emitters.RemoveAt(i);
                continue;
            }
            if (!gpu.IsInsideTree())
            {
                // Out of the tree: forget it, it's checked again if it comes back.
                Emitters.RemoveAt(i);
                continue;
            }
            if (!gpu.IsVisibleInTree() || gpu.HasMeta(OriginalMeta))
                continue;
            var scene = SceneName(gpu);
            FrameScenes[scene] = FrameScenes.GetValueOrDefault(scene) + 1;
        }
        if (FrameScenes.Count == 0)
            return;
        _windowFrames++;
        WindowScenes.UnionWith(FrameScenes.Keys);
    }

    // The outermost effect scene the emitter is part of, e.g. vfx_slime_impact
    // rather than the vfx_goopy_impact_bits inside it.
    private static string SceneName(Node node)
    {
        string name = null;
        for (var n = node; n != null; n = n.GetParent())
        {
            var path = n.SceneFilePath;
            if (string.IsNullOrEmpty(path))
                continue;
            if (name != null && !path.Contains("vfx"))
                break;
            name = path.GetFile().GetBaseName();
        }
        return name ?? node.Name.ToString();
    }

    // For FrameLog's hitch lines: emitters drawn this frame that need the map.
    public static string FrameSummary() =>
        FrameScenes.Count > 0
            ? $"collision map ({string.Join(", ", FrameScenes.Select(kv => $"{kv.Key} x{kv.Value}"))})"
            : null;

    // For FrameLog's stats line; starts a new window.
    public static string TakeWindowSummary()
    {
        if (!_frameHooked || (_windowFrames == 0 && _windowSkipped == 0))
            return "";
        var text = _windowFrames > 0
            ? $" | collision map {_windowFrames} frames, {(_light ? "light" : "game size")} ({string.Join(", ", WindowScenes.Take(4))})"
            : " | collision map 0 frames";
        if (_windowSkipped > 0)
            text += $", collision skipped for {_windowSkipped} emitters";
        _windowFrames = 0;
        _windowSkipped = 0;
        WindowScenes.Clear();
        return text;
    }
}
