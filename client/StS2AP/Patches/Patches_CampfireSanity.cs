
using Archipelago.MultiClient.Net.Models;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.Utils;


namespace StS2AP.Patches
{
    public static class Patches_RestSiteOption
    {
        [HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
        public static class BeginRestSite
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                RestSiteMultiplayer.BeforeOptionsGenerated();
            }

            [HarmonyPostfix]
            public static void Postfix(RestSiteSynchronizer __instance)
            {
                RestSiteMultiplayer.AfterOptionsGenerated(__instance);
            }
        }

        [HarmonyPatch(typeof(RestSiteOption), "Generate")]
        public static class Generate
        {
            [HarmonyPostfix]
            static void AddOptions(Player player, ref List<RestSiteOption> __result)
            {
                if (!MultiplayerSupport.ShouldRunReplicatedConstruction(
                        MultiplayerFeature.RestSites
                    ))
                    return;

                if (!MultiplayerSupport.IsRealMultiplayerRun)
                {
                    ApplySingleplayerOptions(player, __result);
                    return;
                }

                if (!MultiplayerLocationChecks.TryGetSettings(
                        player,
                        out ArchipelagoSettings settings
                    )
                    || !settings.CampfireSanity)
                {
                    return;
                }

                if (!RestSiteMultiplayer.TryGetFrozenState(
                        player,
                        out ApRestSiteState state,
                        out string reason
                    ))
                {
                    RestSiteMultiplayer.ReportConstructionFailure(reason);
                    __result.Clear();
                    __result.Add(new RestSiteSyncBlockedOption(player));
                    return;
                }

                ApplyMultiplayerOptions(player, __result, state);
            }

            private static void ApplySingleplayerOptions(
                Player player,
                List<RestSiteOption> options)
            {
                if (!ArchipelagoClient.Settings.CampfireSanity)
                    return;

                var progress = ArchipelagoClient.Progress;
                LogUtility.Info(
                    $"Adding Campfire Locations for act {player.RunState.CurrentActIndex}"
                );
                for (int act = 1; act <= player.RunState.CurrentActIndex + 1; act++)
                {
                    for (int campfire = 1; campfire <= 2; campfire++)
                    {
                        string checkName = $"{player.APName()} Act {act} Campfire {campfire}";
                        progress.CampfiresChecked.TryGetValue(checkName, out bool isChecked);
                        if (isChecked)
                            continue;

                        long locationId = ArchipelagoClient.Session.Locations
                            .GetLocationIdFromName("Slay the Spire II", checkName);
                        LogUtility.Info(
                            $"Adding campfire location {locationId} {checkName}"
                        );
                        string description = checkName;
                        string optionId = "FILLER";
                        if (ArchipelagoClient.ScoutedLocations.TryGetValue(
                                locationId,
                                out ScoutedItemInfo? info
                            ))
                        {
                            description = $"{info.Player.Alias}'s {info.ItemName}";
                            optionId = GetScoutedOptionId(info);
                        }
                        options.Add(new APRestOption(
                            player,
                            locationId,
                            optionId,
                            description,
                            checkName
                        ));
                    }
                }

                long? currentCharacterId = GameUtility.CurrentCharacterID;
                int currentAct = Math.Min(player.RunState.CurrentActIndex + 1, 3);
                int restLevel = currentCharacterId.HasValue
                    ? ArchipelagoClient.Progress.MaxRestLevel(currentCharacterId.Value) ?? 0
                    : 0;
                int smithLevel = currentCharacterId.HasValue
                    ? ArchipelagoClient.Progress.MaxSmithLevel(currentCharacterId.Value) ?? 0
                    : 0;
                ApplyProgressiveLocks(player, options, currentAct, restLevel, smithLevel);
            }

