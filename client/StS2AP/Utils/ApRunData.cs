using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Extensions;
using StS2AP.Models;
using STS2RitsuLib;
using STS2RitsuLib.Networking.Sidecar;
using STS2RitsuLib.RunData;

namespace StS2AP.Utils;

/// <summary>
/// Owns the AP data embedded in MegaCrit's canonical run snapshot. Lobby methods stage the
/// launch contract; mid-run snapshots are published immediately after local AP mutations and
/// are durable only when the fixed host writes the next native checkpoint. There are no
/// Frozen/Validated booleans: launch readiness must be derived from the current contributions,
/// and the committed run snapshot is the lifecycle boundary that makes the mapping immutable.
/// </summary>
public static class ApRunData
{
    private const int RunSchemaVersion = 2;
    private const string ProgressMessageKey = "player_ap_progress_v1";
    private static RunSavedData<ApRunSharedState> _sharedRun = null!;
    private static PlayerRunSavedData<ApPlayerRunState> _players = null!;
    private static readonly RitsuLibSidecarJsonSerializer<ApProgressUpdateMessage>
        ProgressSerializer = new();
    private static readonly RitsuLibSidecarMessageDescriptor<ApProgressUpdateMessage>
        ProgressDescriptor = new(
            ModEntry.ModId,
            ProgressMessageKey,
            ProgressSerializer.Serialize,
            ProgressSerializer.Deserialize,
            Required: true
        );
    private static IDisposable? _progressSubscription;
    private static bool _initialized;
    private static long _localProgressRevision;

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
                SchemaVersion = RunSchemaVersion,
                SyncLobbyOnChange = true,
            }
        );
        _players = runStore.RegisterPerPlayer(
            key: "ap_players",
            defaultFactory: () => new ApPlayerRunState(),
            options: new RunSavedDataOptions
            {
                SchemaVersion = RunSchemaVersion,
                SyncLobbyOnChange = true,
            }
        );

        _initialized = true;
        _progressSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(
            ProgressDescriptor,
            OnProgressUpdateReceived
        );
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
        _players.Lobby.TryGet(lobby, localNetId, out ApPlayerRunState? existing);
        var state = new ApPlayerRunState
        {
            Participation = participation,
            ApRoomSeed = participation == ApParticipationKind.OwnApSlot
                ? MultiplayerSupport.PreparedApRoomSeed
                : null,
            ApTeamId = participation == ApParticipationKind.OwnApSlot
                ? MultiplayerSupport.PreparedApTeamId
                : null,
            ApSlotId = participation == ApParticipationKind.OwnApSlot
                ? MultiplayerSupport.PreparedApSlotId
                : null,
            ReceiptSourceReady = participation switch
            {
                ApParticipationKind.OwnApSlot => MultiplayerSupport.InitialItemsLoaded,
                ApParticipationKind.ApGuest => MultiplayerSupport.HostReceiptCatalogReady,
                _ => true,
            },
            Progress = existing?.Progress ?? new APProgressUnified(),
            ProgressRevision = existing?.ProgressRevision ?? 0,
        };
        // SyncLobbyOnChange makes this a contribution to the authoritative host staging
        // session. On a client RitsuLib pushes the local PlayerRunSavedData payload with a
        // vanilla character-change message and flushes it again with SetReady(true); the host
        // merges it under sender Net ID before handling that Ready message. On the host the
        // same call merges locally. This is not a host-to-client or mid-run synchronization
        // mechanism.
        if (existing == null || !HasSameLobbyContribution(existing, state))
        {
            _players.Lobby.Set(lobby, localNetId, state);
        }

        // This stages transport and storage only. The host Ready UI and final launch guard both
        // call TryValidateHostLobbyContributions against the latest active player list.
        if (lobby.NetService.Type == NetGameType.Host)
        {
            EnsureLobbyRunId(lobby);
            StageHostSettings(lobby, participation);
            ApReceiptRelay.PublishLobbySnapshot(lobby);
        }
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

        bool hasApGuest = BetaMainCompatibility.GetLobbyPlayerNetIds(lobby).Any(netId =>
            TryGetLobbyPlayerState(lobby, netId, out ApPlayerRunState participant)
            && participant.Participation == ApParticipationKind.ApGuest
        );
        if (hasApGuest)
        {
            ulong hostNetId = lobby.NetService.NetId;
            if (!TryGetLobbyPlayerState(lobby, hostNetId, out ApPlayerRunState host)
                || host.Participation != ApParticipationKind.OwnApSlot
                || !host.ReceiptSourceReady)
            {
                reason = "AP Guests require the fixed STS host to have a prepared AP slot.";
                return false;
            }
            if (!TryGetLobbySharedState(lobby, out ApRunSharedState shared)
                || shared.HostSettings == null)
            {
                reason = "The host's AP settings have not been frozen into lobby run data.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    public static string? GetLobbyContributionBlocker(ApPlayerRunState state)
    {
        if (state.Participation == ApParticipationKind.VanillaGuest)
            return null;
        if (state.Participation == ApParticipationKind.ApGuest)
            return state.ReceiptSourceReady ? null : "host-receipt-catalog-incomplete";
        if (state.ApRoomSeed == null || state.ApTeamId == null || state.ApSlotId == null)
            return "incomplete-ap-identity";
        return state.ReceiptSourceReady ? null : "ap-history-incomplete";
    }

    // TODO: needs comments
    public static bool RestoreLocalProgress(Player player)
    {
        if (!_initialized)
            return false;
        if (player.RunState is not RunState runState
            || !_players.TryGet(runState, player.NetId, out ApPlayerRunState state))
            return false;

        _localProgressRevision = state.ProgressRevision;
        if (!state.Progress.Initialized)
            return false;

        ArchipelagoClient.Progress = ArchipelagoProgress.FromUnified(state.Progress, player);
        return true;
    }

    // TODO: Needs comments
    public static bool PublishLocalProgress(Player player)
    {
        if (!_initialized || !MultiplayerSupport.IsRealMultiplayerRun)
            return true;
        if (player.RunState is not RunState runState
            || !_players.TryGet(runState, player.NetId, out ApPlayerRunState state)
            || state.Participation == ApParticipationKind.VanillaGuest)
        {
            return false;
        }

        APProgressUnified snapshot = ArchipelagoClient.Progress.ToUnified();
        long revision = Math.Max(_localProgressRevision, state.ProgressRevision) + 1;
        _localProgressRevision = revision;
        _players.Set(runState, player.NetId, new ApPlayerRunState
        {
            SchemaVersion = state.SchemaVersion,
            Participation = state.Participation,
            ApRoomSeed = state.ApRoomSeed,
            ApTeamId = state.ApTeamId,
            ApSlotId = state.ApSlotId,
            ReceiptSourceReady = state.ReceiptSourceReady,
            Progress = snapshot,
            ProgressRevision = revision,
        });

        if (RunManager.Instance.NetService.Type == NetGameType.Host)
            return true;
        if (!_sharedRun.TryGet(runState, out ApRunSharedState shared))
            return false;

        return RitsuLibSidecarTypedMessageRegistry.SendToHost(
            RunManager.Instance,
            ProgressDescriptor,
            new ApProgressUpdateMessage
            {
                RunId = shared.RunId,
                OwnerNetId = player.NetId,
                Revision = revision,
                Progress = snapshot,
            }
        );
    }

    // EXPLAIN: difference from the other progress function
    public static bool PublishCurrentProgress() =>
        GameUtility.CurrentPlayer is not { } player || PublishLocalProgress(player);

    public static bool IsReceiptUsed(RunState runState, ulong netId, int receivedItemIndex) =>
        _players.TryGet(runState, netId, out ApPlayerRunState state)
        && state.Progress.UsedItems.Contains(receivedItemIndex);

    public static void CaptureLocalHostProgressBeforeSave()
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
            return;
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        Player? localPlayer = runState?.GetPlayer(RunManager.Instance.NetService.NetId);
        if (localPlayer != null)
            PublishLocalProgress(localPlayer);
    }

    public static void SendSharedSlotPressStartChecks(RunState runState)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host
            || !TryGetSharedState(runState, out ApRunSharedState shared))
        {
            return;
        }

        ulong hostNetId = RunManager.Instance.NetService.NetId;
        foreach (Player player in runState.Players)
        {
            if (!TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state))
                continue;
            bool usesHostSlot = (player.NetId == hostNetId
                    && state.Participation == ApParticipationKind.OwnApSlot)
                || state.Participation == ApParticipationKind.ApGuest;
            if (!usesHostSlot)
                continue;
            if (player.NetId != hostNetId
                && shared.SharedSlotCheckScope != SharedSlotCheckScope.AllApParticipants)
            {
                continue;
            }
            GameUtility.TrySendPressStartCheckFor(
                player.Character,
                includeUnrecognizedCharacters: false
            );
        }
    }

    // TOOD: say why this is needed
    public static IReadOnlyList<long> GetSharedSlotApGuestCharacterOffsets(RunState runState)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host
            || !TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.SharedSlotCheckScope != SharedSlotCheckScope.AllApParticipants)
        {
            return Array.Empty<long>();
        }

        return runState.Players
            .Where(player => TryGetPlayerState(
                    runState,
                    player.NetId,
                    out ApPlayerRunState state
                ) && state.Participation == ApParticipationKind.ApGuest)
            .Select(player => player.Character.GetCharacterOffset())
            .Where(offset => offset.HasValue)
            .Select(offset => offset!.Value)
            .Distinct()
            .ToArray();
    }

    // TODO: this should have comments on why this is needed
    private static void OnProgressUpdateReceived(
        RitsuLibSidecarTypedDispatchContext<ApProgressUpdateMessage> context)
    {
        if (context.Message.SchemaVersion != 1
            || context.SenderNetId != context.Message.OwnerNetId)
        {
            LogUtility.Error("Rejected incompatible or incorrectly owned AP progress update.");
            return;
        }

        RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
        {
            if (RunManager.Instance.NetService.Type != NetGameType.Host)
                return;
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null
                || !_sharedRun.TryGet(runState, out ApRunSharedState shared)
                || shared.RunId != context.Message.RunId
                || !_players.TryGet(runState, context.Message.OwnerNetId, out ApPlayerRunState state)
                || state.Participation == ApParticipationKind.VanillaGuest
                || context.Message.Revision <= state.ProgressRevision)
            {
                return;
            }

            state.Progress = context.Message.Progress;
            state.ProgressRevision = context.Message.Revision;
            _players.Set(runState, context.Message.OwnerNetId, state);
        });
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

    private static void StageHostSettings(
        StartRunLobby lobby,
        ApParticipationKind hostParticipation)
    {
        _sharedRun.Lobby.Modify(lobby, state =>
        {
            state.SchemaVersion = RunSchemaVersion;
            state.SharedSlotCheckScope = MultiplayerSupport.ConfiguredSharedSlotCheckScope;
            state.HostSettings = hostParticipation == ApParticipationKind.OwnApSlot
                ? MultiplayerSupport.CreateEffectiveHostSettingsSnapshot()
                : null;
        });
    }

    // TODO: state why this is needed
    private static bool HasSameLobbyContribution(
        ApPlayerRunState left,
        ApPlayerRunState right) =>
        left.SchemaVersion == right.SchemaVersion
        && left.Participation == right.Participation
        && string.Equals(left.ApRoomSeed, right.ApRoomSeed, StringComparison.Ordinal)
        && left.ApTeamId == right.ApTeamId
        && left.ApSlotId == right.ApSlotId
        && left.ReceiptSourceReady == right.ReceiptSourceReady;

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
