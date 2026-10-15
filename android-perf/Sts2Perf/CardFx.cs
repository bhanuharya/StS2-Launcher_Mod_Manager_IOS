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
// bg_sway=off detaches the wind_sway shader from the background foliage, so
// it stands still instead of being reshaded every frame.
//
// All apply to what's already on screen and to new nodes as they appear.
internal static class CardFx
{
    private const string CardScene = "card.tscn";
    private const string TrailPrefix = "card_trail";
    private const string SwayShader = "wind_sway.gdshader";

    private static bool _hooked;
    private static bool _swept;
    private static bool _fastApplied;
    private static bool _trailsHidden;
    private static bool _swayOff;
    private static readonly List<CanvasItem> Sways = new();
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
        // Re-applied every tick: a CanvasGroup resets its mode when the game
        // changes one of its properties. Untouched unless on, or just turned off.
        if (fast || _fastApplied)
            foreach (var group in Groups)
                SetGroup(group, !fast);
        if (hideTrails || _trailsHidden)
            foreach (var trail in Trails)
                SetTrail(trail, !hideTrails);
        if (swayOff || _swayOff)
            foreach (var sway in Sways)
                SetSway(sway, !swayOff);
        if (fast != _fastApplied || hideTrails != _trailsHidden || swayOff != _swayOff)
            PerfEntry.Log(
                $"visuals: cards {(fast ? "fast (no offscreen pass)" : "as the game")}, card trails {(hideTrails ? "off" : "on")}, background sway {(swayOff ? "off" : "on")}"
                    + $" ({Groups.Count} card groups, {Trails.Count} trails, {Sways.Count} swaying layers now, new ones as they appear)"
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
        else if (node is CanvasItem swaying && swaying.Material is ShaderMaterial { Shader: { } shader }
                 && shader.ResourcePath.EndsWith(SwayShader, StringComparison.Ordinal))
        {
            Sways.Add(swaying);
            if (_swayOff)
                SetSway(swaying, false);
        }
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
}
