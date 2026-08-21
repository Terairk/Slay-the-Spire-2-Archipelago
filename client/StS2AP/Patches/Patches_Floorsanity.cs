using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StS2AP.Patches
{
    /// <summary>
    /// Patches for `AbstractRoom` and all of its derived classes.
    /// Sends Archipelago location checks when entering rooms to track floor progress.
    /// </summary>
    public static class Patches_Floorsanity
    {
        /// <summary>
        /// Sends an Archipelago location check when entering any room.
        /// Patches all room types (Combat, Event, Treasure, Rest Site, Merchant) since abstract classes cannot be patched directly.
        /// </summary>
        [HarmonyPatch]
        public static class OnRoomEnter
        {
            /// <summary>
            /// List of all room types that should trigger floor checks when entered.
            /// </summary>
            private static readonly Type[] RoomTypes =
            [
                typeof(CombatRoom),
                typeof(EventRoom),
                typeof(TreasureRoom),
                typeof(RestSiteRoom),
                typeof(MerchantRoom)
            ];

            /// <summary>
            /// Identifies all the `Enter` methods from each room type that should be patched.
            /// Harmony will apply the postfix patch to each of these methods.
            /// </summary>
            /// <returns>An enumerable of MethodBase objects representing each Enter method to patch.</returns>
            [HarmonyTargetMethods]
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in RoomTypes)
                {
                    var method = AccessTools.Method(type, nameof(CombatRoom.Enter));
                    if (method != null)
                    {
                        yield return method;
                    }
                }
            }

            /// <summary>
            /// Postfix patch that sends a floor check when entering any room type.
            /// </summary>
            /// <param name="runState">The current run state.</param>
            /// <param name="isRestoringRoomStackBase">Whether the room is being restored from save.</param>
            [HarmonyPostfix]
            public static void Postfix(IRunState? runState, bool isRestoringRoomStackBase)
            {
                if (!MultiplayerSupport.ShouldRunReplicatedConstruction(
                    MultiplayerFeature.FloorChecks))
                    return;

                TrySendFloorChecks(runState);
            }
        }

        /// <summary>
        /// The logic to determine if we need to send a location check
        /// </summary>
        /// <param name="runState">The current state of the run</param>
        static void TrySendFloorChecks(IRunState? runState)
        {
            if (runState == null)
            {
                LogUtility.Error("Run state is null, skipping Archipelago floor checks");
                return;
            }

            // Try to get floor information from runState using reflection
            var floorProperty = runState.GetType().GetProperty("TotalFloor");

            if (floorProperty == null)
            {
                LogUtility.Error("fail");
                return;
            }

            var floorValue = floorProperty.GetValue(runState);
            IEnumerable<MegaCrit.Sts2.Core.Entities.Players.Player> players =
                runState is RunState concreteRun
                    ? concreteRun.Players
                    : GameUtility.CurrentPlayer is { } currentPlayer
                        ? new[] { currentPlayer }
                        : Array.Empty<MegaCrit.Sts2.Core.Entities.Players.Player>();
            foreach (var player in players)
            {
                if (!MultiplayerLocationChecks.TryGetSettings(
                        player,
                        out ArchipelagoSettings settings)
                    || !settings.Floorsanity
                    || !MultiplayerLocationChecks.IsCheckWriter(player))
                {
                    continue;
                }

                string locationName = $"{player.APName()} Reached Floor {floorValue}";
                LogUtility.Debug($"Attempting to record floor check: {locationName}");
                MultiplayerLocationChecks.QueueCheck(player, locationName);
            }
        }
    }
}
