using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace StS2AP.Utils;

/// <summary>
/// Supplies the per-Net-ID AP inputs used when MegaCrit constructs one mutable Ancient event per
/// player on every replica. MegaCrit continues to own option-index and relic synchronization; this
/// class only makes the deterministic option transform owner-correct. The counts come from
/// <see cref="RoomInputsMultiplayer"/>, which every replica updates at the same point in the
/// action queue.
/// </summary>
public static class AncientMultiplayer
{
    private static readonly Dictionary<ulong, IReadOnlyDictionary<long, int>> FrozenByOwner = new();
    private static RunState? _runState;
    private static bool _encounterFrozen;

    public static void BindRun(RunState runState)
    {
        EndRun();
        if (!MultiplayerSupport.IsExperimentalMultiplayerRun)
            return;

        _runState = runState;
    }

    public static void EndRun()
    {
        _runState = null;
        _encounterFrozen = false;
        FrozenByOwner.Clear();
    }

    /// <summary>
    /// Freezes the agreed Progressive Ancient counts at the native encounter boundary, so the
    /// whole event uses one set even if new room inputs arrive while it is open.
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

        foreach (Player player in runState.Players)
        {
            if (RoomInputsMultiplayer.TryGetProgressiveAncients(
                    player.NetId,
                    out Dictionary<long, int> counts
                ))
            {
                FrozenByOwner[player.NetId] = counts;
            }
        }
        _encounterFrozen = true;
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
            || !FrozenByOwner.TryGetValue(
                player.NetId,
                out IReadOnlyDictionary<long, int>? progressiveAncients
            ))
        {
            reason = $"No agreed Ancient progress was frozen for player {player.NetId}.";
            return false;
        }
        if (!settings.TryGetCharacterConfig(
                player.Character.Id.Entry,
                out CharacterConfig? config
            ))
        {
            reason = $"No AP character mapping exists for {player.Character.Id.Entry}.";
            return false;
        }

        characterOffset = config.CharOffset;
        progressiveAncients.TryGetValue(characterOffset, out receivedCount);
        if (characterOffset > 0)
            return true;

        reason = $"Character {player.Character.Id.Entry} has an invalid AP offset.";
        return false;
    }

}
