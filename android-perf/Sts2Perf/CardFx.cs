using System;
using System.Collections.Generic;
using Godot;

namespace STS2Perf;

// card_fx=fast and card_trails=off: cheaper cards, the biggest GPU cost in a
// fight on a Pad 5 (Scene probe, 0.4.2.6): a hand of ~7 cards took 12.7 of
// the screen's 26 ms per frame.
//
// Each card (card.tscn) draws inside a CanvasGroup: Godot renders the card
// into an offscreen buffer and then composites it, with the card_ripple
// shader. On a tile-based mobile GPU every such pass costs about a
// full-screen read and write, per card, per frame. card_fx=fast turns the
// group off for cards (RenderingServer canvas_item_set_canvas_group_mode, the
// node itself untouched) and detaches its ripple shader, so parts that
// inherit their parent's material don't each get it: the card's parts draw
// straight to the screen, the ripple effect is gone, and a fading card's
// parts can show through each other mid-animation.
//
// card_trails=off hides the trail drawn behind cards flying to a pile
// (card_trail_*.tscn); frames ran at ~90 ms while a discarded hand flew off.
//
// bg_sway=off (Perf settings → Background animation) stills the fight's
// background:
// - the wind_sway shader comes off the foliage, so it stands still instead
//   of being reshaded every frame;
// - Underdocks' water shimmer goes: 18-20 sprites (water_reflection*) drawn
//   additively with the stepped fire shader, several of them covering half
//   the screen or more, inside 7-9 clip masks, each of which Godot draws
//   offscreen first. Those fights ran the GPU at 38-58 ms per frame at full
//   resolution on a Pad 5 (17-20 fps in a 0.4.2.18 log) against 12-14 ms for
//   Overgrowth's. The sprites are hidden, and a clip mask that held nothing
//   else draws as a plain layer again;
// - the vfx_water_reflection ripple shader comes off its water layers.
// The art itself stays; only its animation goes. Done through the
// RenderingServer, so the game's nodes keep their own settings.
//
// All apply to what's already on screen and to new nodes as they appear.
internal static class CardFx
{
    private const string CardScene = "card.tscn";
    private const string TrailPrefix = "card_trail";
    private const string SwayShader = "wind_sway.gdshader";
    private const string ShimmerShader = "vfx_stepped_shader_fire_add.tres";
    private const string RippleShader = "vfx_water_reflection.gdshader";

    private static bool _hooked;
    private static bool _swept;
    private static bool _fastApplied;
    private static bool _trailsHidden;
    private static bool _swayOff;
    private static readonly List<CanvasItem> Sways = new();
    private static readonly List<CanvasItem> Shimmers = new();
    private static readonly List<CanvasItem> ClipParents = new();
    private static readonly List<CanvasGroup> Groups = new();
    private static readonly List<CanvasItem> Trails = new();

    private static bool Wanted =>
        PerfSettings.Value("card_fx") == "fast" || PerfSettings.Value("card_trails") == "off" || PerfSettings.Value("bg_sway") == "off";