            private static void ApplyMultiplayerOptions(
                Player player,
                List<RestSiteOption> options,
                ApRestSiteState state)
            {
                int currentAct = Math.Min(player.RunState.CurrentActIndex + 1, 3);
                LogUtility.Info(
                    $"Adding synchronized Campfire Locations for player {player.NetId}, "
                        + $"act {currentAct}"
                );
                foreach (ApCampfireCheckState check in state.CampfireChecks
                    .Where(check => check.Act <= currentAct && !check.IsChecked)
                    .OrderBy(check => check.Act)
                    .ThenBy(check => check.Campfire))
                {
                    options.Add(new APRestOption(
                        player,
                        check.LocationId,
                        check.OptionId,
                        check.Description,
                        check.LocationName
                    ));
                }

                ApplyProgressiveLocks(
                    player,
                    options,
                    currentAct,
                    state.ProgressiveRestLevel,
                    state.ProgressiveSmithLevel
                );
            }

            private static void ApplyProgressiveLocks(
                Player player,
                List<RestSiteOption> options,
                int currentAct,
                int restLevel,
                int smithLevel)
            {
                bool canRest = restLevel >= currentAct;
                bool canSmith = smithLevel >= currentAct;
                LogUtility.Info(
                    $"Campfire access for player {player.NetId}: "
                        + $"restLevel={restLevel}, smithLevel={smithLevel}, "
                        + $"canRest={canRest}, canSmith={canSmith}"
                );

                if (!canRest)
                    options.RemoveAll(option => "HEAL".Equals(option.OptionId));
                if (!canSmith)
                    options.RemoveAll(option => "SMITH".Equals(option.OptionId));

                // Preserve the established singleplayer softlock rule exactly: relic/card
                // options do not suppress Nothing when both progressive actions are locked.
                if (!canRest && !canSmith)
                    options.Insert(0, new FakeRestOption(player));
            }

            private static string GetScoutedOptionId(ScoutedItemInfo info)
            {
                if (info.Advancement())
                    return "PROGRESSION";
                if (info.Trap())
                    return "TRAP";
                if (info.Useful())
                    return "USEFUL";
                return "FILLER";
            }
        }

        public class APRestOption : RestSiteOption, IApRestSiteSemanticOption
        {
            private readonly long locationId;
            private readonly string optionId;
            private readonly string description;
            private readonly string checkName;
            public APRestOption(
                Player owner,
                long locationId,
                string optionId,
                string description,
                string checkName) : base(owner)
            {
                this.locationId = locationId;
                this.optionId = optionId;
                this.description = description;
                this.checkName = checkName;
            }

            public string SemanticKey => $"AP_CHECK|{checkName}|{optionId}";

            public override IEnumerable<string> AssetPaths
            {
                get
                {
                    List<string> list = new List<string>();
                    list.AddRange(base.AssetPaths);
                    list.AddRange(NRestSmokeVfx.AssetPaths);
                    list.AddRange(NDesaturateTransitionVfx.AssetPaths);
                    return list;
                }
            }

            public override LocString Description
            {
                get
                {
                    LocString description = new LocString("rest_site_ui", "OPTION_CHECK.description");
                    description.Add("description", this.description);
                    return description;
                }
            }

            public override string OptionId
            {
                get { return optionId; }
            }

            public override Task<bool> OnSelect()
            {
                return SendCampfireCheck(Owner, locationId, checkName);
            }

            public static Task<bool> SendCampfireCheck(
                Player owner,
                long locationId,
                string checkName)
            {
                // Every replica executes OnSelect. Only the applicable own-slot process or the
                // fixed shared-slot host owns the external AP write; all replicas still report
                // native success so MegaCrit removes the same dense option index.
                if (!MultiplayerSupport.IsRealMultiplayerRun)
                {
                    GameUtility.SendCheck(locationId);
                    ArchipelagoClient.Progress.CampfiresChecked[checkName] = true;
                }
                else if (MultiplayerLocationChecks.IsCheckWriter(owner))
                {
                    MultiplayerLocationChecks.QueueCheck(owner, checkName, locationId);
                    ArchipelagoClient.Progress.CampfiresChecked[checkName] = true;
                    RestSiteMultiplayer.PublishRelevantStates();
                }

                return Task.FromResult(true);
            }

