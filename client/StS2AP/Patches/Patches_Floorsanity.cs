using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
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
            [HarmonyPostfix]
            public static void Postfix(
                AbstractRoom __instance,
                IRunState? runState)
            {
                if (!MultiplayerSupport.ShouldRunReplicatedConstruction(
                    MultiplayerFeature.FloorChecks))
                    return;

                TrySendFloorChecks(__instance, runState);
            }
        }

        /// <summary>
        /// The logic to determine if we need to send a location check
        /// </summary>
        /// <param name="runState">The current state of the run</param>
        static void TrySendFloorChecks(
            AbstractRoom room,
            IRunState? runState)
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

            object? floorValue = floorProperty.GetValue(runState);
            if (floorValue == null)
            {
                LogUtility.Error("TotalFloor was null, skipping Archipelago floor checks");
                return;
            }

            var concreteRun = runState as RunState;
            int rawFloor = Convert.ToInt32(floorValue);
            int normalizedFloor = MultiplayerSupport.IsRealMultiplayerRun && concreteRun != null
                ? rawFloor + concreteRun.CurrentActIndex
                : rawFloor;
            IEnumerable<Player> players =
                concreteRun != null
                    ? concreteRun.Players
                    : GameUtility.CurrentPlayer is { } currentPlayer
                        ? new[] { currentPlayer }
                        : Array.Empty<Player>();

            bool isMultiplayerBossBoundary = MultiplayerSupport.IsRealMultiplayerRun
                && concreteRun != null
                && room.RoomType == RoomType.Boss;

            foreach (var player in players)
            {
                if (!MultiplayerLocationChecks.TryGetCheckSettings(
                        player,
                        out ArchipelagoSettings settings))
                {
                    continue;
                }

                if (settings.Floorsanity && MultiplayerLocationChecks.IsCheckWriter(player))
                {
                    // IMPORTANT: Multiplayer has one fewer physical floor in every act. Keep the
                    // APWorld's singleplayer-compatible 17/16/15 layout by offsetting later acts,
                    // then emit the missing boss-arena milestone when the boss room is entered.
                    QueueFloorCheck(player, normalizedFloor);
                    if (isMultiplayerBossBoundary)
                        QueueFloorCheck(player, normalizedFloor + 1);
                }

                if (isMultiplayerBossBoundary)
                {
                    int act = concreteRun!.CurrentActIndex + 1;
                    if (MultiplayerLocationChecks.TryMarkBossCompensation(player, act))
                        ApplyMultiplayerBossCompensation(player, settings, act);
                }
            }
        }

        private static void QueueFloorCheck(Player player, int floor)
        {
            string locationName = $"{player.APName()} Reached Floor {floor}";
            LogUtility.Debug($"Attempting to record floor check: {locationName}");
            MultiplayerLocationChecks.QueueCheck(player, locationName);
        }

        private static void ApplyMultiplayerBossCompensation(
            Player player,
            ArchipelagoSettings settings,
            int act)
        {
            // IMPORTANT: The APWorld intentionally retains the singleplayer location counts.
            // Multiplayer automatically contributes missing card, combat-gold, and potion checks
            // at the Act 1/2 bosses, plus Relic 10 at the Act 3 boss, so every replica advances
            // the same numbered cursors at deterministic boundaries.
            if (act is 1 or 2)
            {
                QueueSyntheticCardCheck(player, settings);

                if (settings.GoldSanity)
                {
                    int goldNumber = MultiplayerLocationChecks.IncrementGoldRewards(player);
                    if (goldNumber <= ArchipelagoProgress._maxGoldRewards)
                    {
                        MultiplayerLocationChecks.QueueCheck(
                            player,
                            $"{player.APName()} Combat Gold {goldNumber}"
                        );
                    }
                }

                if (settings.PotionSanity)
                {
                    int potionNumber = MultiplayerLocationChecks.IncrementPotionRewards(player);
                    if (potionNumber <= ArchipelagoProgress._maxPotionRewards)
                    {
                        MultiplayerLocationChecks.QueueCheck(
                            player,
                            $"{player.APName()} Potion Drop {potionNumber}"
                        );
                    }
                }

                MultiplayerLocationChecks.PublishLocalProgress(player);
                return;
            }

            if (act == 3)
            {
                // Relic attempts also authorize and bank incoming relic rewards. This is only an
                // outgoing location replacement, so send the final Act 3 check without touching
                // RelicRewardsAttempted or BankedRelicRewards.
                MultiplayerLocationChecks.QueueCheck(
                    player,
                    $"{player.APName()} Relic {ArchipelagoProgress._maxRelicRewards}"
                );
            }
        }

        private static void QueueSyntheticCardCheck(
            Player player,
            ArchipelagoSettings settings)
        {
            int attempt = MultiplayerLocationChecks.IncrementCardRewards(player);
            if (!settings.ShouldShuffleAllCards && attempt % 2 == 0)
                attempt = MultiplayerLocationChecks.IncrementCardRewards(player);

            int rewardNumber = settings.ShouldShuffleAllCards
                ? attempt
                : (attempt + 1) / 2;
            int maximum = settings.ShouldShuffleAllCards
                ? ArchipelagoProgress._maxCardRewards
                : ArchipelagoProgress._maxCardRewards / 2;
            if (rewardNumber <= maximum)
            {
                MultiplayerLocationChecks.QueueCheck(
                    player,
                    $"{player.APName()} Card Reward {rewardNumber}"
                );
            }
        }
    }
}
