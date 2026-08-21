using Archipelago.MultiClient.Net.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Models;
using STS2RitsuLib.Networking.Sidecar;

namespace StS2AP.Utils;

/// <summary>
/// Relays each AP owner's campfire inputs through the fixed host, then verifies that every peer
/// constructed the same dense native option lists before enabling the rest-site UI.
/// </summary>
public static class RestSiteMultiplayer
{
    private const string StateMessageKey = "rest_site_state_v1";
    private const string ManifestMessageKey = "rest_site_manifest_v1";

    private static readonly RitsuLibSidecarJsonSerializer<ApRestSiteStateMessage>
        StateSerializer = new();
    private static readonly RitsuLibSidecarMessageDescriptor<ApRestSiteStateMessage>
        StateDescriptor = new(
            ModEntry.ModId,
            StateMessageKey,
            StateSerializer.Serialize,
            StateSerializer.Deserialize,
            Required: true
        );
    private static readonly RitsuLibSidecarJsonSerializer<ApRestSiteManifestMessage>
        ManifestSerializer = new();
    private static readonly RitsuLibSidecarSyncMessageDescriptor<ApRestSiteManifestMessage>
        ManifestDescriptor = new(
            ModEntry.ModId,
            ManifestMessageKey,
            ManifestSerializer.Serialize,
            ManifestSerializer.Deserialize,
            HandleManifest,
            LocationTargeted: true,
            ShouldBuffer: true,
            Mode: NetTransferMode.Reliable,
            FailurePolicy: RitsuLibSidecarSyncFailurePolicy.Required,
            BroadcastScope: RitsuLibSidecarSyncBroadcastScope.ReadyPeers,
            DispatchLocalOnBroadcast: false,
            LogLevel: LogLevel.Debug,
            ShouldBroadcast: true
        );

    private static readonly Dictionary<ulong, ApRestSiteState> States = new();
    private static readonly Dictionary<ulong, ApRestSiteState> FrozenStates = new();
    private static readonly Dictionary<ulong, ApRestSiteManifestMessage> Manifests = new();

    private static IDisposable? _stateSubscription;
    private static RunState? _runState;
    private static RunLobby? _runLobby;
    private static Guid _runId;
    private static string? _visitId;
    private static string? _constructionFailure;
    private static ManifestStatus _manifestStatus;
    private static bool _failureNoticeShown;
    private static int _refreshQueued;

    private enum ManifestStatus
    {
        Disabled,
        Waiting,
        Approved,
        Failed,
    }

    public static void Initialize()
    {
        if (_stateSubscription != null)
            return;
        _stateSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(
            StateDescriptor,
            OnStateReceived
        );
        RitsuLibSidecarSyncMessages.Register(ManifestDescriptor);
    }

