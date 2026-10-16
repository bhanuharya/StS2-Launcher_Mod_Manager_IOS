using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using HarmonyLib;
using STS2Mobile.Launcher;
using STS2Mobile.Patches;

namespace STS2Perf;

// Applies PerfSettings to the running game and keeps it applied:
//
//  - when the launcher hands over to the game, and when the game handles a
//    window change;
//  - every 2 seconds, re-reading sts2perf.cfg if it changed (so a script can
//    switch modes mid-game) and re-applying overrides the game reset (its own
//    FPS limit / vsync settings).
//
// Every setting remembers the game's own value the first time it overrides
// it, and puts that back when the setting returns to its default.
//
// Reduced resolution: the launcher lays the game out on a 1680x1080 canvas
// (divided by UI scale, expanded to the screen's aspect; see UiScalePatches)
// with the canvas_items stretch mode, which draws that layout at full screen
// resolution. The viewport stretch mode keeps the same layout and input
// mapping but draws it at the layout size and scales the frame up. On a Pad 5
// that measured worse than Android's Game Mode downscale (Godot still scales
// and presents at full size) and looks softer, so it's opt-in.
internal static class PerfRuntime
{
    private const double TickSeconds = 2.0;
    private const string ParticleRatioMeta = "sts2perf_ratio";
    private const string ParticleAmountMeta = "sts2perf_amount";

    private static bool _started;
    private static bool _applyQueued;
    private static bool _gameUiSeen;

    private static Window.ContentScaleModeEnum? _stockScaleMode;
    private static (bool Hdr, Viewport.ScreenSpaceAAEnum Aa, bool Deband)? _stockEffects;
    private static int? _stockMaxFps;
    private static DisplayServer.VSyncMode? _stockVsync;
    private static readonly Dictionary<string, Variant> StockProperties = new();
    private static int _particlesApplied = 100;
    private static bool _nodeAddedHooked;
    private static readonly HashSet<string> Logged = new();