            // Need to override Equals because the base game does equality checks based on
            // optionId, but we have identical optionIds for different options
            public override bool Equals(object? obj)
            {
                if(obj is APRestOption otherOpt && otherOpt.locationId == locationId)
                {
                    return Owner == otherOpt.Owner;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return (locationId, Owner).GetHashCode();
            }
        }

        public class FakeRestOption : RestSiteOption, IApRestSiteSemanticOption
        {
            public FakeRestOption(Player owner) : base(owner)
            {
            }

            public override string OptionId => "NOTHING";

            public string SemanticKey => "AP_NOTHING";

            public override LocString Description
            {
                get
                {
                    LocString description = new LocString("rest_site_ui", "OPTION_NOTHING.descriptionDisabled");
                    return description;
                }
            }

            public override Task<bool> OnSelect()
            {
                return DoNothing();
            }
            public static async Task<bool> DoNothing()
            {
                return true;
            }
        }

        public sealed class RestSiteSyncBlockedOption : RestSiteOption, IApRestSiteSemanticOption
        {
            public RestSiteSyncBlockedOption(Player owner) : base(owner) { }

            public override string OptionId => "NOTHING";
            public override bool IsEnabled => false;
            public string SemanticKey => "AP_SYNC_BLOCKED";
            public override LocString Description =>
                new LocString("rest_site_ui", "OPTION_NOTHING.descriptionDisabled");
            public override Task<bool> OnSelect() => Task.FromResult(false);
        }
    }

    public static class Patches_NRestSiteRoom
    {

        [HarmonyPatch(typeof(NRestSiteRoom), nameof(NRestSiteRoom._Ready))]
        public static class _Ready
        {

            [HarmonyPrefix]
            public static void addScrollBar(NRestSiteRoom __instance)
            {
                if (!MultiplayerSupport.ShouldRunReplicatedConstruction(
                        MultiplayerFeature.RestSites
                    )
                    || GameUtility.CurrentPlayer is not Player localPlayer
                    || !MultiplayerLocationChecks.TryGetSettings(
                        localPlayer,
                        out ArchipelagoSettings settings
                    )
                    || !settings.CampfireSanity)
                    return;

                HBoxContainer choicesContainer = __instance.GetNode<HBoxContainer>("%ChoicesContainer");
                Control choicesScreen = __instance.GetNode<Control>("%ChoicesScreen");

                ScrollContainer wrapper = new ScrollContainer();
                wrapper.SetAnchorsPreset(Control.LayoutPreset.VcenterWide);
                wrapper.SetAnchorAndOffset(Side.Left, 0.5f, -__instance.Size.X/2 + 50.0f);
                wrapper.SetAnchorAndOffset(Side.Top, 0.5f, -285.0f);
                wrapper.SetAnchorAndOffset(Side.Right, 0.5f, __instance.Size.X/2 - 50.0f);
                wrapper.SetAnchorAndOffset(Side.Bottom, 0.5f, -50.0f);
                wrapper.GrowHorizontal = Control.GrowDirection.Both;
                wrapper.GrowVertical = Control.GrowDirection.Both;
                wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;

                // Allow the wrapper to size itself based on content, but cap at viewport width
                wrapper.CustomMinimumSize = new Vector2(0, 0);

                choicesContainer.SizeFlagsHorizontal = Control.SizeFlags.Expand | Control.SizeFlags.ShrinkCenter;
                choicesScreen.AddChild(wrapper);
                choicesContainer.Reparent(wrapper);
            }

            [HarmonyPostfix]
            public static void applyManifestGuard(NRestSiteRoom __instance)
            {
                RestSiteMultiplayer.ApplyManifestGuardToUi(__instance);
            }
        }
    }
}
