using System;
using Godot;
using HarmonyLib;
using STS2Mobile.Launcher;
using STS2Mobile.Launcher.Components;

namespace STS2Perf;

// Adds a "Graphics: Fast / Sharp" button to the launcher's Console header,
// next to the Debug toggle (the left column is already full on phones).
// The choice is saved and applied when the game starts (see RenderMode).
internal static class GraphicsToggle
{
    public static void Apply(Harmony harmony)
    {
        PerfEntry.Patch(
            harmony,
            AccessTools.Constructor(typeof(LauncherView), new[] { typeof(Control), typeof(float) }),
            "LauncherView(Control, float)",
            typeof(GraphicsToggle),
            postfix: nameof(LauncherViewPostfix)
        );
    }

    private static string Label() =>
        PerfSettings.FastGraphics ? "Graphics: Fast" : "Graphics: Sharp";

    private static void LauncherViewPostfix(LauncherView __instance, float scale)
    {
        try
        {
            var debug = __instance.DebugButton;
            if (debug?.GetParent() is not Container header)
            {
                PerfEntry.Log("graphics button: Console header not found");
                return;
            }

            // Same look as the Debug toggle beside it.
            var button = new StyledButton(Label(), scale, fontSize: 11, height: 28);
            button.CustomMinimumSize = new Vector2((int)(130 * scale), button.CustomMinimumSize.Y);
            button.TooltipText = "Fast renders the game at a lower resolution (smoother on weaker devices). Applies when the game starts.";
            button.Pressed += () =>
            {
                PerfSettings.FastGraphics = !PerfSettings.FastGraphics;
                button.Text = Label();
                PerfEntry.Log(
                    PerfSettings.FastGraphics
                        ? "Graphics: Fast (lower resolution) — applies when the game starts"
                        : "Graphics: Sharp (full resolution) — applies when the game starts"
                );
            };
            header.AddChild(button);
            header.MoveChild(button, debug.GetIndex());
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"graphics button failed: {ex.Message}");
        }
    }
}
