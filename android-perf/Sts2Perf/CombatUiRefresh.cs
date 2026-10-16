using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// combat_ui=limited (Perf settings → Combat UI refresh): the game refreshes
// its combat UI at most 10 times a second instead of on every frame
// something changes, and updates the cards in hand a few per frame instead
// of all in one.
//
// Every change in a fight (HP, block, a power applied or ticking down,
// energy, a card moving between piles, a history entry) calls the game's
// CombatStateTracker.NotifyCombatStateChanged, which waits for the next frame
// and raises CombatStateChanged once. Its subscribers are all UI: the hand
// re-formats every card's description (SmartFormat, damage previews through
// every power and relic), every intent, the creatures' state displays, the
// energy counter, the end-turn button. During an enemy's move something
// changes on most frames, so that refresh ran frame after frame: on a Pad 5
// those frames had 45-85 ms of game logic with the GPU at 12 ms.
//
// 1. How often. A change right after a quiet spell still refreshes on the
//    next frame, as the game does; within IntervalMs of the last refresh it
//    waits until then, and every change meanwhile folds into that one
//    refresh. The game's own refresh code runs (CallCombatStateChangedDeferred).
//    0.4.2.14 on a Pad 5: 63 changes became 17 refreshes, 98 became 26.
// 2. How much at once. One refresh still updated every card in hand in the
//    same frame: NPlayerHand.OnCombatStateChanged took up to 41 ms, 39 of it
//    in NCard.UpdateVisuals (about 5 ms a card). So the hand's per-card
//    updates (NHandCardHolder.UpdateCard) made during that refresh are queued
//    and run on the next frames, HandBudgetMs of them per frame (at least
//    one), each card once however often it was queued. The rest of the
//    refresh (the hand's enabled state, intents, health) still runs at once.
//
// Numbers on cards and intents can lag a change by up to a tenth of a
// second, a little more for the last card in a full hand. If the game's
// methods aren't as expected (a game update), that part is left to the game.
internal static class CombatUiRefresh
{
    private const string HarmonyId = "com.sts2perf.combatui";
    private const ulong IntervalMs = 100;
    private const double HandBudgetMs = 5;

    private static Harmony _harmony;
    private static bool _limited;
    private static FieldInfo _pendingField;
    private static MethodInfo _refresh;
    private static MethodInfo _updateCard;
    private static ulong _lastMs;
    private static int _refreshes;
    private static int _requests;
    private static bool _unavailable;
    private static bool _inHandRefresh;
    private static bool _pumping;
    private static bool _pumpHooked;
    private static readonly Queue<Node> HandQueue = new();
    private static readonly HashSet<Node> HandQueued = new();
    private static int _cardsDeferred;
    private static int _pumpFrames;

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
            if (notify == null || _pendingField?.FieldType != typeof(Task) || _refresh?.ReturnType != typeof(Task) || _refresh.GetParameters().Length != 0)
            {
                PerfEntry.Log("combat UI refresh: CombatStateTracker not as expected in this game version, left as the game does it");
                _unavailable = true;
                return;
            }
            _harmony ??= new Harmony(HarmonyId);
            _harmony.Patch(notify, prefix: new HarmonyMethod(typeof(CombatUiRefresh), nameof(Notify)));
            PatchHand();
        }
        else
        {
            _harmony.UnpatchAll(HarmonyId);
            FlushHand();
        }
        _limited = limited;
        PerfEntry.Log(
            limited
                ? $"combat UI refresh: at most every {IntervalMs} ms{(_updateCard != null ? $", hand cards {HandBudgetMs:0} ms per frame" : "")}"
                : "combat UI refresh: as the game"
        );
    }

    private static void PatchHand()
    {
        var hand = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand:OnCombatStateChanged");
        _updateCard = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder:UpdateCard");
        if (hand == null || _updateCard == null || _updateCard.GetParameters().Length != 0 || !typeof(Node).IsAssignableFrom(_updateCard.DeclaringType))
        {
            PerfEntry.Log("combat UI refresh: the hand isn't as expected in this game version, its cards update as the game does");
            _updateCard = null;
            return;
        }
        _harmony.Patch(hand,
            prefix: new HarmonyMethod(typeof(CombatUiRefresh), nameof(HandRefreshStart)),
            postfix: new HarmonyMethod(typeof(CombatUiRefresh), nameof(HandRefreshEnd)));
        _harmony.Patch(_updateCard, prefix: new HarmonyMethod(typeof(CombatUiRefresh), nameof(DeferUpdateCard)));
        if (!_pumpHooked && Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += Pump;
            _pumpHooked = true;
        }
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
        catch (OperationCanceledException)
        {
            // The game cancels it when a fight ends; so does its own.
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

    private static void HandRefreshStart() => _inHandRefresh = true;

    private static void HandRefreshEnd() => _inHandRefresh = false;

    // During the hand's refresh, a card's update is queued instead of run.
    private static bool DeferUpdateCard(object __instance)
    {
        if (!_inHandRefresh || _pumping || __instance is not Node holder)
            return true;
        if (HandQueued.Add(holder))
        {
            HandQueue.Enqueue(holder);
            _cardsDeferred++;
        }
        return false;
    }

    // Each frame: the queued card updates, HandBudgetMs of them (at least one).
    private static void Pump()
    {
        // A refresh that threw never reached its postfix.
        _inHandRefresh = false;
        if (HandQueue.Count == 0)
            return;
        _pumpFrames++;
        var clock = Stopwatch.StartNew();
        do
        {
            var holder = HandQueue.Dequeue();
            HandQueued.Remove(holder);
            Run(holder);
        } while (HandQueue.Count > 0 && clock.Elapsed.TotalMilliseconds < HandBudgetMs);
    }

    // Turned off: whatever is still queued runs now.
    private static void FlushHand()
    {
        _inHandRefresh = false;
        while (HandQueue.Count > 0)
            Run(HandQueue.Dequeue());
        HandQueued.Clear();
    }

    private static void Run(Node holder)
    {
        if (_updateCard == null || !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
            return;
        _pumping = true;
        try
        {
            _updateCard.Invoke(holder, null);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"combat UI refresh: card update failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally
        {
            _pumping = false;
        }
    }

    // For FrameLog's stats line: changes, refreshes and spread card updates.
    public static string TakeWindowSummary()
    {
        if (!_limited)
            return "";
        var text = $" | combat ui {_refreshes} refreshes for {_requests} changes"
            + (_cardsDeferred > 0 ? $", {_cardsDeferred} card updates over {_pumpFrames} frames" : "");
        _refreshes = _requests = _cardsDeferred = _pumpFrames = 0;
        return text;
    }
}
