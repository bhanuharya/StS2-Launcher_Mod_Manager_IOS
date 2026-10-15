using System;
using Godot;
using HarmonyLib;
using STS2Mobile.Launcher;
using STS2Mobile.Launcher.Components;

namespace STS2Perf;

// "Perf settings" button next to the launcher's Debug toggle, opening a panel
// with one row per setting; each tap moves to the next value and saves it to
// sts2perf.cfg (PerfSettings). Built from plain engine controls plus the
// launcher's own Styled* components, so it looks like the rest of the launcher.
internal static class PerfPanel
{
    private record Option(string Value, string Label);

    private record Row(string Key, string Title, Option[] Options);

    private static readonly Row[] Rows =
    {
        new("resolution", "Resolution", new Option[] { new("full", "Full"), new("reduced", "Low res (softer)") }),
        new("effects", "HDR / FXAA / debanding", new Option[] { new("on", "As the game sets"), new("off", "Off") }),
        new("max_fps", "FPS cap", new Option[]
        {
            new("0", "Game setting"), new("30", "30"), new("40", "40"), new("45", "45"),
            new("60", "60"), new("90", "90"), new("120", "120"),
        }),
        new("vsync", "VSync", new Option[]
        {
            new("game", "Game setting"), new("on", "On"), new("off", "Off"),
            new("adaptive", "Adaptive"), new("mailbox", "Mailbox"),
        }),
        new("particles", "Particles", new Option[]
        {
            new("100", "100%"), new("75", "75%"), new("50", "50%"), new("25", "25%"), new("0", "Off"),
        }),
        new("frame_log", "Frame-time log", new Option[]
        {
            new("0", "Off"), new("5", "Every 5 s"), new("10", "Every 10 s"), new("30", "Every 30 s"),
        }),
        new("jit_warmup", "JIT warm-up (experimental)", new Option[] { new("off", "Off"), new("on", "At main menu") }),
        new("probe", "Scene probe (once)", new Option[] { new("off", "Off"), new("next_fight", "Next fight") }),
    };

    public static void Apply(Harmony harmony)
    {
        PerfEntry.Patch(
            harmony,
            AccessTools.Constructor(typeof(LauncherView), new[] { typeof(Control), typeof(float) }),
            "LauncherView(Control, float)",
            typeof(PerfPanel),
            postfix: nameof(LauncherViewPostfix)
        );
    }

    private static void LauncherViewPostfix(LauncherView __instance, Control parent, float scale)
    {
        PerfRuntime.EnsureStarted();
        try
        {
            var debug = __instance.DebugButton;
            if (debug?.GetParent() is not Container header)
            {
                PerfEntry.Log("perf settings button: Console header not found");
                return;
            }
            // Same look as the Debug toggle beside it.
            var button = new StyledButton("Perf settings", scale, fontSize: 11, height: 28);
            button.CustomMinimumSize = new Vector2((int)(130 * scale), button.CustomMinimumSize.Y);
            button.Pressed += () => Show(parent, scale);
            header.AddChild(button);
            header.MoveChild(button, debug.GetIndex());
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"perf settings button failed: {ex.Message}");
        }
    }

    private static void Show(Control parent, float scale)
    {
        try
        {
            var overlay = new ColorRect { Color = new Color(0, 0, 0, 0.6f) };
            overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            ModalGate.Register(overlay); // Android Back closes it

            var center = new CenterContainer();
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            overlay.AddChild(center);

            var box = new PanelContainer();
            var style = new StyleBoxFlat { BgColor = Ui.SurfaceHigh };
            style.SetCornerRadiusAll((int)(Ui.RadiusL * scale));
            style.SetContentMarginAll((int)(24 * scale));
            box.AddThemeStyleboxOverride("panel", style);
            center.AddChild(box);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", (int)(12 * scale));
            box.AddChild(vbox);

            vbox.AddChild(new StyledLabel("Performance settings", scale, fontSize: 20));
            var note = new StyledLabel(
                "Saved to sts2perf.cfg and applied when the game starts. "
                    + "Edits to that file while playing apply within 2 seconds.",
                scale,
                fontSize: 12
            );
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.CustomMinimumSize = new Vector2((int)(480 * scale), 0);
            note.AddThemeColorOverride("font_color", Ui.TextSecondary);
            vbox.AddChild(note);

            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            vbox.AddChild(scroll);
            TouchScroll.Attach(scroll);

            var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            rows.AddThemeConstantOverride("separation", (int)(8 * scale));
            scroll.AddChild(rows);
            foreach (var row in Rows)
                AddRow(rows, row, scale);

            var closeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            var close = new StyledButton("Close", scale, fontSize: 14, height: 52, variant: ButtonVariant.Primary);
            close.CustomMinimumSize = new Vector2((int)(160 * scale), close.CustomMinimumSize.Y);
            close.Pressed += overlay.QueueFree;
            closeRow.AddChild(close);
            vbox.AddChild(closeRow);

            // Size the list after layout, capped so the Close button stays on screen.
            Callable
                .From(() =>
                {
                    if (!GodotObject.IsInstanceValid(scroll))
                        return;
                    var vpH = scroll.GetViewport()?.GetVisibleRect().Size.Y ?? 1080f;
                    var natural = rows.GetCombinedMinimumSize().Y;
                    scroll.CustomMinimumSize = new Vector2((int)(480 * scale), Mathf.Min(natural, vpH * 0.55f));
                })
                .CallDeferred();

            parent.AddChild(overlay);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"perf settings panel failed: {ex.Message}");
        }
    }

    private static void AddRow(VBoxContainer rows, Row row, float scale)
    {
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", (int)(12 * scale));

        var title = new StyledLabel(row.Title, scale, fontSize: 14, align: HorizontalAlignment.Left)
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.AddChild(title);

        var button = new StyledButton(CurrentLabel(row), scale, fontSize: 14, height: 44);
        button.CustomMinimumSize = new Vector2((int)(190 * scale), button.CustomMinimumSize.Y);
        button.Pressed += () =>
        {
            var next = row.Options[(CurrentIndex(row) + 1) % row.Options.Length];
            PerfSettings.Set(row.Key, next.Value);
            button.Text = next.Label;
            PerfEntry.Log($"{row.Title}: {next.Label}");
        };
        line.AddChild(button);
        rows.AddChild(line);
    }

    // -1 for a value set by hand in the file; the next tap starts the list over.
    private static int CurrentIndex(Row row) =>
        Array.FindIndex(row.Options, o => o.Value == PerfSettings.Value(row.Key));

    private static string CurrentLabel(Row row)
    {
        var i = CurrentIndex(row);
        return i >= 0 ? row.Options[i].Label : PerfSettings.Value(row.Key);
    }
}
