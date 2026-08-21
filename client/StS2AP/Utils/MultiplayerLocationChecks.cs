using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Extensions;
using StS2AP.Models;

namespace StS2AP.Utils;

/// <summary>
/// Resolves the AP slot which owns a player's location checks. Own-slot players use their
/// staged settings and local AP connection; AP Guests use the fixed host's frozen settings and
/// check writer. Vanilla guests keep native behavior.
/// </summary>
public static class MultiplayerLocationChecks
{
    public static bool TryGetSettings(Player player, out ArchipelagoSettings settings)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
        {
            settings = ArchipelagoClient.Settings;
            return settings != null;
        }

        if (player.RunState is not RunState runState
            || !ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state))
        {
            settings = null!;
            return false;
        }

        switch (state.Participation)
        {
            case ApParticipationKind.OwnApSlot when state.SlotSettings != null:
                settings = state.SlotSettings;
                return true;
            case ApParticipationKind.ApGuest:
                if (ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
                    && shared.SharedSlotCheckScope == SharedSlotCheckScope.AllApParticipants
                    && shared.HostSettings != null)
                {
                    settings = shared.HostSettings;
                    return true;
                }
                break;
        }

        settings = null!;
        return false;
    }

    public static bool IsLocalProgressOwner(Player player)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
            return player == GameUtility.CurrentPlayer;

        try
        {
            return LocalContext.IsMe(player);
        }
        catch
        {
            return false;
        }
    }

    public static void PublishLocalProgress(Player player)
    {
        if (IsLocalProgressOwner(player))
            ApRunData.PublishLocalProgress(player);
    }

    public static int IncrementCardRewards(Player player) =>
        IncrementCounter(player, Counter.Card);

    public static int IncrementRareCardRewards(Player player) =>
        IncrementCounter(player, Counter.RareCard);

    public static int IncrementGoldRewards(Player player) =>
        IncrementCounter(player, Counter.Gold);

    public static int IncrementPotionRewards(Player player) =>
        IncrementCounter(player, Counter.Potion);

    public static int GetRelicRewardsAttempted(Player player)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun || IsLocalProgressOwner(player))
            return ArchipelagoClient.Progress.RelicRewardsAttempted;
        return TryGetRemoteProgress(player, out ApRunProgressState progress)
            ? progress.RelicRewardsAttempted
            : int.MaxValue;
    }

    private static int IncrementCounter(Player player, Counter counter)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun || IsLocalProgressOwner(player))
        {
            return counter switch
            {
                Counter.Card => ++ArchipelagoClient.Progress.CardRewardsAttempted,
                Counter.RareCard => ++ArchipelagoClient.Progress.RareCardRewardsAttempted,
                Counter.Gold => ++ArchipelagoClient.Progress.GoldRewardsAttempted,
                Counter.Potion => ++ArchipelagoClient.Progress.PotionRewardsAttempted,
                _ => throw new ArgumentOutOfRangeException(nameof(counter)),
            };
        }

        if (!TryGetRemoteProgress(player, out ApRunProgressState progress))
            return int.MaxValue;
        return counter switch
        {
            Counter.Card => ++progress.CardRewardsAttempted,
            Counter.RareCard => ++progress.RareCardRewardsAttempted,
            Counter.Gold => ++progress.GoldRewardsAttempted,
            Counter.Potion => ++progress.PotionRewardsAttempted,
            _ => throw new ArgumentOutOfRangeException(nameof(counter)),
        };
    }

    internal static bool TryGetRemoteProgress(Player player, out ApRunProgressState progress)
    {
        if (player.RunState is RunState runState
            && ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state)
            && state.Participation != ApParticipationKind.VanillaGuest)
        {
            progress = state.Progress;
            return true;
        }

        progress = null!;
        return false;
    }

    internal static IReadOnlyList<int> GetReplicatedRelicReceiptIndexes(
        Player player,
        ApRunProgressState progress)
    {
        long? characterOffset = player.Character.GetCharacterOffset();
        if (!characterOffset.HasValue)
            return Array.Empty<int>();

        IEnumerable<int> indexes = progress.RelicReceiptIndexesByCharacter.TryGetValue(
            characterOffset.Value,
            out List<int>? liveIndexes)
                ? liveIndexes
                : Array.Empty<int>();
        if (player.RunState is RunState runState
            && ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state)
            && state.InitialRelicReceiptIndexesByCharacter.TryGetValue(
                characterOffset.Value,
                out List<int>? initialIndexes))
        {
            indexes = indexes.Concat(initialIndexes);
        }
        return indexes.Distinct().Order().ToArray();
    }

    public static bool IsCheckWriter(Player player)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
            return player == GameUtility.CurrentPlayer;
        if (player.RunState is not RunState runState
            || !ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state))
        {
            return false;
        }

        if (state.Participation == ApParticipationKind.OwnApSlot)
            return MultiplayerSupport.IsLocalOwnApSlot && IsLocalProgressOwner(player);

        return state.Participation == ApParticipationKind.ApGuest
            && MultiplayerSupport.IsLocalOwnApSlot
            && RunManager.Instance.NetService.Type == NetGameType.Host
            && ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            && shared.SharedSlotCheckScope == SharedSlotCheckScope.AllApParticipants;
    }

    public static long ResolveLocationId(Player player, string locationName)
    {
        if (!IsCheckWriter(player))
            return -1;

        try
        {
            return ArchipelagoClient.Session.Locations.GetLocationIdFromName(
                "Slay the Spire II",
                locationName
            );
        }
        catch (Exception ex)
        {
            LogUtility.Warn($"Could not resolve AP location '{locationName}': {ex.Message}");
            return -1;
        }
    }

    public static bool IsChecked(Player player, long locationId) =>
        locationId != -1
        && IsCheckWriter(player)
        && ArchipelagoClient.CheckedLocations.Contains(locationId);

    /// <summary>
    /// Records the exact location in the owning AP slot's existing durable multiplayer outbox.
    /// Non-writer replicas still complete the native reward selection.
    /// </summary>
    public static bool QueueCheck(Player player, string locationName, long locationId = -1)
    {
        if (!IsCheckWriter(player))
            return false;

        if (locationId == -1)
            locationId = ResolveLocationId(player, locationName);
        if (locationId == -1)
        {
            LogUtility.Warn($"Location '{locationName}' not found in the owning Archipelago slot");
            return false;
        }
        if (ArchipelagoClient.CheckedLocations.Contains(locationId))
            return false;

        ArchipelagoClient.CheckedLocations.Add(locationId);
        PendingCheckUtility.RecordAndSend(locationId);
        LogUtility.Success($"Recorded location check: {locationName} ({locationId})");
        return true;
    }

    private enum Counter
    {
        Card,
        RareCard,
        Gold,
        Potion,
    }
}
