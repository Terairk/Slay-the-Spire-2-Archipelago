using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Models;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace StS2AP.Utils;

/// <summary>
/// Owns the AP data embedded in MegaCrit's canonical run snapshot. Lobby methods stage the
/// launch contract; mid-run ledger mutation must only be called from the same host-ordered
/// operation that applies the corresponding shared effect. There are intentionally no
/// Frozen/Validated booleans: launch readiness must be derived from the current contributions,
/// and the committed run snapshot is the lifecycle boundary that makes the mapping immutable.
/// </summary>
public static class ApRunData
{
    private static RunSavedData<ApRunSharedState> _sharedRun = null!;
    private static PlayerRunSavedData<ApPlayerRunState> _players = null!;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        var runStore = RitsuLibFramework.GetRunSavedDataStore(ModEntry.ModId);
        _sharedRun = runStore.Register(
            key: "ap_run",
            defaultFactory: () => new ApRunSharedState(),
            options: new RunSavedDataOptions
            {
                SchemaVersion = 1,
                SyncLobbyOnChange = true,
            }
        );
        _players = runStore.RegisterPerPlayer(
            key: "ap_players",
            defaultFactory: () => new ApPlayerRunState(),
            options: new RunSavedDataOptions
            {
                SchemaVersion = 1,
                SyncLobbyOnChange = true,
            }
        );

