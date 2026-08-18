using Archipelago.MultiClient.Net.Models;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Data;
using StS2AP.Extensions;
using StS2AP.Models;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using static StS2AP.Data.ItemTable;

namespace StS2AP.Utils;

/// <summary>
/// Routes AP-owned causes to their concrete game effects. The first supported route is
/// aggregate gold, which deliberately uses a cumulative raw cursor instead of discrete
/// receipt IDs and delegates replication to MegaCrit's RewardSynchronizer.
/// </summary>
public static class ApGrantDispatcher
{
    private const string GoldStoreKey = "multiplayer_gold";
    private static readonly object GoldClaimLock = new();
    private static readonly ModDataStoreCache<MultiplayerGoldState> GoldStore =
        RitsuLibFramework.GetDataStore(ModEntry.ModId)
            .CreateCache<MultiplayerGoldState>(GoldStoreKey);

    private static bool _goldClaimInFlight;
    private static string? _activeRunIdentity;
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

    /// <summary>Binds the owner-private cursor to the newly launched local STS run.</summary>
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

        string runIdentity;
        MultiplayerGoldRunState? persisted;
        try
        {
            long startTime = RunManager.Instance.ToSave(preFinishedRoom: null).StartTime;
            runIdentity = $"{runState.Rng.StringSeed}:{startTime}";
            persisted = GoldStore.Value.Runs.LastOrDefault(entry =>
                entry.ApRoomSeed == roomSeed
                && entry.ApTeamId == apTeamId
                && entry.ApSlotId == apSlotId
                && entry.StsRunIdentity == runIdentity
            );
        }
        catch (Exception ex)
        {
            reason = $"The owner-private AP gold cursor could not be loaded: {ex.Message}";
            LogUtility.Error(reason);
            return false;
        }

        int redeemedRaw = 0;
        if (persisted != null)
            persisted.RedeemedRawByCharacter.TryGetValue(characterOffset, out redeemedRaw);

        ArchipelagoClient.Progress.GoldReceived.TryGetValue(characterOffset, out int receivedRaw);
        if (redeemedRaw < 0 || redeemedRaw > receivedRaw)
        {
            LogUtility.Warn(
                $"Clamping invalid persisted AP gold cursor {redeemedRaw} to raw bank {receivedRaw}"
            );
            redeemedRaw = Math.Clamp(redeemedRaw, 0, receivedRaw);
        }

        _activeRunIdentity = runIdentity;
        _activeCharacterOffset = characterOffset;
        ArchipelagoClient.Progress.GoldRedeemed = redeemedRaw;
        LogUtility.Info(
            $"Bound aggregate AP gold cursor: run={runIdentity}, character={characterOffset}, "
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

            if (_activeRunIdentity == null || !_activeCharacterOffset.HasValue)
            {
                LogUtility.Error("Cannot execute aggregate AP gold: no owner-private run binding");
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
            if (!PersistActiveCursor(claim.RedeemedRawAfter))
            {
                MultiplayerSupport.InvalidateRunClaims(
                    "the aggregate AP gold cursor could not be persisted after applying gold"
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

    private static bool PersistActiveCursor(int redeemedRaw)
    {
        if (_activeRunIdentity == null
            || !_activeCharacterOffset.HasValue
            || MultiplayerSupport.PreparedApRoomSeed is not { } roomSeed
            || MultiplayerSupport.PreparedApTeamId is not { } apTeamId
            || MultiplayerSupport.PreparedApSlotId is not { } apSlotId)
        {
            return false;
        }

        try
        {
            GoldStore.Modify(document =>
            {
                MultiplayerGoldRunState? run = document.Runs.LastOrDefault(entry =>
                    entry.ApRoomSeed == roomSeed
                    && entry.ApTeamId == apTeamId
                    && entry.ApSlotId == apSlotId
                    && entry.StsRunIdentity == _activeRunIdentity
                );
                if (run == null)
                {
                    run = new MultiplayerGoldRunState
                    {
                        ApRoomSeed = roomSeed,
                        ApTeamId = apTeamId,
                        ApSlotId = apSlotId,
                        StsRunIdentity = _activeRunIdentity,
                    };
                    document.Runs.Add(run);
                }

                run.RedeemedRawByCharacter[_activeCharacterOffset.Value] = redeemedRaw;
            });
            GoldStore.Save();
            return true;
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Failed to persist aggregate AP gold cursor: {ex}");
            return false;
        }
    }

    public static void EndRun()
    {
        _activeRunIdentity = null;
        _activeCharacterOffset = null;
        lock (GoldClaimLock)
            _goldClaimInFlight = false;
    }
}
