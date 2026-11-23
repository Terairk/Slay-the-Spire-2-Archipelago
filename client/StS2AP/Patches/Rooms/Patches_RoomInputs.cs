using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace StS2AP.Patches;

/// <summary>
/// Queues the host's room inputs ahead of its move into a rest site or its vote for the next act.
/// See <see cref="RoomInputsMultiplayer"/> for why the order matters.
/// </summary>
public static class Patches_RoomInputs
{
    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
    private static class RequestEnqueue
    {
        [HarmonyPrefix]
        private static void QueueRoomInputsFirst(GameAction action) =>
            RoomInputsMultiplayer.BeforeHostRequestsAction(action);
    }
}
