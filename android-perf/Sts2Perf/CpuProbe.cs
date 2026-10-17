using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// cpu_probe=on (Perf settings → CPU breakdown): which of the game's scripts
// the CPU's per-frame time goes to.
//
// The 0.4.2.10 logs from a Pad 5 showed a big shiv turn is CPU-bound: the
// game's logic took 25-39 ms per frame (frame log "logic") for minutes while
// ~350 cards were created and exhausted, with the GPU at 18-23 ms. This times
// every per-frame callback of the game's node scripts (_Process,
// _PhysicsProcess, _Draw) with a Harmony prefix and postfix, per method,
// including whatever each one calls, and the frame log's stats line gets the
// biggest ones:
//
//   | scripts 21.3 ms/frame: NHandCardHolder._Process 6.10 ms x12, ...
//
// "x12" is calls per frame (instances). The same methods' garbage (bytes the
// .NET code allocated in them, which is what makes Mono collect; GcTuning)
// follows, per frame:
//
//   | garbage 21 KB/frame: RichTextSine._ProcessCustomFX 4.1 KB, ...
//
// Logic time not covered by these is
// the engine's own work (tweens, animations, layout) and signal handlers.
// Hitch lines get the slow frame's own biggest ones:
//
//   [Perf] hitch 64 ms: scripts 31 ms (NCardFlyVfx._Process 22.0 ms, ...); ...
//
// Patching takes a moment, once, when the game is up with this on; timing
// costs about a microsecond per call. Turning it off removes the patches.
internal static class CpuProbe
{
    private const string HarmonyId = "com.sts2perf.cpuprobe";
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    // Engine-to-C# callbacks: the per-frame ones, _Notification (every
    // notification an overriding node gets, e.g. each label redraw), and the
    // text effects' _ProcessCustomFX (once per character per redraw).
    private static readonly string[] Callbacks = { "_Process", "_PhysicsProcess", "_Draw", "_Notification" };
    private static readonly string[] EffectCallbacks = { "_ProcessCustomFX" };

    // Game methods that aren't per-frame callbacks but run on many frames in a
    // fight: the combat UI refresh's handlers (see CombatUiRefresh) and the
    // card and text updates they make. Listed alongside the callbacks; they
    // nest (UpdateVisuals runs inside the hand's handler), so they're left out
    // of the total.
    private static readonly string[] Extra =
    {
        "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand:OnCombatStateChanged",
        "MegaCrit.Sts2.Core.Nodes.Combat.NIntent:OnCombatStateChanged",
        "MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay:OnCombatStateChanged",
        "MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter:OnCombatStateChanged",
        "MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton:OnCombatStateChanged",
        "MegaCrit.Sts2.Core.Nodes.Combat.NCreature:ShowCreatureHoverTips",
        "MegaCrit.Sts2.Core.Nodes.Combat.NPower:ShowPowerHoverTips",
        "MegaCrit.Sts2.Core.Nodes.Cards.NCard:UpdateVisuals",
        "MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel:AdjustFontSize",
        "MegaCrit.Sts2.addons.mega_text.MegaLabel:AdjustFontSize",
    };

    private static readonly HashSet<MethodBase> ExtraMethods = new();

    private sealed class Stat
    {
        public long Ticks;
        public int Calls;
        public long Bytes;
    }

    private static Harmony _harmony;
    private static bool _patched;
    private static readonly Dictionary<MethodBase, Stat> Stats = new();
    private static readonly Dictionary<MethodBase, long> Frame = new();

    public static bool Active => _patched;

    // From PerfRuntime.ApplyAll once the game's UI is up.
    public static void Apply()
    {
        var on = PerfSettings.Value("cpu_probe") == "on";
        if (on && !_patched)
            Patch();
        else if (!on && _patched)
            Unpatch();
    }

