using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using STS2Mobile.Launcher;
using STS2Mobile.Patches;

namespace STS2Perf;

// "Fast" graphics: render the game at its layout resolution instead of the
// screen's.
//
// The launcher lays the game out on a 1680x1080 canvas (divided by UI scale,
// expanded to the screen's aspect; see UiScalePatches) with the canvas_items
// stretch mode, which draws that layout at full screen resolution. Switching
// the root window to the viewport stretch mode keeps the exact same layout and
// input mapping but draws it at the layout size, and Godot scales the finished
// frame up to the screen. On a 2560x1600 tablet that is 1728x1080, under half
// the pixels; on a 1080p phone at 100% UI scale it changes nothing.
//
// Fast also turns off full-screen extras the game may enable on the root
// viewport (HDR 2D, FXAA, debanding). Each change is logged with [Perf].
//
// Sharp leaves everything as upstream ships it.
internal static class RenderMode
{
    private static bool _applyQueued;
    private static Window.ContentScaleModeEnum? _stockMode;
    private static bool _effectsChecked;

    public static void Apply(Harmony harmony)
    {
        PerfEntry.Patch(
            harmony,
            AccessTools.Method(typeof(UiScalePatches), nameof(UiScalePatches.ApplyScaledContentSize)),
            "UiScalePatches.ApplyScaledContentSize",
            typeof(RenderMode),
            postfix: nameof(QueueApply)
        );

        // The launcher pins its own canvas_items layout while it's open and
        // puts the game's values back when it closes.
        PerfEntry.Patch(
            harmony,
            AccessTools.Method(typeof(LauncherUI), "OnExitTree"),
            "LauncherUI.OnExitTree",
            typeof(RenderMode),
            postfix: nameof(LauncherClosed)
        );

        // The game's own window-change handlers (the non-Auto aspect ratio
        // paths UiScalePatches leaves to the game). Type.GetType loads sts2
        // from the trusted assembly list; null in launcher-only mode.
        foreach (var name in new[]
        {
            "MegaCrit.Sts2.Core.Nodes.CommonUi.NGlobalUi, sts2",
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu, sts2",
        })
        {
            var type = Type.GetType(name, throwOnError: false);
            if (type == null)
                continue;
            PerfEntry.Patch(
                harmony,
                AccessTools.Method(type, "OnWindowChange"),
                $"{type.Name}.OnWindowChange",
                typeof(RenderMode),
                postfix: nameof(QueueApply)
            );
        }
    }

    private static void LauncherClosed() => QueueApply();

    // Deferred: these hooks run inside window resize handling, and changing
    // the stretch mode there would re-enter it.
    private static void QueueApply()
    {
        if (_applyQueued)
            return;
        _applyQueued = true;
        try
        {
            Callable.From(ApplyNow).CallDeferred();
        }
        catch (Exception ex)
        {
            _applyQueued = false;
            PerfEntry.Log($"render mode: {ex.Message}");
        }
    }

    private static void ApplyNow()
    {
        _applyQueued = false;
        if (LauncherUI.LauncherActive)
            return;
        try
        {
            if (Engine.GetMainLoop() is not SceneTree { Root: { } root })
                return;

            if (PerfSettings.FastGraphics)
            {
                if (root.ContentScaleMode == Window.ContentScaleModeEnum.CanvasItems)
                {
                    _stockMode ??= root.ContentScaleMode;
                    root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
                    var render = root.GetVisibleRect().Size;
                    var screen = DisplayServer.WindowGetSize();
                    PerfEntry.Log(
                        $"Fast graphics: rendering {render.X:0}x{render.Y:0} for a {screen.X}x{screen.Y} screen"
                    );
                }
                ReduceEffects(root);
            }
            else if (_stockMode is { } stock && root.ContentScaleMode != stock)
            {
                root.ContentScaleMode = stock;
                PerfEntry.Log("Sharp graphics: full resolution");
            }
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"render mode failed: {ex.Message}");
        }
    }

    private static void ReduceEffects(Window root)
    {
        if (_effectsChecked)
            return;
        _effectsChecked = true;

        var changed = new List<string>();
        if (root.UseHdr2D)
        {
            root.UseHdr2D = false;
            changed.Add("HDR 2D");
        }
        if (root.ScreenSpaceAA != Viewport.ScreenSpaceAAEnum.Disabled)
        {
            changed.Add($"screen-space AA ({root.ScreenSpaceAA})");
            root.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
        }
        if (root.UseDebanding)
        {
            root.UseDebanding = false;
            changed.Add("debanding");
        }
        PerfEntry.Log(
            changed.Count > 0
                ? $"Fast graphics: turned off {string.Join(", ", changed)}"
                : $"Fast graphics: no full-screen effects to turn off (MSAA 2D {root.Msaa2D})"
        );
    }
}
