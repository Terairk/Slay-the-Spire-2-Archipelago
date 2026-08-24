using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Utils;

namespace StS2AP.Multiplayer;

/// <summary>
/// Resolves the AP identity that governs one STS player. Reward behavior and character-specific
/// location checks are deliberately separate: AP Guests always inherit the fixed host's settings
/// and receipts, while the shared-slot check scope only controls whether their checks are sent.
/// </summary>
public static class ApPlayerContextResolver
{
    public static bool IsVanillaGuest(Player player) =>
        MultiplayerSupport.IsRealMultiplayerRun
        && TryGetPlayerState(player, out _, out ApPlayerRunState state)
        && state.Participation == ApParticipationKind.VanillaGuest;

    public static bool TryGetRewardSettings(
        Player player,
        out ArchipelagoSettings settings)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
        {
            settings = ArchipelagoClient.Settings;
            return settings != null;
        }

        settings = null!;
        if (!TryGetPlayerState(player, out RunState runState, out ApPlayerRunState state))
            return false;

        if (state.Participation == ApParticipationKind.OwnApSlot
            && state.SlotSettings != null)
        {
            settings = state.SlotSettings;
            return true;
        }

        if (state.Participation == ApParticipationKind.ApGuest
            && ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            && shared.HostSettings != null)
        {
            settings = shared.HostSettings;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the character configuration through the same per-player AP context as rewards.
    /// This must be used instead of the process-global settings during replicated construction:
    /// another own-slot player can have a different character table on this replica.
    /// </summary>
    public static bool TryGetCharacterConfig(
        Player player,
        out CharacterConfig config)
    {
        config = null!;
        if (!TryGetRewardSettings(player, out ArchipelagoSettings settings)
            || !settings.Characters.TryGetValue(
                player.Character.Id.Entry,
                out CharacterConfig? resolved
            )
            || resolved == null)
        {
            return false;
        }

        config = resolved;
        return true;
    }

    public static bool TryGetApCharacterName(Player player, out string characterName)
    {
        characterName = string.Empty;
        if (!TryGetCharacterConfig(player, out CharacterConfig config))
            return false;

        characterName = config.ModNum == 0
            ? config.Name
            : $"Custom Character {config.ModNum}";
        return true;
    }

    public static bool TryGetRewardProgress(
        Player player,
        out ApRunProgressState progress,
        out string reason)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
        {
            progress = ArchipelagoClient.Progress.ToRunProgressState();
            reason = string.Empty;
            return true;
        }

        progress = null!;
        if (!TryGetRewardProgressSource(player, out ApPlayerRunState source, out reason))
            return false;
        if (!source.Progress.Initialized)
        {
            reason = $"AP reward progress for player {player.NetId} is not initialized";
            return false;
        }

        progress = source.Progress;
        return true;
    }

    /// <summary>
    /// Returns whether this player's character-specific checks belong to an AP slot. This says
    /// nothing about which process may write them; <see cref="MultiplayerLocationChecks.IsCheckWriter"/>
    /// remains the mutation authority.
    /// </summary>
    public static bool HasCharacterChecks(Player player)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun)
            return true;
        if (!TryGetPlayerState(player, out RunState runState, out ApPlayerRunState state))
            return false;
        if (state.Participation == ApParticipationKind.OwnApSlot)
            return true;

        return state.Participation == ApParticipationKind.ApGuest
            && ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            && shared.SharedSlotCheckScope == SharedSlotCheckScope.AllApParticipants;
    }

    /// <summary>
    /// Resolves the canonical run-data record whose receipts govern this player. Own-slot players
    /// use their own record; AP Guests use the fixed host's record regardless of shared-check scope.
    /// </summary>
    internal static bool TryGetRewardProgressSource(
        Player player,
        out ApPlayerRunState source,
        out string reason)
    {
        source = null!;
        reason = string.Empty;
        if (!TryGetPlayerState(player, out RunState runState, out ApPlayerRunState state))
        {
            reason = $"no canonical AP run state exists for player {player.NetId}";
            return false;
        }

        if (state.Participation == ApParticipationKind.OwnApSlot)
        {
            source = state;
            return true;
        }

        if (state.Participation != ApParticipationKind.ApGuest)
        {
            reason = $"player {player.NetId} is a Vanilla Guest";
            return false;
        }

        if (!BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out ulong hostNetId
            )
            || !ApRunData.TryGetPlayerState(
                runState,
                hostNetId,
                out ApPlayerRunState hostState
            )
            || hostState.Participation != ApParticipationKind.OwnApSlot)
        {
            reason = $"the fixed host has no AP reward state for player {player.NetId}";
            return false;
        }

        source = hostState;
        return true;
    }

    internal static bool TryGetRewardProgressSourceNetId(
        Player player,
        out ulong sourceNetId)
    {
        sourceNetId = 0;
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !TryGetPlayerState(player, out _, out ApPlayerRunState state))
        {
            return false;
        }

        if (state.Participation == ApParticipationKind.OwnApSlot)
        {
            sourceNetId = player.NetId;
            return true;
        }

        return state.Participation == ApParticipationKind.ApGuest
            && BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out sourceNetId
            );
    }

    private static bool TryGetPlayerState(
        Player player,
        out RunState runState,
        out ApPlayerRunState state)
    {
        runState = null!;
        state = null!;
        if (player.RunState is not RunState playerRunState
            || !ApRunData.TryGetPlayerState(
                playerRunState,
                player.NetId,
                out ApPlayerRunState playerState
            ))
        {
            return false;
        }

        runState = playerRunState;
        state = playerState;
        return true;
    }
}
