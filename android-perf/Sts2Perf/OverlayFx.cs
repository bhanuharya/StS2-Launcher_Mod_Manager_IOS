using Godot;

namespace STS2Perf;

// idle_overlay=skip: the game keeps a full-screen transition rect on top of
// everything (Game/GameTransitionRect, NTransition, fade_transition shader).
// Between transitions its threshold is 0 and it shows nothing, but the GPU
// still shades every pixel of it: 2.2 ms per frame at 2560x1600 on a Pad 5
// (Scene probe, 0.4.2.6). It's hidden while the threshold is 0, checked every
// frame, so a transition shows from its first frame on.
internal static class OverlayFx
{
    private static CanvasItem _rect;
    private static bool _hooked;

    // From PerfRuntime.ApplyAll (every 2 s and on settings changes).
    public static void Apply(SceneTree tree)
    {
        var skip = PerfSettings.Value("idle_overlay") == "skip";
        if (skip && (_rect == null || !GodotObject.IsInstanceValid(_rect)))
            _rect = tree.Root.FindChild("GameTransitionRect", true, false) as CanvasItem;
        if (skip && !_hooked)
        {
            tree.ProcessFrame += OnFrame;
            _hooked = true;
            PerfEntry.Log($"idle transition layer: skipped while idle ({(_rect != null ? "found" : "not found yet")})");
        }
        else if (!skip && _hooked)
        {
            tree.ProcessFrame -= OnFrame;
            _hooked = false;
            if (_rect != null && GodotObject.IsInstanceValid(_rect))
                RenderingServer.CanvasItemSetVisible(_rect.GetCanvasItem(), _rect.Visible);
            PerfEntry.Log("idle transition layer: drawn as the game does");
        }
    }

    private static void OnFrame()
    {
        if (_rect == null || !GodotObject.IsInstanceValid(_rect) || _rect.Material is not ShaderMaterial material)
            return;
        var threshold = material.GetShaderParameter("threshold");
        if (threshold.VariantType is not (Variant.Type.Float or Variant.Type.Int))
            return;
        // Every frame: the game's own visibility changes reset the server state.
        RenderingServer.CanvasItemSetVisible(_rect.GetCanvasItem(), _rect.Visible && threshold.AsSingle() > 0.0001f);
    }
}