    private static void Patch()
    {
        var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "sts2");
        if (game == null)
            return;
        _patched = true;
        var clock = Stopwatch.StartNew();
        _harmony ??= new Harmony(HarmonyId);
        var prefix = new HarmonyMethod(typeof(CpuProbe), nameof(Prefix));
        var postfix = new HarmonyMethod(typeof(CpuProbe), nameof(Postfix));
        var count = 0;
        var failed = 0;
        foreach (var type in Types(game))
        {
            if (type.ContainsGenericParameters)
                continue;
            var names = typeof(Node).IsAssignableFrom(type) ? Callbacks : typeof(RichTextEffect).IsAssignableFrom(type) ? EffectCallbacks : null;
            if (names == null)
                continue;
            foreach (var method in type.GetMethods(Declared).Where(m => names.Contains(m.Name) && !m.IsAbstract))
            {
                try
                {
                    _harmony.Patch(method, prefix: prefix, postfix: postfix);
                    count++;
                }
                catch (Exception ex)
                {
                    if (++failed <= 3)
                        PerfEntry.Log($"cpu breakdown: can't time {type.Name}.{method.Name}: {ex.Message}");
                }
            }
        }
        var extra = 0;
        foreach (var name in Extra)
        {
            var method = AccessTools.Method(name);
            if (method == null)
                continue;
            try
            {
                _harmony.Patch(method, prefix: prefix, postfix: postfix);
                ExtraMethods.Add(method);
                extra++;
            }
            catch (Exception ex)
            {
                PerfEntry.Log($"cpu breakdown: can't time {name}: {ex.Message}");
            }
        }
        PerfEntry.Log($"cpu breakdown: timing {count} per-frame callbacks of the game's scripts ({failed} failed) and {extra} combat UI methods, set up in {clock.Elapsed.TotalSeconds:0.0} s");
    }

    private static void Unpatch()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _patched = false;
        ExtraMethods.Clear();
        lock (Stats)
        {
            Stats.Clear();
            Frame.Clear();
        }
        PerfEntry.Log("cpu breakdown: off");
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null);
        }
    }

    private static void Prefix(out (long Ticks, long Bytes) __state) =>
        __state = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    private static void Postfix((long Ticks, long Bytes) __state, MethodBase __originalMethod)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state.Ticks;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - __state.Bytes;
        lock (Stats)
        {
            if (!Stats.TryGetValue(__originalMethod, out var stat))
                Stats[__originalMethod] = stat = new Stat();
            stat.Ticks += elapsed;
            stat.Calls++;
            stat.Bytes += bytes;
            Frame[__originalMethod] = Frame.TryGetValue(__originalMethod, out var ticks) ? ticks + elapsed : elapsed;
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // For FrameLog's hitch line: this frame's biggest callbacks, or null.
    public static string FrameSummary()
    {
        if (!_patched)
            return null;
        lock (Stats)
        {
            var total = Ms(Frame.Where(kv => !ExtraMethods.Contains(kv.Key)).Sum(kv => kv.Value));
            if (total < 1 && !Frame.Any(kv => Ms(kv.Value) >= 1))
                return null;
            var top = Frame.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key.DeclaringType?.Name}.{kv.Key.Name} {Ms(kv.Value):0.0} ms");
            return $"scripts {total:0} ms ({string.Join(", ", top)})";
        }
    }

    public static void ResetFrame()
    {
        if (!_patched)
            return;
        lock (Stats)
            Frame.Clear();
    }

    // For FrameLog's stats line: the window's biggest callbacks, per frame,
    // then starts a new window.
    public static string TakeSummary(int frames)
    {
        if (!_patched || frames == 0)
            return "";
        List<(MethodBase Method, long Ticks, int Calls, long Bytes)> rows;
        lock (Stats)
        {
            rows = Stats.Select(kv => (kv.Key, kv.Value.Ticks, kv.Value.Calls, kv.Value.Bytes)).ToList();
            Stats.Clear();
        }
        double PerFrame(long ticks) => Ms(ticks) / frames;
        var top = rows.OrderByDescending(r => r.Ticks)
            .Take(8)
            .Select(r => $"{r.Method.DeclaringType?.Name}.{r.Method.Name} {PerFrame(r.Ticks):0.00} ms x{(double)r.Calls / frames:0.#}");
        double Kb(long bytes) => bytes / 1024.0 / frames;
        var garbage = rows.Where(r => r.Bytes > 0)
            .OrderByDescending(r => r.Bytes)
            .Take(6)
            .Select(r => $"{r.Method.DeclaringType?.Name}.{r.Method.Name} {Kb(r.Bytes):0.0} KB");
        return $" | scripts {PerFrame(rows.Where(r => !ExtraMethods.Contains(r.Method)).Sum(r => r.Ticks)):0.0} ms/frame: {string.Join(", ", top)}"
            + $" | garbage {Kb(rows.Where(r => !ExtraMethods.Contains(r.Method)).Sum(r => r.Bytes)):0} KB/frame: {string.Join(", ", garbage)}";
    }
}
