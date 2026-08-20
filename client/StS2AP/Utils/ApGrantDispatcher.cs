using Archipelago.MultiClient.Net.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Data;
using StS2AP.Extensions;
using StS2AP.Models;
using static StS2AP.Data.ItemTable;

namespace StS2AP.Utils;

// EXPLAIN: this entire file to me and why is the path for gold so different than singleplayer

/// <summary>
/// Routes AP-owned causes to their concrete game effects. The first supported route is
/// aggregate gold, which deliberately uses a cumulative raw cursor instead of discrete
/// receipt IDs and delegates replication to MegaCrit's RewardSynchronizer.
/// </summary>
public static class ApGrantDispatcher
{
    private static readonly object GoldClaimLock = new();

    private static bool _goldClaimInFlight;
    private static long? _activeCharacterOffset;

    /// <summary>Rebuilds the raw per-character bank from authoritative AP history.</summary>
    public static void RebuildGoldBank(IReadOnlyList<ItemInfo> receivedItems)
    {
        var rebuilt = new Dictionary<long, int>();
        foreach (ItemInfo item in receivedItems)
        {
            if (item.ItemId < 10000)
                continue;

            APItem itemId = item.GetCharacterSpecificItemID();
            if (!ItemTable.GoldItemAmounts.TryGetValue(itemId, out int amount))
                continue;

            long characterOffset = item.GetCharacterOffset();
            rebuilt.TryGetValue(characterOffset, out int previous);
            rebuilt[characterOffset] = previous + amount;
        }

        ArchipelagoClient.Progress.GoldReceived = rebuilt;
        LogUtility.Info(
            $"Rebuilt AP gold bank from {receivedItems.Count} receipt(s): "
                + string.Join(",", rebuilt.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}={pair.Value}"))
        );
    }

    /// <summary>Binds the host-owned per-player cursor to the launched local STS run.</summary>
    public static bool BeginRun(RunState runState, long characterOffset, out string reason)
    {
        reason = string.Empty;
        if (MultiplayerSupport.PreparedApRoomSeed is not { } roomSeed
            || MultiplayerSupport.PreparedApTeamId is not { } apTeamId
            || MultiplayerSupport.PreparedApSlotId is not { } apSlotId)
        {
            reason = "The AP owner identity was not prepared before the run launched.";
            return false;
        }

        int redeemedRaw = ArchipelagoClient.Progress.GoldRedeemed;
        ArchipelagoClient.Progress.GoldReceived.TryGetValue(characterOffset, out int receivedRaw);
        if (redeemedRaw < 0 || redeemedRaw > receivedRaw)
        {
            LogUtility.Warn(
                $"Clamping invalid persisted AP gold cursor {redeemedRaw} to raw bank {receivedRaw}"
            );
            redeemedRaw = Math.Clamp(redeemedRaw, 0, receivedRaw);
        }

        _activeCharacterOffset = characterOffset;
        ArchipelagoClient.Progress.GoldRedeemed = redeemedRaw;
        LogUtility.Info(
            $"Bound aggregate AP gold cursor: room={roomSeed}, team={apTeamId}, slot={apSlotId}, "
                + $"character={characterOffset}, "
                + $"redeemedRaw={redeemedRaw}, receivedRaw={receivedRaw}"
        );
        return true;
    }

    /// <summary>Materializes one immutable aggregate claim for the current reward-menu row.</summary>
    public static ApGoldClaim? MaterializeGoldClaim()
    {
        ArchipelagoGoldOffer offer = ArchipelagoClient.Progress.PrepareGoldOffer();
        if (offer.SourceAmount <= 0 || offer.GrantedAmount <= 0)
            return null;

        return new ApGoldClaim(
            offer.SourceAmount,
            offer.GrantedAmount,
            ArchipelagoClient.Progress.GoldRedeemed + offer.SourceAmount
        );
    }

    /// <summary>
    /// Applies and synchronizes the immutable claim, then advances and persists the raw cursor.
    /// Crash recovery between those systems is intentionally unresolved; this method never
    /// speculatively rolls back or automatically replays an already-applied wallet mutation.
    /// </summary>
    public static async Task<bool> ExecuteGoldClaim(ApGoldClaim claim)
    {
        lock (GoldClaimLock)
        {
            if (_goldClaimInFlight)
            {
                LogUtility.Warn("Ignoring aggregate AP gold claim while another claim is active");
                return false;
            }
            _goldClaimInFlight = true;
        }

        try
        {
            if (!MultiplayerSupport.CanClaimGold(out string blockedReason))
            {
                LogUtility.Warn($"AP gold claim blocked: {blockedReason}");
                return false;
            }

            if (!_activeCharacterOffset.HasValue)
            {
                LogUtility.Error("Cannot execute aggregate AP gold: no run-owned cursor binding");
                return false;
            }

            int expectedBefore = claim.RedeemedRawAfter - claim.SourceAmount;
            if (claim.SourceAmount <= 0
                || claim.GrantedAmount <= 0
                || ArchipelagoClient.Progress.GoldRedeemed != expectedBefore
                || ArchipelagoClient.Progress.GoldRemaining < claim.SourceAmount)
            {
                LogUtility.Warn(
                    $"Refusing stale aggregate AP gold claim: source={claim.SourceAmount}, "
                        + $"granted={claim.GrantedAmount}, expectedCursor={expectedBefore}, "
                        + $"actualCursor={ArchipelagoClient.Progress.GoldRedeemed}, "
                        + $"remaining={ArchipelagoClient.Progress.GoldRemaining}"
                );
                return false;
            }

            bool granted = await GameUtility.GrantGold(claim.GrantedAmount);
            if (!granted)
                return false;

            ArchipelagoClient.Progress.GoldRedeemed = claim.RedeemedRawAfter;
            Player? player = GameUtility.CurrentPlayer;
            if (player == null || !ApRunData.PublishLocalProgress(player))
            {
                MultiplayerSupport.InvalidateRunClaims(
                    "the aggregate AP gold cursor could not be published after applying gold"
                );
            }

            LogUtility.Info(
                $"Aggregate AP gold committed: source={claim.SourceAmount}, "
                    + $"granted={claim.GrantedAmount}, redeemedRawAfter={claim.RedeemedRawAfter}"
            );
            return true;
        }
        finally
        {
            lock (GoldClaimLock)
                _goldClaimInFlight = false;
        }
    }

    public static void EndRun()
    {
        _activeCharacterOffset = null;
        lock (GoldClaimLock)
            _goldClaimInFlight = false;
    }
}
