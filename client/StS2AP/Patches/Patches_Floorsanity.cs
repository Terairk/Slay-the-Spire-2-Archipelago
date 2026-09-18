using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Data;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.UI;
using StS2AP.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
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
            /// It also forces a refresh of the Archipelago Unused Item Count, for run start sync issues.
            /// </summary>
            /// <param name="runState">The current run state.</param>
            /// <param name="isRestoringRoomStackBase">Whether the room is being restored from save.</param>
            [HarmonyPostfix]
            public static void Postfix(IRunState? runState, bool isRestoringRoomStackBase)
            {
                // Attempt to send a check for the current room we're on
                if(ArchipelagoClient.Settings.Floorsanity)
                {
                    TrySendFloorCheck(runState);
                }
            }
        }

        /// <summary>
        /// The logic to determine if we need to send a location check
        /// </summary>
        /// <param name="runState">The current state of the run</param>
        static void TrySendFloorCheck(IRunState? runState)
        {
            // Null checks to shut compiler up
            if (GameUtility.CurrentPlayer == null || runState == null)
            {
                LogUtility.Error("CurrentPlayer or runState is null, skipping Archipelago check");
                return;
            }

            // Try to get floor information from runState using reflection
            var floorProperty = runState.GetType().GetProperty("TotalFloor");

            if (floorProperty == null)
            {
                LogUtility.Error("fail");
                return;
            }

            if (floorProperty.GetValue(runState) is not object rawFloor)
            {
                LogUtility.Error("TotalFloor had no value, skipping Archipelago check");
                return;
            }

            int floorValue = Convert.ToInt32(rawFloor);
            if (floorValue < 1)
            {
                LogUtility.Warn($"Cannot send a floor check for invalid floor {floorValue}");
                return;
            }

            var name = GameUtility.CurrentPlayer.APName();
            var locationName = $"{name} Reached Floor {floorValue}";

            int lastGeneratedFloor = Math.Min(floorValue, LocationData.MaxFloor);
            long[] floorLocationIds = Enumerable.Range(1, lastGeneratedFloor)
                .Select(floor => LocationData.GetFloorLocation(GameUtility.CurrentPlayer.Character, floor))
                .ToArray();
            LocationCheckSendResult result = GameUtility.SendChecks(floorLocationIds);

            if (result.AcceptedCount > 0)
            {
                string message =
                    $"{result.AcceptedCount} floor check(s) through {locationName}";
                if (result.Dispatch == LocationCheckSendResult.DispatchStatus.Submitted)
                    LogUtility.Success($"Submitted {message}");
                else
                    LogUtility.Warn($"Queued {message} until the AP connection recovers");
            }
            else if (result.Dispatch == LocationCheckSendResult.DispatchStatus.PersistenceFailed)
            {
                LogUtility.Error($"Could not persist floor checks through {locationName}");
            }
            else if (result.Dispatch == LocationCheckSendResult.DispatchStatus.NoAuthenticatedSlot)
            {
                LogUtility.Warn($"Could not record {locationName}: no authenticated AP slot");
            }
            else if (result.AlreadyCheckedCount > 0 && result.NotInSlotCount == 0)
            {
                LogUtility.Debug($"Floor checks through {locationName} are already recorded");
            }
            else
            {
                LogUtility.Warn(
                    $"No new floor checks through {locationName}; {result.NotInSlotCount} location(s) are not in this AP slot"
                );
            }
        }
    }
}

