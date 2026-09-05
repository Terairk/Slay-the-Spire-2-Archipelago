using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using StS2AP.Data;
using StS2AP.Entities.RestSite;
using StS2AP.Models;
using StS2AP.Utils;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Models;

namespace StS2AP.Models.Singleton;

/// <summary>
/// Adds AP Campfire checks and progressive Rest/Smith locks through the base game's run-model
/// hook. RestSiteSynchronizer remains the sole owner of multiplayer selection semantics.
/// </summary>
[RegisterSingleton]
public sealed class ApRestSiteModel : HookedSingletonModel
{
    public ApRestSiteModel() : base(HookType.Run) { }

    public override bool TryModifyRestSiteOptions(
        Player player,
        ICollection<RestSiteOption> options)
    {
        if (!MultiplayerSupport.ShouldRunReplicatedConstruction(MultiplayerFeature.RestSites)
            || !ApPlayerContextResolver.TryGetRewardSettings(
                player,
                out ArchipelagoSettings settings
            )
            || !settings.CampfireSanity
            || !ApPlayerContextResolver.TryGetCharacterConfig(
                player,
                out CharacterConfig config
            ))
        {
            return false;
        }

        if (!TryGetProgress(
                player,
                config.CharOffset,
                out int restLevel,
                out int smithLevel,
                out IReadOnlySet<long> checkedLocations,
                out string reason
            ))
        {
            LogUtility.Error(
                $"Leaving native rest-site options unchanged for player {player.NetId}: {reason}"
            );
            return false;
        }

        int currentAct = Math.Min(player.RunState.CurrentActIndex + 1, 3);
        bool canRest = restLevel >= currentAct;
        bool canSmith = smithLevel >= currentAct;

        if (!canRest)
        {
            RemoveOption(options, "HEAL");
            RemoveOption(options, "MEND");
        }
        if (!canSmith)
            RemoveOption(options, "SMITH");

        // Progression unlocks do not guarantee a usable action: native Smith is disabled
        // when the deck has no upgradeable cards. Keep the existing both-locked fallback,
        // and also provide a way out when every remaining action is disabled or absent.
        // Do this before adding AP checks so taking a check is never required to leave.
        bool needsFallback = (!canRest && !canSmith) || !options.Any(option => option.IsEnabled);
        if (needsFallback)
            InsertFirst(options, new FakeRestSiteOption(player));

        if (ApPlayerContextResolver.HasCharacterChecks(player))
        {
            string characterName = config.ModNum == 0
                ? config.Name
                : $"Custom Character {config.ModNum}";
            for (int act = 1; act <= currentAct; act++)
            {
                for (int campfire = 1; campfire <= 2; campfire++)
                {
                    long locationId = LocationData.GetCampfireLocationId(
                        config.CharOffset,
                        act,
                        campfire
                    );
                    if (checkedLocations.Contains(locationId))
                        continue;

                    string locationName = $"{characterName} Act {act} Campfire {campfire}";
                    options.Add(new ApRestSiteOption(player, locationId, locationName));
                }
            }
        }

        LogUtility.Info(
            $"Applied AP rest-site options for player {player.NetId}: act={currentAct}, "
                + $"restLevel={restLevel}, smithLevel={smithLevel}, fallback={needsFallback}"
        );
        return true;
    }

    private static bool TryGetProgress(
        Player player,
        long characterOffset,
        out int restLevel,
        out int smithLevel,
        out IReadOnlySet<long> checkedLocations,
        out string reason)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
        {
            restLevel = ArchipelagoClient.Progress.MaxRestLevel(characterOffset) ?? 0;
            smithLevel = ArchipelagoClient.Progress.MaxSmithLevel(characterOffset) ?? 0;
            checkedLocations = ArchipelagoClient.Progress.CheckedCampfireLocationIds;
            reason = string.Empty;
            return true;
        }

        restLevel = 0;
        smithLevel = 0;
        checkedLocations = new HashSet<long>();
        if (!ApPlayerContextResolver.TryGetRewardProgress(
                player,
                out var progress,
                out reason
            ))
        {
            return false;
        }

        progress.ProgressiveRests.TryGetValue(characterOffset, out restLevel);
        progress.ProgressiveSmiths.TryGetValue(characterOffset, out smithLevel);
        checkedLocations = progress.CheckedCampfireLocationIds;
        return true;
    }

    private static void RemoveOption(ICollection<RestSiteOption> options, string optionId)
    {
        foreach (RestSiteOption option in options.Where(option => option.OptionId == optionId).ToArray())
            options.Remove(option);
    }

    private static void InsertFirst(
        ICollection<RestSiteOption> options,
        RestSiteOption option)
    {
        if (options is List<RestSiteOption> list)
            list.Insert(0, option);
        else
            options.Add(option);
    }
}
