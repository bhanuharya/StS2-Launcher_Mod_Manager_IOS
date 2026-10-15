using Godot;

namespace STS2Perf;

// idle_overlay=skip: the game keeps a full-screen transition rect on top of
// everything (Game/GameTransitionRect, NTransition, fade_transition shader).
// Between transitions it shows nothing, but the GPU still shades every pixel
// of it: 2.2 ms per frame at 2560x1600 on a Pad 5 (Scene probe, 0.4.2.6).
//
// It's hidden only while nothing in it is showing: its shader's threshold is
// 0 and none of its children (SimpleTransition, a ColorRect fade to black
// used between rooms; GradientTransition) is visible with any opacity, for
// IdleFrames frames in a row; it's shown again on the first frame anything
// in it is. 0.4.2.8 looked at the threshold alone and hid the fade between
// rooms, so the previous room showed while the next one loaded.
internal static class OverlayFx
{
    private const int IdleFrames = 30;

    private static CanvasItem _rect;
    private static bool _hooked;
    private static int _idleFor;

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
            _idleFor = 0;
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
        if (_rect == null || !GodotObject.IsInstanceValid(_rect))
            return;
        _idleFor = Idle(_rect) ? _idleFor + 1 : 0;
        // Every frame: the game's own visibility changes reset the server state.
        RenderingServer.CanvasItemSetVisible(_rect.GetCanvasItem(), _rect.Visible && _idleFor < IdleFrames);
    }

    private static bool Idle(CanvasItem rect)
    {
        if (rect.Material is not ShaderMaterial material)
            return false;
        var threshold = material.GetShaderParameter("threshold");
        if (threshold.VariantType is not (Variant.Type.Float or Variant.Type.Int) || threshold.AsSingle() > 0.0001f)
            return false;
        foreach (var child in rect.GetChildren())
        {
            if (child is not CanvasItem item || !item.Visible)
                continue;
            var alpha = item.Modulate.A * item.SelfModulate.A * (item is ColorRect colorRect ? colorRect.Color.A : 1f);
            if (alpha > 0.01f)
                return false;
        }
        return true;
    }
}