    public static void BindRun(RunState runState)
    {
        EndRun();
        if (!MultiplayerSupport.IsExperimentalMultiplayerRun
            || !ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.RunId == Guid.Empty)
        {
            return;
        }

        _runState = runState;
        _runId = shared.RunId;
        _runLobby = RunManager.Instance.RunLobby;
        if (_runLobby != null)
            _runLobby.RemotePlayerDisconnected += OnRemotePlayerDisconnected;

        foreach (Player player in runState.Players)
        {
            if (ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state)
                && state.RestSiteState != null)
            {
                States[player.NetId] = state.RestSiteState;
            }
        }
    }

    public static void EndRun()
    {
        if (_runLobby != null)
            _runLobby.RemotePlayerDisconnected -= OnRemotePlayerDisconnected;
        _runState = null;
        _runLobby = null;
        _runId = Guid.Empty;
        _visitId = null;
        _constructionFailure = null;
        _manifestStatus = ManifestStatus.Disabled;
        _failureNoticeShown = false;
        Interlocked.Exchange(ref _refreshQueued, 0);
        States.Clear();
        FrozenStates.Clear();
        Manifests.Clear();
    }

    /// <summary>Coalesces AP callbacks and moves their scene/run-data work to the main thread.</summary>
    public static void QueueRelevantStateRefresh()
    {
        if (_runState == null || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;

        if (!RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                PublishRelevantStates();
            }))
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            LogUtility.Error("Could not schedule the AP rest-site state refresh.");
        }
    }

    /// <summary>Publishes the local own-slot state and any AP Guest states owned by this host.</summary>
    public static void PublishRelevantStates()
    {
        if (_runState == null
            || !MultiplayerSupport.IsFeatureEnabled(MultiplayerFeature.RestSites)
            || GameUtility.CurrentPlayer is not Player localPlayer)
        {
            return;
        }

        if (MultiplayerSupport.IsLocalOwnApSlot && UsesCampfireSanity(localPlayer))
        {
            if (TryBuildState(localPlayer, out ApRestSiteState localState, out string error))
                PublishState(localPlayer, localState);
            else
                LogUtility.Error($"Could not publish local AP rest-site state: {error}");
        }

        if (RunManager.Instance.NetService.Type != NetGameType.Host
            || !MultiplayerSupport.IsLocalOwnApSlot
            || !ApRunData.TryGetSharedState(_runState, out ApRunSharedState shared)
            || shared.SharedSlotCheckScope != SharedSlotCheckScope.AllApParticipants)
        {
            return;
        }

        foreach (Player player in _runState.Players.Where(IsApGuest).Where(UsesCampfireSanity))
        {
            if (TryBuildState(player, out ApRestSiteState state, out string error))
                PublishState(player, state);
            else
                LogUtility.Error(
                    $"Could not publish AP Guest rest-site state for {player.NetId}: {error}"
                );
        }
    }

    public static void BeforeOptionsGenerated()
    {
        if (_runState == null
            || !MultiplayerSupport.ShouldRunReplicatedConstruction(MultiplayerFeature.RestSites))
        {
            _manifestStatus = ManifestStatus.Disabled;
            return;
        }

        _visitId = CreateVisitId(_runState);
        _constructionFailure = null;
        _manifestStatus = ManifestStatus.Waiting;
        _failureNoticeShown = false;
        FrozenStates.Clear();
        foreach ((ulong owner, ApRestSiteState state) in States)
            FrozenStates[owner] = state;
        foreach (ulong reporter in Manifests
            .Where(pair => pair.Value.VisitId != _visitId)
            .Select(pair => pair.Key)
            .ToList())
        {
            Manifests.Remove(reporter);
        }
    }

    public static bool TryGetFrozenState(
        Player player,
        out ApRestSiteState state,
        out string reason)
    {
        if (!FrozenStates.TryGetValue(player.NetId, out state!))
        {
            reason = $"No host-confirmed AP rest-site state exists for player {player.NetId}.";
            return false;
        }
        if (!TryGetCharacterIdentity(player, out long offset, out _, out reason)
            || !HasValidStateShape(state, offset))
        {
            reason = string.IsNullOrEmpty(reason)
                ? $"AP rest-site state for player {player.NetId} was invalid."
                : reason;
            return false;
        }
        return true;
    }

    public static void ReportConstructionFailure(string reason)
    {
        _constructionFailure ??= reason;
        LogUtility.Error(reason);
    }

    public static void AfterOptionsGenerated(RestSiteSynchronizer synchronizer)
    {
        if (_runState == null || _manifestStatus == ManifestStatus.Disabled)
            return;

        var report = new ApRestSiteManifestMessage
        {
            RunId = _runId,
            VisitId = _visitId ?? CreateVisitId(_runState),
            ReporterNetId = RunManager.Instance.NetService.NetId,
            ConstructionFailure = _constructionFailure,
            OptionKeys = BuildManifest(synchronizer, _runState),
        };

        AcceptManifest(report);
        INetGameService netService = RunManager.Instance.NetService;
        bool sent = netService.Type == NetGameType.Host
            ? RitsuLibSidecarSyncMessages.Broadcast(netService, ManifestDescriptor, report)
            : RitsuLibSidecarSyncMessages.SendToHostAndBroadcast(
                netService,
                ManifestDescriptor,
                report
            );
        if (!sent)
            FailManifest("Could not publish the local rest-site option manifest.");
    }

    public static void ApplyManifestGuardToUi(NRestSiteRoom room)
    {
        if (_manifestStatus is ManifestStatus.Waiting or ManifestStatus.Failed)
            room.DisableOptions();
        else if (_manifestStatus == ManifestStatus.Approved)
            room.EnableOptions();
    }

    private static bool UsesCampfireSanity(Player player) =>
        MultiplayerLocationChecks.TryGetSettings(player, out ArchipelagoSettings settings)
        && settings.CampfireSanity;

    private static bool IsApGuest(Player player) =>
        _runState != null
        && ApRunData.TryGetPlayerState(_runState, player.NetId, out ApPlayerRunState state)
        && state.Participation == ApParticipationKind.ApGuest;

    private static void PublishState(Player owner, ApRestSiteState state)
    {
        var message = new ApRestSiteStateMessage
        {
            RunId = _runId,
            OwnerNetId = owner.NetId,
            State = state,
        };
        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type == NetGameType.Host)
        {
            if (TryInstallState(message, out string error))
                BroadcastState(netService, message);
            else
                LogUtility.Error(error);
            return;
        }

        if (!RitsuLibSidecarTypedMessageRegistry.SendToHost(
                netService,
                StateDescriptor,
                message
            ))
        {
            LogUtility.Error($"Could not send AP rest-site state for {owner.NetId} to the host.");
        }
    }

    private static void OnStateReceived(
        RitsuLibSidecarTypedDispatchContext<ApRestSiteStateMessage> context)
    {
        if (!RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
            {
                ApRestSiteStateMessage message = context.Message;
                INetGameService netService = RunManager.Instance.NetService;
                if (message.SchemaVersion != 1
                    || !TryAdoptRun(message.RunId)
                    || _runState == null
                    || !ApRunData.TryGetPlayerState(
                        _runState,
                        message.OwnerNetId,
                        out ApPlayerRunState ownerState
                    ))
                {
                    LogUtility.Error("Rejected invalid AP rest-site state message.");
                    return;
                }

                if (netService.Type == NetGameType.Host)
                {
                    if (context.SenderNetId != message.OwnerNetId
                        || ownerState.Participation != ApParticipationKind.OwnApSlot)
                    {
                        LogUtility.Error("Rejected incorrectly owned AP rest-site state.");
                        return;
                    }
                    if (TryInstallState(message, out string hostError))
                        BroadcastState(netService, message);
                    else
                        LogUtility.Error(hostError);
                    return;
                }

                if (!BetaMainCompatibility.TryGetHostNetId(netService, out ulong hostNetId)
                    || context.SenderNetId != hostNetId)
                {
                    LogUtility.Error("Rejected AP rest-site state from a non-host peer.");
                    return;
                }
                if (!TryInstallState(message, out string clientError))
                    LogUtility.Error(clientError);
            }))
        {
            LogUtility.Error("Could not schedule the AP rest-site state message.");
        }
    }

    private static bool TryInstallState(ApRestSiteStateMessage message, out string reason)
    {
        reason = string.Empty;
        if (_runState == null
            || message.RunId != _runId
            || message.State == null
            || _runState.GetPlayer(message.OwnerNetId) is not Player owner
            || !TryGetCharacterIdentity(owner, out long offset, out _, out reason)
            || !HasValidStateShape(message.State, offset)
            || !ApRunData.TrySetRestSiteState(_runState, message.OwnerNetId, message.State))
        {
            reason = string.IsNullOrEmpty(reason)
                ? $"Could not install AP rest-site state for {message.OwnerNetId}."
                : reason;
            return false;
        }

        States[message.OwnerNetId] = message.State;
        LogUtility.Info(
            $"Installed AP rest-site state: owner={message.OwnerNetId}, "
                + $"restLevel={message.State.ProgressiveRestLevel}, "
                + $"smithLevel={message.State.ProgressiveSmithLevel}"
        );
        return true;
    }

    private static void BroadcastState(
        INetGameService netService,
        ApRestSiteStateMessage message)
    {
        if (!RitsuLibSidecarTypedMessageRegistry.Broadcast(
                netService,
                StateDescriptor,
                message
            ))
        {
            LogUtility.Error($"Could not broadcast AP rest-site state for {message.OwnerNetId}.");
        }
    }

    private static bool TryBuildState(
        Player player,
        out ApRestSiteState state,
        out string reason)
    {
        state = null!;
        if (!TryGetCharacterIdentity(
                player,
                out long offset,
                out string characterName,
                out reason
            ))
        {
            return false;
        }

        int restLevel = ArchipelagoClient.Progress.MaxRestLevel(offset) ?? 0;
        int smithLevel = ArchipelagoClient.Progress.MaxSmithLevel(offset) ?? 0;
        if (!ArchipelagoClient.IsConnected)
        {
            if (!States.TryGetValue(player.NetId, out ApRestSiteState? prior))
            {
                reason = "AP is disconnected and no checkpointed campfire state exists.";
                return false;
            }
            state = CloneWithLiveConsumption(prior, restLevel, smithLevel);
            return true;
        }

        state = new ApRestSiteState
        {
            CharacterOffset = offset,
            ProgressiveRestLevel = restLevel,
            ProgressiveSmithLevel = smithLevel,
        };
        for (int act = 1; act <= 3; act++)
        {
            for (int campfire = 1; campfire <= 2; campfire++)
            {
                string locationName = $"{characterName} Act {act} Campfire {campfire}";
                long locationId = ArchipelagoClient.Session.Locations.GetLocationIdFromName(
                    "Slay the Spire II",
                    locationName
                );
                if (locationId == -1)
                {
                    reason = $"The AP slot did not define '{locationName}'.";
                    return false;
                }

                string description = locationName;
                string optionId = "FILLER";
                if (ArchipelagoClient.ScoutedLocations.TryGetValue(
                        locationId,
                        out ScoutedItemInfo? info
                    ))
                {
                    description = $"{info.Player.Alias}'s {info.ItemName}";
                    optionId = GetScoutedOptionId(info);
                }
                state.CampfireChecks.Add(new ApCampfireCheckState
                {
                    Act = act,
                    Campfire = campfire,
                    LocationName = locationName,
                    LocationId = locationId,
                    IsChecked = IsLocallyConsumed(locationName, locationId),
                    Description = description,
                    OptionId = optionId,
                });
            }
        }
        return true;
    }

    private static ApRestSiteState CloneWithLiveConsumption(
        ApRestSiteState prior,
        int restLevel,
        int smithLevel) => new()
    {
        CharacterOffset = prior.CharacterOffset,
        ProgressiveRestLevel = restLevel,
        ProgressiveSmithLevel = smithLevel,
        CampfireChecks = prior.CampfireChecks.Select(check => new ApCampfireCheckState
        {
            Act = check.Act,
            Campfire = check.Campfire,
            LocationName = check.LocationName,
            LocationId = check.LocationId,
            IsChecked = check.IsChecked || IsLocallyConsumed(check.LocationName, check.LocationId),
            Description = check.Description,
            OptionId = check.OptionId,
        }).ToList(),
    };

    private static bool IsLocallyConsumed(string locationName, long locationId) =>
        ArchipelagoClient.CheckedLocations.Contains(locationId)
        || ArchipelagoClient.Progress.PendingLocationChecks.Contains(locationId)
        || ArchipelagoClient.Progress.CampfiresChecked.TryGetValue(
            locationName,
            out bool checkedValue
        ) && checkedValue;

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

    private static bool TryGetCharacterIdentity(
        Player player,
        out long offset,
        out string apName,
        out string reason)
    {
        offset = 0;
        apName = string.Empty;
        reason = string.Empty;
        if (!MultiplayerLocationChecks.TryGetSettings(
                player,
                out ArchipelagoSettings settings
            )
            || !settings.Characters.TryGetValue(
                player.Character.Id.Entry,
                out CharacterConfig? config
            ))
        {
            reason = $"No frozen AP character mapping exists for player {player.NetId}.";
            return false;
        }

        offset = config.CharOffset;
        apName = config.ModNum == 0 ? config.Name : $"Custom Character {config.ModNum}";
        return offset > 0 && !string.IsNullOrWhiteSpace(apName);
    }

    private static Task HandleManifest(
        RitsuLibSidecarSyncMessageContext<ApRestSiteManifestMessage> context)
    {
        ApRestSiteManifestMessage report = context.Message;
        if (report.SchemaVersion != 1
            || report.ReporterNetId != context.SenderNetId
            || string.IsNullOrWhiteSpace(report.VisitId)
            || report.OptionKeys == null
            || report.OptionKeys.Count == 0
            || report.OptionKeys.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Invalid AP rest-site manifest.");
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (!RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
            {
                if (TryAdoptRun(report.RunId)
                    && _runState != null
                    && report.VisitId == CreateVisitId(_runState))
                {
                    AcceptManifest(report);
                }
                completion.SetResult();
            }))
        {
            completion.SetException(
                new InvalidOperationException("Godot main loop was unavailable for rest-site parity.")
            );
        }
        return completion.Task;
    }

    private static void AcceptManifest(ApRestSiteManifestMessage report)
    {
        Manifests[report.ReporterNetId] = report;
        EvaluateManifests();
    }

    private static void EvaluateManifests()
    {
        if (_manifestStatus == ManifestStatus.Failed || _runState == null)
            return;
        if (_constructionFailure != null)
        {
            FailManifest(_constructionFailure);
            return;
        }

        IReadOnlyList<ulong> connected;
        try
        {
            connected = _runLobby == null
                ? _runState.Players.Select(player => player.NetId).ToArray()
                : BetaMainCompatibility.GetConnectedRunPlayerNetIds(_runLobby);
        }
        catch (Exception ex)
        {
            FailManifest($"Could not resolve connected rest-site peers: {ex.Message}");
            return;
        }

        if (connected.Count == 0)
        {
            FailManifest("The active rest site had no connected players.");
            return;
        }
        if (connected.Any(netId =>
            !Manifests.TryGetValue(netId, out ApRestSiteManifestMessage? report)
            || report.VisitId != _visitId))
        {
            _manifestStatus = ManifestStatus.Waiting;
            ApplyCurrentUiState();
            return;
        }

        ApRestSiteManifestMessage baseline = Manifests[connected[0]];
        foreach (ulong netId in connected)
        {
            ApRestSiteManifestMessage report = Manifests[netId];
            if (!string.IsNullOrEmpty(report.ConstructionFailure))
            {
                FailManifest(
                    $"Player {netId} could not construct rest-site options: "
                        + report.ConstructionFailure
                );
                return;
            }
            if (!baseline.OptionKeys.SequenceEqual(report.OptionKeys))
            {
                LogUtility.Error(
                    $"Rest-site manifest mismatch: reporter {baseline.ReporterNetId}="
                        + $"[{string.Join(",", baseline.OptionKeys)}], reporter "
                        + $"{report.ReporterNetId}=[{string.Join(",", report.OptionKeys)}]"
                );
                FailManifest(
                    $"Rest-site option order differs between players "
                        + $"{baseline.ReporterNetId} and {report.ReporterNetId}."
                );
                return;
            }
        }

        _manifestStatus = ManifestStatus.Approved;
        LogUtility.Info(
            $"Validated AP rest-site option parity for {_visitId}: "
                + $"reporters=[{string.Join(",", connected)}]"
        );
        ApplyCurrentUiState();
    }

    private static void FailManifest(string reason)
    {
        _manifestStatus = ManifestStatus.Failed;
        LogUtility.Error($"AP rest-site synchronization blocked: {reason}");
        ApplyCurrentUiState();
        if (_failureNoticeShown)
            return;

        _failureNoticeShown = true;
        NotificationUtility.ShowRawText(
            "Rest-site synchronization failed. Campfire choices are disabled to prevent a desync.",
            timeout: 30.0,
            priority: NotificationUtility.NotificationPriority.High
        );
    }

    private static void ApplyCurrentUiState()
    {
        if (NRestSiteRoom.Instance is NRestSiteRoom room)
            ApplyManifestGuardToUi(room);
    }

    private static void OnRemotePlayerDisconnected(ulong _)
    {
        if (_manifestStatus == ManifestStatus.Waiting
            && !RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(EvaluateManifests))
        {
            LogUtility.Error("Could not re-evaluate rest-site parity after a disconnect.");
        }
    }

    private static bool TryAdoptRun(Guid runId)
    {
        if (_runState != null)
            return _runId == runId;
        if (_runId != Guid.Empty
            || runId == Guid.Empty
            || RunManager.Instance.DebugOnlyGetState() is not RunState current
            || !ApRunData.TryGetSharedState(current, out ApRunSharedState shared)
            || shared.RunId != runId)
        {
            return false;
        }

        _runState = current;
        _runId = runId;
        foreach (Player player in current.Players)
        {
            if (ApRunData.TryGetPlayerState(current, player.NetId, out ApPlayerRunState state)
                && state.RestSiteState != null)
            {
                States[player.NetId] = state.RestSiteState;
            }
        }
        return true;
    }

    private static string CreateVisitId(RunState runState) =>
        $"act-{runState.CurrentActIndex + 1}/floor-"
            + runState.MapPointHistory.Sum(actEntries => actEntries.Count);

    private static string GetSemanticOptionKey(RestSiteOption option) =>
        option is IApRestSiteSemanticOption apOption
            ? apOption.SemanticKey
            : $"{option.GetType().FullName}|{option.OptionId}|enabled={option.IsEnabled}";

    private static List<string> BuildManifest(
        RestSiteSynchronizer synchronizer,
        RunState runState) => runState.Players.SelectMany(player =>
            new[] { $"OWNER|{player.NetId}" }.Concat(
                synchronizer.GetOptionsForPlayer(player).Select(GetSemanticOptionKey)
            )
        ).ToList();

    private static bool HasValidStateShape(ApRestSiteState state, long offset) =>
        state.SchemaVersion == 1
        && state.CharacterOffset == offset
        && state.ProgressiveRestLevel >= 0
        && state.ProgressiveSmithLevel >= 0
        && state.CampfireChecks != null
        && state.CampfireChecks.Count == ArchipelagoProgress._maxCampfireChecks
        && state.CampfireChecks.All(check =>
            check.Act is >= 1 and <= 3
            && check.Campfire is >= 1 and <= 2
            && check.LocationId != -1
            && !string.IsNullOrWhiteSpace(check.LocationName)
            && !string.IsNullOrWhiteSpace(check.Description)
            && check.OptionId is "PROGRESSION" or "TRAP" or "USEFUL" or "FILLER")
        && state.CampfireChecks
            .Select(check => (check.Act, check.Campfire))
            .Distinct()
            .Count() == ArchipelagoProgress._maxCampfireChecks;

}
