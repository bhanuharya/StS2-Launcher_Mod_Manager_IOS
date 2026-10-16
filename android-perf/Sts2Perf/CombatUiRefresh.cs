using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// combat_ui=limited (Perf settings → Combat UI refresh): the game redraws its
// combat UI at most 10 times a second instead of on every frame something
// changes.
//
// Every change in a fight (HP, block, a power applied or ticking down,
// energy, a card moving between piles, a history entry) calls the game's
// CombatStateTracker.NotifyCombatStateChanged, which waits for the next frame
// and raises CombatStateChanged once. Its subscribers are all UI: the hand
// re-formats every card's description (SmartFormat, damage previews through
// every power and relic) and auto-sizes its text by re-shaping it at several
// font sizes, every intent, the creatures' state displays, the energy
// counter, the end-turn button. During an enemy's move something changes on
// most frames, so that refresh runs frame after frame: on a Pad 5 those
// frames had 45-85 ms of game logic with the GPU at 12 ms.
//
// With limited, a change right after a quiet spell still refreshes on the
// next frame, as the game does; within IntervalMs of the last refresh it
// waits until then, and every change meanwhile folds into that one refresh.
// The game's own refresh code runs unchanged (CallCombatStateChangedDeferred);
// only how often. Numbers on cards and intents can lag a change by up to a
// tenth of a second.
internal static class CombatUiRefresh
{
    private const string HarmonyId = "com.sts2perf.combatui";
    private const ulong IntervalMs = 100;

    private static Harmony _harmony;
    private static bool _limited;
    private static FieldInfo _pendingField;
    private static MethodInfo _refresh;
    private static ulong _lastMs;
    private static int _refreshes;
    private static int _requests;
    private static bool _unavailable;

    // From PerfRuntime.ApplyAll.
    public static void Apply()
    {
        var limited = PerfSettings.Value("combat_ui") == "limited";
        if (limited == _limited || _unavailable)
            return;
        if (limited)
        {
            var tracker = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatStateTracker");
            var notify = tracker == null ? null : AccessTools.Method(tracker, "NotifyCombatStateChanged");
            _pendingField = tracker == null ? null : AccessTools.Field(tracker, "_combatStateChangedDeferredTask");
            _refresh = tracker == null ? null : AccessTools.Method(tracker, "CallCombatStateChangedDeferred");
            if (notify == null || _pendingField == null || _refresh == null)
            {
                PerfEntry.Log("combat UI refresh: CombatStateTracker not as expected in this game version, left as the game does it");
                _unavailable = true;
                return;
            }
            _harmony ??= new Harmony(HarmonyId);
            _harmony.Patch(notify, prefix: new HarmonyMethod(typeof(CombatUiRefresh), nameof(Notify)));
        }
        else
        {
            _harmony.UnpatchAll(HarmonyId);
        }
        _limited = limited;
        PerfEntry.Log(limited ? $"combat UI refresh: at most every {IntervalMs} ms" : "combat UI refresh: as the game");
    }

    // Replaces NotifyCombatStateChanged: one refresh in flight at a time, as
    // the game does, but started no sooner than IntervalMs after the last.
    private static bool Notify(object __instance)
    {
        try
        {
            _requests++;
            if (_pendingField.GetValue(__instance) is Task { IsCompleted: false })
                return false;
            _pendingField.SetValue(__instance, Refresh(__instance));
            return false;
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"combat UI refresh: {ex.Message}; left to the game");
            return true;
        }
    }

    private static async Task Refresh(object tracker)
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            while (_lastMs != 0 && Time.GetTicksMsec() - _lastMs < IntervalMs)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            // The game's own: waits a frame, then raises CombatStateChanged.
            await (Task)_refresh.Invoke(tracker, null);
            _refreshes++;
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"combat UI refresh failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally
        {
            _lastMs = Time.GetTicksMsec();
        }
    }

    // For FrameLog's stats line: changes and refreshes in the window.
    public static string TakeWindowSummary()
    {
        if (!_limited)
            return "";
        var text = $" | combat ui {_refreshes} refreshes for {_requests} changes";
        _refreshes = _requests = 0;
        return text;
    }
}
