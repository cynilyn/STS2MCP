using System;
using System.Collections;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // Monotonic counter bumped by game events that change combat state (see AddCombatSyncState).
    // Only touched on the main thread (game events and RunOnMainThread both run there).
    private static long _stateVersion;
    private static ActionExecutor? _versionExecutor;
    private static bool _combatEventsHooked;

    private static void BumpStateVersion() => _stateVersion++;

    private static void EnsureStateVersionHooks()
    {
        var executor = RunManager.Instance?.ActionExecutor;
        if (executor != null && !ReferenceEquals(executor, _versionExecutor))
        {
            // RunManager creates a new ActionExecutor per run; the old one is dropped with its run.
            executor.BeforeActionExecuted += _ => BumpStateVersion();
            executor.AfterActionExecuted += _ => BumpStateVersion();
            _versionExecutor = executor;
        }
        var combat = CombatManager.Instance;
        if (combat != null && !_combatEventsHooked)
        {
            combat.TurnStarted += _ => BumpStateVersion();
            combat.TurnEnded += _ => BumpStateVersion();
            combat.PlayerActionsDisabledChanged += _ => BumpStateVersion();
            _combatEventsHooked = true;
        }
    }

    /// <summary>
    /// Adds action-queue sync fields to a state response while combat is in progress, so a client can tell
    /// whether the game is still resolving queued actions (card/potion effects, auto-plays, draws).
    /// Must run on the main thread (called inside RunOnMainThread).
    /// </summary>
    private static Dictionary<string, object?> AddCombatSyncState(Dictionary<string, object?> result)
    {
        try
        {
            EnsureStateVersionHooks();
            var combat = CombatManager.Instance;
            if (combat == null || !combat.IsInProgress)
                return result;

            var executor = RunManager.Instance.ActionExecutor;
            bool executorRunning = executor?.IsRunning ?? false;
            var current = executor?.CurrentlyRunningAction;

            // Inspect the player action queues (ActionQueueSet._actionQueues is private). Mirrors
            // ActionQueueSet.GetReadyAction: only the front (non-canceled) action of each queue can run; a
            // queue whose front waits for a player choice blocks the actions behind it, so those are not
            // counted as pending (otherwise a hand/card select would look busy forever).
            int ready = 0, awaitingChoice = 0, queued = 0;
            var queueSet = RunManager.Instance.ActionQueueSet;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            if (queueSet?.GetType().GetField("_actionQueues", flags)?.GetValue(queueSet) is IEnumerable queues)
            {
                foreach (var queue in queues)
                {
                    if (queue == null) continue;
                    var qType = queue.GetType();
                    if (qType.GetField("actions")?.GetValue(queue) is not IEnumerable actions) continue;
                    bool queuePaused = qType.GetField("isPaused")?.GetValue(queue) is true;
                    GameAction? front = null;
                    foreach (var a in actions)
                    {
                        if (a is not GameAction action) continue;
                        if (action.State == GameActionState.Canceled || action.State == GameActionState.Finished) continue;
                        queued++;
                        front ??= action;
                    }
                    if (front == null) continue;
                    switch (front.State)
                    {
                        case GameActionState.GatheringPlayerChoice:
                            awaitingChoice++;
                            break;
                        case GameActionState.Executing:
                            ready++;
                            break;
                        case GameActionState.WaitingForExecution:
                        case GameActionState.ReadyToResumeExecuting:
                            if (front.ActionType == GameActionType.NonCombat) break; // never runs during combat
                            if (queuePaused && front.ActionType == GameActionType.CombatPlayPhaseOnly) break;
                            ready++;
                            break;
                    }
                }
            }

            bool effectRunning = false;
            var me = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
            if (me != null)
                effectRunning = combat.IsExecutingCardOrPotionEffect(me);

            // A card/potion effect suspended on a player choice (hand/card select) keeps its effect depth > 0,
            // so the effect flag only counts when nothing is waiting for a choice.
            result["action_queue_busy"] = executorRunning || ready > 0 || (effectRunning && awaitingChoice == 0);
            result["state_version"] = _stateVersion;
            result["action_queue"] = new Dictionary<string, object?>
            {
                ["executor_running"] = executorRunning,
                ["current_action"] = current?.GetType().Name,
                ["pending_actions"] = ready,
                ["queued_actions"] = queued,
                ["awaiting_player_choice"] = awaitingChoice > 0,
                ["card_or_potion_effect_running"] = effectRunning,
                ["executor_paused"] = executor?.IsPaused ?? false,
                ["combat_paused"] = combat.IsPaused,
                ["player_actions_disabled"] = combat.PlayerActionsDisabled,
                ["hand_in_card_play"] = NPlayerHand.Instance?.InCardPlay ?? false,
            };
        }
        catch (Exception ex)
        {
            result["action_queue_error"] = ex.Message;
        }
        return result;
    }
}