    // From PerfRuntime.ApplyAll (every 2 s and on settings changes).
    public static void Apply(SceneTree tree)
    {
        if (!Wanted && !_hooked)
            return;
        if (!_hooked)
        {
            tree.NodeAdded += OnNodeAdded;
            _hooked = true;
        }
        if (!_swept)
        {
            _swept = true;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                Track(node);
                foreach (var child in node.GetChildren())
                    stack.Push(child);
            }
        }
        var fast = PerfSettings.Value("card_fx") == "fast";
        var hideTrails = PerfSettings.Value("card_trails") == "off";
        var swayOff = PerfSettings.Value("bg_sway") == "off";
        Groups.RemoveAll(g => !GodotObject.IsInstanceValid(g));
        Trails.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        Sways.RemoveAll(s => !GodotObject.IsInstanceValid(s));
        Shimmers.RemoveAll(s => !GodotObject.IsInstanceValid(s));
        ClipParents.RemoveAll(c => !GodotObject.IsInstanceValid(c));
        // Re-applied every tick: a CanvasGroup resets its mode when the game
        // changes one of its properties. Untouched unless on, or just turned off.
        if (fast || _fastApplied)
            foreach (var group in Groups)
                SetGroup(group, !fast);
        if (hideTrails || _trailsHidden)
            foreach (var trail in Trails)
                SetTrail(trail, !hideTrails);
        if (swayOff || _swayOff)
        {
            foreach (var sway in Sways)
                SetSway(sway, !swayOff);
            foreach (var shimmer in Shimmers)
                SetShimmer(shimmer, !swayOff);
            foreach (var parent in ClipParents)
                SetClip(parent, !swayOff);
        }
        if (fast != _fastApplied || hideTrails != _trailsHidden || swayOff != _swayOff)
            PerfEntry.Log(
                $"visuals: cards {(fast ? "fast (no offscreen pass)" : "as the game")}, card trails {(hideTrails ? "off" : "on")}, background animation {(swayOff ? "off" : "on")}"
                    + $" ({Groups.Count} card groups, {Trails.Count} trails, {Sways.Count} swaying layers, {Shimmers.Count} shimmer sprites now, new ones as they appear)"
            );
        _fastApplied = fast;
        _trailsHidden = hideTrails;
        _swayOff = swayOff;
        if (!Wanted)
        {
            tree.NodeAdded -= OnNodeAdded;
            _hooked = false;
            _swept = false;
            Groups.Clear();
            Trails.Clear();
            Sways.Clear();
            Shimmers.Clear();
            ClipParents.Clear();
        }
    }

    private static void OnNodeAdded(Node node)
    {
        try
        {
            Track(node);
        }
        catch
        {
            // Never let this break the game's node setup.
        }
    }

    private static void Track(Node node)
    {
        if (node is CanvasGroup group && InCard(group))
        {
            Groups.Add(group);
            if (_fastApplied)
                SetGroup(group, false);
        }
        else if (node is CanvasItem item && SceneFile(node).StartsWith(TrailPrefix, StringComparison.Ordinal))
        {
            Trails.Add(item);
            if (_trailsHidden)
                SetTrail(item, false);
        }
        else if (node is CanvasItem animated && animated.Material is ShaderMaterial { Shader: { } shader })
        {
            var path = shader.ResourcePath;
            if (path.EndsWith(SwayShader, StringComparison.Ordinal)
                || (path.EndsWith(RippleShader, StringComparison.Ordinal) && InBackground(animated)))
            {
                Sways.Add(animated);
                if (_swayOff)
                    SetSway(animated, false);
            }
            else if (path.EndsWith(ShimmerShader, StringComparison.Ordinal) && InBackground(animated))
            {
                Shimmers.Add(animated);
                if (_swayOff)
                    SetShimmer(animated, false);
                if (animated.GetParent() is CanvasItem parent && parent.ClipChildren != CanvasItem.ClipChildrenMode.Disabled
                    && !ClipParents.Contains(parent))
                {
                    ClipParents.Add(parent);
                    // Its other children may not be in the tree yet.
                    Callable.From(() => { if (_swayOff && GodotObject.IsInstanceValid(parent)) SetClip(parent, false); }).CallDeferred();
                }
            }
        }
    }

    // Part of a fight's background scene (underdocks_background.tscn, a boss's
    // *_background.tscn).
    private static bool InBackground(Node node)
    {
        for (var n = node.GetParent(); n != null; n = n.GetParent())
            if (SceneFile(n).EndsWith("_background.tscn", StringComparison.Ordinal))
                return true;
        return false;
    }

    internal static bool InCard(Node node)
    {
        var n = node;
        for (var i = 0; i < 8 && n != null; i++, n = n.GetParent())
            if (SceneFile(n) == CardScene)
                return true;
        return false;
    }

    internal static string SceneFile(Node node)
    {
        var path = node.SceneFilePath;
        return string.IsNullOrEmpty(path) ? "" : path[(path.LastIndexOf('/') + 1)..];
    }

    internal static void SetGroup(CanvasGroup group, bool asGame)
    {
        var item = group.GetCanvasItem();
        if (asGame)
        {
            RenderingServer.CanvasItemSetCanvasGroupMode(
                item,
                RenderingServer.CanvasGroupMode.Transparent,
                group.ClearMargin,
                true,
                group.FitMargin,
                group.UseMipmaps
            );
            RenderingServer.CanvasItemSetMaterial(item, group.Material?.GetRid() ?? new Rid());
        }
        else
        {
            RenderingServer.CanvasItemSetCanvasGroupMode(item, RenderingServer.CanvasGroupMode.Disabled);
            RenderingServer.CanvasItemSetMaterial(item, new Rid());
        }
    }

    private static void SetTrail(CanvasItem trail, bool visible) =>
        RenderingServer.CanvasItemSetVisible(trail.GetCanvasItem(), visible && trail.Visible);

    private static void SetSway(CanvasItem item, bool asGame) =>
        RenderingServer.CanvasItemSetMaterial(item.GetCanvasItem(), asGame ? item.Material?.GetRid() ?? new Rid() : new Rid());

    private static void SetShimmer(CanvasItem item, bool asGame) =>
        RenderingServer.CanvasItemSetVisible(item.GetCanvasItem(), asGame && item.Visible);

    // A clip mask whose children are all shimmer: drawn as a plain layer
    // (AndDraw) or not at all (Only, a mask that isn't drawn itself).
    private static void SetClip(CanvasItem parent, bool asGame)
    {
        var item = parent.GetCanvasItem();
        var onlyShimmer = true;
        foreach (var child in parent.GetChildren())
            if (child is CanvasItem c && c.Visible && !Shimmers.Contains(c))
                onlyShimmer = false;
        if (asGame || !onlyShimmer)
        {
            RenderingServer.CanvasItemSetVisible(item, parent.Visible);
            RenderingServer.CanvasItemSetCanvasGroupMode(
                item,
                parent.ClipChildren == CanvasItem.ClipChildrenMode.Only
                    ? RenderingServer.CanvasGroupMode.ClipOnly
                    : RenderingServer.CanvasGroupMode.ClipAndDraw
            );
        }
        else if (parent.ClipChildren == CanvasItem.ClipChildrenMode.Only)
        {
            RenderingServer.CanvasItemSetVisible(item, false);
        }
        else
        {
            RenderingServer.CanvasItemSetCanvasGroupMode(item, RenderingServer.CanvasGroupMode.Disabled);
        }
    }
}
