using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Models;

namespace StS2AP.Utils;

/// <summary>
/// Supplies the host-confirmed, per-Net-ID AP inputs used when MegaCrit constructs one mutable
/// Ancient event per player on every replica. MegaCrit continues to own option-index and relic
/// synchronization; this class only makes the deterministic option transform owner-correct.
/// </summary>
public static class AncientMultiplayer
{
    private sealed record ConfirmedProgress(
        long Revision,
        IReadOnlyDictionary<long, int> ProgressiveAncients
    );

    private static readonly Dictionary<ulong, ConfirmedProgress> ConfirmedByOwner = new();
    private static readonly Dictionary<ulong, ConfirmedProgress> FrozenByOwner = new();
    private static RunState? _runState;
    private static bool _encounterFrozen;

    public static void BindRun(RunState runState)
    {
        EndRun();
        if (!MultiplayerSupport.IsExperimentalMultiplayerRun)
            return;

        _runState = runState;
        foreach (Player player in runState.Players)
        {
            if (!ApPlayerContextResolver.TryGetRewardProgressSource(
                    player,
                    out ApPlayerRunState source,
                    out _
                ))
            {
                continue;
            }

            var counts = source.Progress.Initialized
                ? new Dictionary<long, int>(source.Progress.ProgressiveAncients)
                : new Dictionary<long, int>();
            foreach ((long characterOffset, int initialCount) in
                source.InitialProgressiveAncientsByCharacter)
            {
                counts.TryGetValue(characterOffset, out int checkpointCount);
                counts[characterOffset] = Math.Max(checkpointCount, initialCount);
            }
            ConfirmedByOwner[player.NetId] = new ConfirmedProgress(
                source.ProgressRevision,
                counts
            );
        }
    }

    public static void EndRun()
    {
        _runState = null;
        _encounterFrozen = false;
        ConfirmedByOwner.Clear();
        FrozenByOwner.Clear();
    }

    /// <summary>
    /// Freezes the latest host-confirmed thresholds at the native encounter boundary. A receipt
    /// confirmed after this point deliberately applies to the next applicable Ancient encounter.
    /// </summary>
    public static void BeginEncounter(RunState runState, bool isAncient)
    {
        FrozenByOwner.Clear();
        _encounterFrozen = false;
        if (!isAncient
            || !ReferenceEquals(_runState, runState)
            || !MultiplayerSupport.ShouldRunReplicatedConstruction(
                MultiplayerFeature.Ancients
            ))
        {
            return;
        }

        foreach ((ulong owner, ConfirmedProgress progress) in ConfirmedByOwner)
        {
            FrozenByOwner[owner] = new ConfirmedProgress(
                progress.Revision,
                new Dictionary<long, int>(progress.ProgressiveAncients)
            );
        }
        _encounterFrozen = true;
    }

    /// <summary>
    /// Installs a progress revision only after the fixed host has accepted it. The caller is the
    /// unified progress transport, so no second Ancient-specific network protocol is required.
    /// </summary>
    public static void ConfirmProgress(
        RunState runState,
        ulong ownerNetId,
        long revision,
        ApRunProgressState progress)
    {
        if (!ReferenceEquals(_runState, runState) || !progress.Initialized)
        {
            return;
        }

        // AP Guests consume the fixed host's receipts, so one confirmed host update refreshes the
        // future Ancient snapshot for both the host and every AP Guest. A guest's own progress
        // record is not a reward source and therefore does not affect Ancient availability.
        foreach (Player player in runState.Players)
        {
            if (!ApPlayerContextResolver.TryGetRewardProgressSourceNetId(
                    player,
                    out ulong sourceNetId
                )
                || sourceNetId != ownerNetId
                || (ConfirmedByOwner.TryGetValue(
                    player.NetId,
                    out ConfirmedProgress? existing
                ) && revision < existing.Revision))
            {
                continue;
            }

            ConfirmedByOwner[player.NetId] = new ConfirmedProgress(
                revision,
                new Dictionary<long, int>(progress.ProgressiveAncients)
            );
        }
    }

    public static bool TryGetFrozenContext(
        Player player,
        out ArchipelagoSettings settings,
        out int receivedCount,
        out long characterOffset,
        out string reason)
    {
        settings = null!;
        receivedCount = 0;
        characterOffset = -1;
        reason = string.Empty;
        if (!ApPlayerContextResolver.TryGetRewardSettings(player, out settings))
        {
            reason = $"No frozen AP Ancient settings exist for player {player.NetId}.";
            return false;
        }
        if (!_encounterFrozen
            || !FrozenByOwner.TryGetValue(player.NetId, out ConfirmedProgress? progress))
        {
            reason = $"No host-confirmed Ancient progress was frozen for player {player.NetId}.";
            return false;
        }
        if (!settings.Characters.TryGetValue(
                player.Character.Id.Entry,
                out CharacterConfig? config
            ))
        {
            reason = $"No AP character mapping exists for {player.Character.Id.Entry}.";
            return false;
        }

        characterOffset = config.CharOffset;
        progress.ProgressiveAncients.TryGetValue(characterOffset, out receivedCount);
        if (characterOffset > 0)
            return true;

        reason = $"Character {player.Character.Id.Entry} has an invalid AP offset.";
        return false;
    }

}