    public static void Apply(Harmony harmony)
    {
        PerfEntry.Patch(
            harmony,
            AccessTools.Method(typeof(UiScalePatches), nameof(UiScalePatches.ApplyScaledContentSize)),
            "UiScalePatches.ApplyScaledContentSize",
            typeof(PerfRuntime),
            postfix: nameof(OnGameWindowChange)
        );
        PerfEntry.Patch(
            harmony,
            AccessTools.Method(typeof(LauncherUI), "OnExitTree"),
            "LauncherUI.OnExitTree",
            typeof(PerfRuntime),
            postfix: nameof(QueueApply)
        );

        // The game's window-change handlers (the non-Auto aspect ratio paths
        // UiScalePatches leaves to the game). Type.GetType loads sts2 from the
        // trusted assembly list; null in launcher-only mode.
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
                typeof(PerfRuntime),
                postfix: nameof(OnGameWindowChange)
            );
        }
    }

    // From the launcher UI's construction: Godot is up by then.
    public static void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;
        PerfSettings.Reload();
        PerfEntry.Log($"settings: {PerfSettings.Summary()}");
        FrameLog.Configure();
        JitEvents.Configure();
        ScheduleTick();
    }

    // The stretch mode only changes once the game's own window handlers have
    // run: a resize during the PLAY handoff makes NGame.OnWindowChange throw
    // (see upstream's GameInputSuppressPatches).
    private static void OnGameWindowChange()
    {
        _gameUiSeen = true;
        QueueApply();
    }

    // Deferred: the hooks run inside window resize handling, and changing the
    // stretch mode there would re-enter it.
    private static void QueueApply()
    {
        if (_applyQueued)
            return;
        _applyQueued = true;
        try
        {
            Callable.From(ApplyAll).CallDeferred();
        }
        catch (Exception ex)
        {
            _applyQueued = false;
            PerfEntry.Log($"apply: {ex.Message}");
        }
    }

    private static void ScheduleTick()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
                tree.CreateTimer(TickSeconds, processAlways: true).Timeout += Tick;
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"settings watcher stopped: {ex.Message}");
        }
    }

    private static void Tick()
    {
        try
        {
            if (PerfSettings.Reload())
            {
                PerfEntry.Log($"settings changed: {PerfSettings.Summary()}");
                FrameLog.Configure();
                JitEvents.Configure();
            }
            ApplyAll();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"settings tick failed: {ex.Message}");
        }
        ScheduleTick();
    }

    private static void ApplyAll()
    {
        _applyQueued = false;
        // The launcher pins its own layout while it's open.
        if (LauncherUI.LauncherActive || Engine.GetMainLoop() is not SceneTree { Root: { } root } tree)
            return;
        Run("effects", () => ApplyEffects(root));
        if (_gameUiSeen)
            Run("resolution", () => ApplyResolution(root));
        Run("fps cap", ApplyMaxFps);
        Run("vsync", ApplyVsync);
        Run("particles", () => ApplyParticles(tree));
        Run("cards", () => CardFx.Apply(tree));
        Run("idle overlay", () => OverlayFx.Apply(tree));
        Run("asset cache", AssetKeep.Apply);
        Run("scene timing", SceneTiming.Apply);
        Run("properties", () => ApplyProperties(root));
        if (_gameUiSeen)
        {
            Run("jit warm-up", JitWarmup.MaybeStart);
            Run("scene probe", SceneProbe.Tick);
            Run("cpu breakdown", CpuProbe.Apply);
            Run("fight background", SceneProbe.LogBackground);
            Run("combat ui", CombatUiRefresh.Apply);
            Run("async timing", AsyncTiming.Apply);
            Run("export", GameExport.MaybeStart);
        }
    }

    private static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogOnce($"fail:{what}:{ex.Message}", $"{what} failed: {ex.Message}");
        }
    }

    private static void ApplyResolution(Window root)
    {
        if (PerfSettings.ReducedResolution)
        {
            if (root.ContentScaleMode != Window.ContentScaleModeEnum.CanvasItems)
                return;
            _stockScaleMode ??= root.ContentScaleMode;
            root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
            var render = root.GetVisibleRect().Size;
            var screen = DisplayServer.WindowGetSize();
            PerfEntry.Log($"low res: rendering {render.X:0}x{render.Y:0} for a {screen.X}x{screen.Y} screen");
        }
        else if (_stockScaleMode is { } stock && root.ContentScaleMode != stock)
        {
            root.ContentScaleMode = stock;
            PerfEntry.Log("full resolution");
        }
    }

    private static void ApplyEffects(Window root)
    {
        if (PerfSettings.EffectsOff)
        {
            if (!root.UseHdr2D && root.ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Disabled && !root.UseDebanding)
            {
                if (_stockEffects == null)
                    LogOnce("fx-none", $"effects: the game uses none of them (msaa 2d {root.Msaa2D})");
                return;
            }
            _stockEffects ??= (root.UseHdr2D, root.ScreenSpaceAA, root.UseDebanding);
            PerfEntry.Log(
                $"effects off (were hdr2d {OnOff(root.UseHdr2D)}, aa {root.ScreenSpaceAA}, debanding {OnOff(root.UseDebanding)})"
            );
            root.UseHdr2D = false;
            root.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
            root.UseDebanding = false;
        }
        else if (_stockEffects is { } stock
            && (root.UseHdr2D, root.ScreenSpaceAA, root.UseDebanding) != stock)
        {
            (root.UseHdr2D, root.ScreenSpaceAA, root.UseDebanding) = stock;
            PerfEntry.Log("effects back on");
        }
    }

    private static void ApplyMaxFps()
    {
        var want = PerfSettings.MaxFps;
        var now = Engine.MaxFps;
        if (want > 0)
        {
            // The game drops the cap below 20 while backgrounded; leave that alone.
            if (now == want || (now > 0 && now < 20))
                return;
            _stockMaxFps ??= now;
            Engine.MaxFps = want;
            PerfEntry.Log($"fps cap {want} (game had {(now == 0 ? "unlimited" : now.ToString())})");
        }
        else if (_stockMaxFps is { } stock)
        {
            _stockMaxFps = null;
            if (now != stock && !(now > 0 && now < 20))
                Engine.MaxFps = stock;
            PerfEntry.Log("fps cap back to the game's setting");
        }
    }

    private static void ApplyVsync()
    {
        DisplayServer.VSyncMode? want = PerfSettings.Vsync switch
        {
            "on" => DisplayServer.VSyncMode.Enabled,
            "off" => DisplayServer.VSyncMode.Disabled,
            "adaptive" => DisplayServer.VSyncMode.Adaptive,
            "mailbox" => DisplayServer.VSyncMode.Mailbox,
            _ => null,
        };
        var now = DisplayServer.WindowGetVsyncMode();
        if (want is { } mode)
        {
            if (now == mode)
                return;
            _stockVsync ??= now;
            DisplayServer.WindowSetVsyncMode(mode);
            PerfEntry.Log($"vsync {mode} (game had {now})");
        }
        else if (_stockVsync is { } stock)
        {
            _stockVsync = null;
            if (now != stock)
                DisplayServer.WindowSetVsyncMode(stock);
            PerfEntry.Log("vsync back to the game's setting");
        }
    }

    private static void ApplyParticles(SceneTree tree)
    {
        var percent = PerfSettings.ParticlesPercent;
        if (percent == _particlesApplied)
            return;
        _particlesApplied = percent;

        var count = 0;
        foreach (var node in tree.Root.FindChildren("*", "GPUParticles2D", true, false))
            count += ScaleParticles(node) ? 1 : 0;
        foreach (var node in tree.Root.FindChildren("*", "CPUParticles2D", true, false))
            count += ScaleParticles(node) ? 1 : 0;
        PerfEntry.Log($"particles {percent}% ({count} emitters now, new ones as they appear)");

        // New emitters get scaled as they enter the tree.
        if (percent < 100 && !_nodeAddedHooked)
        {
            tree.NodeAdded += OnNodeAdded;
            _nodeAddedHooked = true;
        }
        else if (percent == 100 && _nodeAddedHooked)
        {
            tree.NodeAdded -= OnNodeAdded;
            _nodeAddedHooked = false;
        }
    }

    private static void OnNodeAdded(Node node)
    {
        try
        {
            ScaleParticles(node);
        }
        catch
        {
            // Never let a particle tweak break the game's node setup.
        }
    }

    private static bool ScaleParticles(Node node)
    {
        var factor = _particlesApplied / 100f;
        switch (node)
        {
            case GpuParticles2D gpu:
                if (!gpu.HasMeta(ParticleRatioMeta))
                    gpu.SetMeta(ParticleRatioMeta, gpu.AmountRatio);
                gpu.AmountRatio = (float)gpu.GetMeta(ParticleRatioMeta) * factor;
                return true;
            case CpuParticles2D cpu:
                if (!cpu.HasMeta(ParticleAmountMeta))
                    cpu.SetMeta(ParticleAmountMeta, cpu.Amount);
                var amount = Math.Max(1, (int)Math.Round((int)cpu.GetMeta(ParticleAmountMeta) * factor));
                if (cpu.Amount != amount)
                    cpu.Amount = amount;
                return true;
            default:
                return false;
        }
    }

    private static void ApplyProperties(Window root)
    {
        var wanted = new HashSet<string>();
        foreach (var (target, property, value) in PerfSettings.PropertyOverrides)
        {
            var key = $"{target}.{property}";
            wanted.Add(key);
            var obj = target == "root" ? (GodotObject)root : Engine.Singleton;
            var current = obj.Get(property);
            if (!TryConvert(value, current, out var desired))
            {
                LogOnce($"bad:{key}={value}", $"{key}: can't set '{value}' (property is {current.VariantType})");
                continue;
            }
            if (Same(current, desired))
                continue;
            if (!StockProperties.ContainsKey(key))
                StockProperties[key] = current;
            obj.Set(property, desired);
            PerfEntry.Log($"{key} = {desired} (was {current})");
        }

        // Overrides removed from the file go back to the game's value.
        foreach (var key in new List<string>(StockProperties.Keys))
        {
            if (wanted.Contains(key))
                continue;
            var dot = key.IndexOf('.');
            var obj = key[..dot] == "root" ? (GodotObject)root : Engine.Singleton;
            obj.Set(key[(dot + 1)..], StockProperties[key]);
            StockProperties.Remove(key);
            PerfEntry.Log($"{key} back to {obj.Get(key[(dot + 1)..])}");
        }
    }

    // Parses a setting for a property of the current value's type. Enums are
    // plain ints in Godot, so "1" works for e.g. root.msaa_2d.
    private static bool TryConvert(string text, Variant current, out Variant result)
    {
        var inv = CultureInfo.InvariantCulture;
        result = default;
        switch (current.VariantType)
        {
            case Variant.Type.Bool:
                if (text is "1" or "true" or "on" or "True")
                    result = true;
                else if (text is "0" or "false" or "off" or "False")
                    result = false;
                else
                    return false;
                return true;
            case Variant.Type.Int:
                if (!long.TryParse(text, NumberStyles.Integer, inv, out var l))
                    return false;
                result = l;
                return true;
            case Variant.Type.Float:
                if (!double.TryParse(text, NumberStyles.Float, inv, out var d))
                    return false;
                result = d;
                return true;
            case Variant.Type.String:
            case Variant.Type.StringName:
                result = text;
                return true;
            default:
                return false;
        }
    }

    private static bool Same(Variant a, Variant b) =>
        a.VariantType switch
        {
            Variant.Type.Bool => a.AsBool() == b.AsBool(),
            Variant.Type.Int => a.AsInt64() == b.AsInt64(),
            Variant.Type.Float => Math.Abs(a.AsDouble() - b.AsDouble()) < 1e-9,
            _ => a.AsString() == b.AsString(),
        };

    private static string OnOff(bool b) => b ? "on" : "off";

    private static void LogOnce(string key, string message)
    {
        if (Logged.Add(key))
            PerfEntry.Log(message);
    }
}