        _initialized = true;
        RitsuLibFramework.SubscribeLifecycle<RunSavedDataPreparingEvent>(OnRunDataPreparing);
        RitsuLibFramework.SubscribeLifecycle<RunSavedDataLobbyStagingEvent>(OnLobbyStagingChanged);
    }

    /// <summary>Stages this process's guest/AP identity under its native MegaCrit Net ID.</summary>
    public static void StageLocalPlayer(StartRunLobby lobby)
    {
        if (!_initialized)
            return;

        ulong localNetId = lobby.NetService.NetId;
        ApParticipationKind participation = MultiplayerSupport.PendingParticipation;
        var state = new ApPlayerRunState
        {
            Participation = participation,
            ApRoomSeed = participation == ApParticipationKind.Archipelago
                ? MultiplayerSupport.PreparedApRoomSeed
                : null,
            ApTeamId = participation == ApParticipationKind.Archipelago
                ? MultiplayerSupport.PreparedApTeamId
                : null,
            ApSlotId = participation == ApParticipationKind.Archipelago
                ? MultiplayerSupport.PreparedApSlotId
                : null,
            ApHistoryComplete = participation == ApParticipationKind.Archipelago
                && MultiplayerSupport.InitialItemsLoaded,
        };
        // SyncLobbyOnChange makes this a contribution to the authoritative host staging
        // session. On a client RitsuLib pushes the local PlayerRunSavedData payload with a
        // vanilla character-change message and flushes it again with SetReady(true); the host
        // merges it under sender Net ID before handling that Ready message. On the host the
        // same call merges locally. This is not a host-to-client or mid-run synchronization
        // mechanism.
        if (!_players.Lobby.TryGet(lobby, localNetId, out ApPlayerRunState existing)
            || !HasSameLobbyContribution(existing, state))
        {
            _players.Lobby.Set(lobby, localNetId, state);
        }

        // This stages transport and storage only. The host Ready UI and final launch guard both
        // call TryValidateHostLobbyContributions against the latest active player list.
        if (lobby.NetService.Type == NetGameType.Host)
            EnsureLobbyRunId(lobby);
    }

    public static bool TryGetLocalPlayerState(
        RunState runState,
        ulong localNetId,
        out ApPlayerRunState state)
    {
        if (_initialized)
            return _players.TryGet(runState, localNetId, out state);

        state = null!;
        return false;
    }

    public static ApRunSharedState GetSharedState(RunState runState) => _sharedRun.Get(runState);

    public static bool TryGetSharedState(RunState runState, out ApRunSharedState state)
    {
        if (_initialized)
            return _sharedRun.TryGet(runState, out state);
        state = null!;
        return false;
    }

    public static bool TryGetPlayerState(
        RunState runState,
        ulong netId,
        out ApPlayerRunState state)
    {
        if (_initialized)
            return _players.TryGet(runState, netId, out state);
        state = null!;
        return false;
    }

    public static bool TryGetLobbySharedState(
        StartRunLobby lobby,
        out ApRunSharedState state)
    {
        if (_initialized)
            return _sharedRun.Lobby.TryGet(lobby, out state);
        state = null!;
        return false;
    }

    public static bool TryGetLobbyPlayerState(
        StartRunLobby lobby,
        ulong netId,
        out ApPlayerRunState state)
    {
        if (_initialized)
            return _players.Lobby.TryGet(lobby, netId, out state);
        state = null!;
        return false;
    }

    public static bool HasAppliedEffect(RunState runState, string effectId) =>
        _initialized
        && _sharedRun.TryGet(runState, out ApRunSharedState state)
        && state.AppliedEffectIds.Contains(effectId);

    /// <summary>
    /// Recomputes launch-contribution readiness from the host's current lobby staging. This
    /// deliberately returns no persistent validation token: callers must evaluate the latest
    /// player list and contributions at the actual host launch boundary.
    /// </summary>
    public static bool TryValidateHostLobbyContributions(
        StartRunLobby lobby,
        out string reason)
    {
        if (lobby.NetService.Type != NetGameType.Host)
        {
            reason = "Only the host has the authoritative merged lobby contributions.";
            return false;
        }

        foreach (ulong netId in BetaMainCompatibility.GetLobbyPlayerNetIds(lobby))
        {
            if (!TryGetLobbyPlayerState(lobby, netId, out ApPlayerRunState state))
            {
                reason = $"Player {netId} has not contributed AP lobby state.";
                return false;
            }

            string? blocker = GetLobbyContributionBlocker(state);
            if (blocker != null)
            {
                reason = $"Player {netId}: {blocker}.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    public static string? GetLobbyContributionBlocker(ApPlayerRunState state)
    {
        if (state.Participation == ApParticipationKind.Guest)
            return null;
        if (state.ApRoomSeed == null || state.ApTeamId == null || state.ApSlotId == null)
            return "incomplete-ap-identity";
        return state.ApHistoryComplete ? null : "ap-history-incomplete";
    }

    /// <summary>
    /// Records a replicated effect in the canonical ledger. Callers must invoke this from the
    /// same host-ordered operation that applies the effect; this method is storage, not transport.
    /// </summary>
    public static void RecordAppliedEffectFromOrderedAction(
        RunState runState,
        string effectId)
    {
        if (!_initialized)
            throw new InvalidOperationException("The AP run-data store is not initialized.");
        if (string.IsNullOrWhiteSpace(effectId))
            throw new ArgumentException("Applied effect ID cannot be empty.", nameof(effectId));

        _sharedRun.Modify(runState, state => state.AppliedEffectIds.Add(effectId));
    }

    private static void EnsureLobbyRunId(StartRunLobby lobby)
    {
        if (_sharedRun.Lobby.TryGet(lobby, out ApRunSharedState existing)
            && existing.RunId != Guid.Empty)
        {
            return;
        }

        _sharedRun.Lobby.Modify(lobby, state =>
        {
            if (state.RunId == Guid.Empty)
                state.RunId = Guid.NewGuid();
        });
    }

    private static bool HasSameLobbyContribution(
        ApPlayerRunState left,
        ApPlayerRunState right) =>
        left.SchemaVersion == right.SchemaVersion
        && left.Participation == right.Participation
        && string.Equals(left.ApRoomSeed, right.ApRoomSeed, StringComparison.Ordinal)
        && left.ApTeamId == right.ApTeamId
        && left.ApSlotId == right.ApSlotId
        && left.ApHistoryComplete == right.ApHistoryComplete;

    private static void OnLobbyStagingChanged(RunSavedDataLobbyStagingEvent evt)
    {
        if (evt.IsMultiplayer && evt.IsHost)
            MultiplayerSupport.RequestHostLobbyRefresh(evt.Lobby);
    }

    private static void OnRunDataPreparing(RunSavedDataPreparingEvent evt)
    {
        bool isAuthoritative = !evt.IsMultiplayer
            || RunManager.Instance.NetService.Type == NetGameType.Host;
        if (!isAuthoritative)
        {
            if (!_sharedRun.TryGet(evt.RunState, out ApRunSharedState clientState)
                || clientState.RunId == Guid.Empty)
            {
                LogUtility.Error("AP multiplayer run snapshot arrived without a host RunId");
            }
            return;
        }

        _sharedRun.Modify(evt.RunState, state =>
        {
            if (state.RunId == Guid.Empty)
                state.RunId = Guid.NewGuid();
        });
    }
}
