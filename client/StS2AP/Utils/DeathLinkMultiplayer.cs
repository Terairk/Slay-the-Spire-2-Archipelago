using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Networking.Sidecar;

namespace StS2AP.Utils;

/// <summary>
/// Owns multiplayer DeathLink transport, replicated HP mutation, and individual-death authority.
/// An own-slot client submits an external-event intent to the native host. The host validates the
/// frozen AP run context and broadcasts one required, location-targeted damage recipe which every
/// replica applies after the currently executing native action finishes.
/// </summary>
public static class DeathLinkMultiplayer
{
    private const int SchemaVersion = 1;
    private const string RequestMessageKey = "death_link_damage_request_v1";
    private const string DamageMessageKey = "death_link_damage_v1";
    private static readonly TimeSpan EchoFallbackWindow = TimeSpan.FromSeconds(6);
    private static readonly object StateLock = new();
    private static readonly SemaphoreSlim DamageGate = new(1, 1);
    private static readonly HashSet<Guid> PublishedEvents = new();
    private static readonly HashSet<Guid> HandledEvents = new();
    private static readonly HashSet<ulong> ActiveInboundDeaths = new();
    private static readonly Dictionary<ulong, DateTime> RecentInboundLethalDamage = new();

    private static readonly RitsuLibSidecarJsonSerializer<DeathLinkDamageRequestMessage>
        RequestSerializer = new();
    private static readonly RitsuLibSidecarMessageDescriptor<DeathLinkDamageRequestMessage>
        RequestDescriptor = new(
            ModEntry.ModId,
            RequestMessageKey,
            RequestSerializer.Serialize,
            RequestSerializer.Deserialize,
            Required: true
        );
    private static readonly RitsuLibSidecarJsonSerializer<DeathLinkDamageMessage>
        DamageSerializer = new();
    private static readonly RitsuLibSidecarSyncMessageDescriptor<DeathLinkDamageMessage>
        DamageDescriptor = new(
            ModEntry.ModId,
            DamageMessageKey,
            DamageSerializer.Serialize,
            DamageSerializer.Deserialize,
            HandleDamageMessage,
            LocationTargeted: true,
            ShouldBuffer: true,
            Mode: NetTransferMode.Reliable,
            FailurePolicy: RitsuLibSidecarSyncFailurePolicy.Required,
            BroadcastScope: RitsuLibSidecarSyncBroadcastScope.ReadyPeers,
            DispatchLocalOnBroadcast: true,
            LogLevel: LogLevel.Debug,
            ShouldBroadcast: false
        );

    private static IDisposable? _requestSubscription;

    public static void Initialize()
    {
        if (_requestSubscription != null)
            return;

        _requestSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(
            RequestDescriptor,
            OnDamageRequested
        );
        RitsuLibSidecarSyncMessages.Register(DamageDescriptor);
    }

    public static void EndRun()
    {
        lock (StateLock)
        {
            PublishedEvents.Clear();
            HandledEvents.Clear();
            ActiveInboundDeaths.Clear();
            RecentInboundLethalDamage.Clear();
        }
    }

    /// <summary>
    /// Routes one AP SDK DeathLink callback into host-authored replicated damage. Only an own-slot
    /// process has an AP SDK callback; AP Guests are added by the host when its slot is targeted.
    /// </summary>
    public static void Receive(DeathLink info)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsLocalOwnApSlot
            || GameUtility.CurrentPlayer is not Player owner
            || !MultiplayerLocationChecks.IsLocalProgressOwner(owner)
            || owner.RunState is not RunState runState
            || !ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.RunId == Guid.Empty
            || !ApRunData.TryGetPlayerState(runState, owner.NetId, out ApPlayerRunState ownerState)
            || ownerState.Participation != ApParticipationKind.OwnApSlot
            || ownerState.SlotSettings is not ArchipelagoSettings ownerSettings
            || !ownerSettings.IsDeathLinkEnabled)
        {
            LogUtility.Warn("Ignored multiplayer DeathLink without a local own-slot run owner.");
            return;
        }

        var request = new DeathLinkDamageRequestMessage
        {
            RunId = shared.RunId,
            EventId = Guid.NewGuid(),
            OwnerNetId = owner.NetId,
            DamagePercent = ownerSettings.DeathLinkDamagePercent,
            Source = info.Source ?? string.Empty,
            Cause = info.Cause,
        };

        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type == NetGameType.Host)
        {
            PublishValidatedDamage(request, owner.NetId);
            return;
        }

        if (!RitsuLibSidecarTypedMessageRegistry.SendToHost(
                netService,
                RequestDescriptor,
                request
            ))
        {
            LogUtility.Error($"Could not submit DeathLink {request.EventId} to the game host.");
            NotificationUtility.ShowRawText("Could not synchronize the received DeathLink.");
        }
    }

    /// <summary>Handles an actual, death-prevention-approved player death on every replica.</summary>
    public static void PlayerDied(Player player)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsFeatureEnabled(MultiplayerFeature.DeathLink)
            || !ArchipelagoClient.IsConnected
            || player.RunState is not RunState runState
            || runState.CurrentRoom?.IsVictoryRoom == true
            || !IsDeathLinkWriter(player, runState, out ApParticipationKind participation)
            || !ApPlayerContextResolver.TryGetRewardSettings(
                player,
                out ArchipelagoSettings settings
            )
            || !settings.IsDeathLinkEnabled)
        {
            return;
        }

        if (ShouldSuppressOutgoing(player.NetId, out string reason))
        {
            LogUtility.Info(
                $"Suppressing outgoing DeathLink for player {player.NetId}: {reason}."
            );
            return;
        }

        string floorCause = $"Act {runState.CurrentActIndex + 1} Floor {runState.ActFloor}";
        string characterName = player.Character.Id.Entry;
        string cause = participation == ApParticipationKind.ApGuest
            ? $"{ArchipelagoClient.PlayerName}'s AP Guest ({characterName}) was Slain on {floorCause}"
            : $"{ArchipelagoClient.PlayerName} ({characterName}) was Slain on {floorCause}";

        ArchipelagoClient.DeathLinkController.SendDeathLink(
            new DeathLink(ArchipelagoClient.PlayerName, cause)
        );
        LogUtility.Info(
            $"Sent individual-player DeathLink for {player.NetId} ({participation})."
        );
    }

    private static void OnDamageRequested(
        RitsuLibSidecarTypedDispatchContext<DeathLinkDamageRequestMessage> context)
    {
        bool posted = RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(
            () => PublishValidatedDamage(context.Message, context.SenderNetId)
        );
        if (!posted)
            LogUtility.Error("Could not schedule a multiplayer DeathLink request on the main loop.");
    }

    private static void PublishValidatedDamage(
        DeathLinkDamageRequestMessage request,
        ulong senderNetId)
    {
        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type != NetGameType.Host
            || senderNetId != request.OwnerNetId
            || !TryValidateOwnerRequest(request, out RunState runState, out Player owner))
        {
            LogUtility.Warn(
                $"Rejected multiplayer DeathLink request {request.EventId} from {senderNetId}."
            );
            return;
        }

        lock (StateLock)
        {
            if (!PublishedEvents.Add(request.EventId))
                return;
        }

        IReadOnlyList<ulong> targets = GetExpectedTargets(runState, owner.NetId);
        var message = new DeathLinkDamageMessage
        {
            RunId = request.RunId,
            EventId = request.EventId,
            SlotOwnerNetId = owner.NetId,
            DamagePercent = request.DamagePercent,
            Source = request.Source,
            Cause = request.Cause,
            TargetNetIds = targets.ToList(),
        };

        if (!RitsuLibSidecarSyncMessages.Broadcast(netService, DamageDescriptor, message))
        {
            lock (StateLock)
                PublishedEvents.Remove(request.EventId);
            LogUtility.Error(
                $"Could not broadcast multiplayer DeathLink damage {request.EventId}."
            );
            NotificationUtility.ShowRawText("Could not synchronize the received DeathLink.");
            return;
        }

        LogUtility.Info(
            $"Published DeathLink {request.EventId} for AP owner {owner.NetId}; "
                + $"targets=[{string.Join(",", targets)}], damage={request.DamagePercent}%."
        );
    }

    private static bool TryValidateOwnerRequest(
        DeathLinkDamageRequestMessage request,
        out RunState runState,
        out Player owner)
    {
        runState = null!;
        owner = null!;
        if (request.SchemaVersion != SchemaVersion
            || request.RunId == Guid.Empty
            || request.EventId == Guid.Empty
            || request.DamagePercent is < 0 or > 100
            || request.Source is null
            || request.Source.Length > 1024
            || request.Cause?.Length > 2048
            || RunManager.Instance.DebugOnlyGetState() is not RunState current
            || !ApRunData.TryGetSharedState(current, out ApRunSharedState shared)
            || shared.RunId != request.RunId
            || current.GetPlayer(request.OwnerNetId) is not Player currentOwner
            || !ApRunData.TryGetPlayerState(
                current,
                request.OwnerNetId,
                out ApPlayerRunState ownerState
            )
            || ownerState.Participation != ApParticipationKind.OwnApSlot
            || ownerState.SlotSettings is not ArchipelagoSettings settings
            || !settings.IsDeathLinkEnabled
            || settings.DeathLinkDamagePercent != request.DamagePercent)
        {
            return false;
        }

        runState = current;
        owner = currentOwner;
        return true;
    }

    private static Task HandleDamageMessage(
        RitsuLibSidecarSyncMessageContext<DeathLinkDamageMessage> context)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        bool posted = RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(
            () => CompleteDamageMessage(context.Message, context.SenderNetId, completion)
        );
        if (!posted)
        {
            completion.SetException(
                new InvalidOperationException(
                    "Godot main loop was unavailable for synchronized DeathLink damage."
                )
            );
        }
        return completion.Task;
    }

    private static async void CompleteDamageMessage(
        DeathLinkDamageMessage message,
        ulong senderNetId,
        TaskCompletionSource completion)
    {
        bool gateHeld = false;
        bool pausedHere = false;
        try
        {
            await RitsuLibSidecarGodotMainLoopScheduling.ContinueOnGodotMainLoopAsync(
                DamageGate.WaitAsync()
            );
            gateHeld = true;

            if (!TryValidateDamageMessage(message, senderNetId, out RunState runState))
                throw new InvalidOperationException("Rejected invalid synchronized DeathLink damage.");

            lock (StateLock)
            {
                if (!HandledEvents.Add(message.EventId))
                {
                    completion.SetResult();
                    return;
                }
            }

            var executor = RunManager.Instance.ActionExecutor;
            if (!executor.IsPaused)
            {
                executor.Pause();
                pausedHere = true;
            }
            while (executor.CurrentlyRunningAction != null)
                await NGame.Instance!.AwaitProcessFrame();

            // The wait above can span a room transition. Revalidate the run identity before
            // touching deterministic state.
            if (!TryValidateDamageMessage(message, senderNetId, out runState))
                throw new InvalidOperationException(
                    "Synchronized DeathLink no longer matched the active run after waiting."
                );

            var plans = new List<(Player Target, int NewHp)>();
            foreach (ulong targetNetId in message.TargetNetIds.Order())
            {
                Player target = runState.GetPlayer(targetNetId)
                    ?? throw new InvalidOperationException(
                        $"DeathLink target {targetNetId} was absent from the run."
                    );
                if (target.Creature.IsDead)
                    continue;

                if (LocalContext.IsMe(target))
                {
                    string cause = message.Cause ?? $"{message.Source} died";
                    NotificationUtility.ShowDeathLink(new DeathLink(message.Source, cause));
                }

                int damage = Mathf.RoundToInt(
                    target.Creature.MaxHp * (message.DamagePercent / 100.0f)
                );
                int newHp = Math.Max(0, target.Creature.CurrentHp - damage);
                plans.Add((target, newHp));
            }

            // Mark the complete AP-slot event as causal before applying its first target. A death
            // hook from one target may affect another target before the sequential recipe reaches
            // it, and that secondary death must not echo the same incoming DeathLink.
            lock (StateLock)
            {
                foreach ((Player target, int newHp) in plans)
                {
                    ActiveInboundDeaths.Add(target.NetId);
                    if (newHp == 0)
                        RecentInboundLethalDamage[target.NetId] = DateTime.UtcNow;
                }
            }

            try
            {
                foreach ((Player target, int newHp) in plans)
                {
                    LogUtility.Info(
                        $"Applying synchronized DeathLink {message.EventId} to {target.NetId}: "
                            + $"{target.Creature.CurrentHp}->{newHp} HP."
                    );
                    await CreatureCmd.SetCurrentHp(target.Creature, newHp);
                }
            }
            finally
            {
                lock (StateLock)
                {
                    foreach ((Player target, _) in plans)
                    {
                        ActiveInboundDeaths.Remove(target.NetId);
                        if (!target.Creature.IsDead)
                            RecentInboundLethalDamage.Remove(target.NetId);
                    }
                }
            }

            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        finally
        {
            try
            {
                if (pausedHere)
                    RunManager.Instance.ActionExecutor.Unpause();
            }
            catch (Exception ex)
            {
                LogUtility.Error($"Could not unpause after synchronized DeathLink: {ex}");
            }
            finally
            {
                if (gateHeld)
                    DamageGate.Release();
            }
        }
    }

    private static bool TryValidateDamageMessage(
        DeathLinkDamageMessage message,
        ulong senderNetId,
        out RunState runState)
    {
        runState = null!;
        if (!BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out ulong hostNetId
            )
            || senderNetId != hostNetId
            || message.SchemaVersion != SchemaVersion
            || message.RunId == Guid.Empty
            || message.EventId == Guid.Empty
            || message.DamagePercent is < 0 or > 100
            || message.Source is null
            || message.Source.Length > 1024
            || message.TargetNetIds is null
            || message.Cause?.Length > 2048
            || RunManager.Instance.DebugOnlyGetState() is not RunState current
            || !ApRunData.TryGetSharedState(current, out ApRunSharedState shared)
            || shared.RunId != message.RunId
            || current.GetPlayer(message.SlotOwnerNetId) is not Player owner
            || !ApRunData.TryGetPlayerState(
                current,
                message.SlotOwnerNetId,
                out ApPlayerRunState ownerState
            )
            || ownerState.Participation != ApParticipationKind.OwnApSlot
            || ownerState.SlotSettings is not ArchipelagoSettings settings
            || !settings.IsDeathLinkEnabled
            || settings.DeathLinkDamagePercent != message.DamagePercent)
        {
            return false;
        }

        ulong[] expectedTargets = GetExpectedTargets(current, owner.NetId).Order().ToArray();
        ulong[] actualTargets = message.TargetNetIds.Distinct().Order().ToArray();
        if (!expectedTargets.SequenceEqual(actualTargets)
            || actualTargets.Length != message.TargetNetIds.Count)
        {
            return false;
        }

        runState = current;
        return true;
    }

    private static IReadOnlyList<ulong> GetExpectedTargets(RunState runState, ulong ownerNetId)
    {
        bool ownerIsHost = BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out ulong hostNetId
            )
            && ownerNetId == hostNetId;
        if (!ownerIsHost)
            return new[] { ownerNetId };

        return runState.Players
            .Where(player =>
                player.NetId == ownerNetId
                || ApRunData.TryGetPlayerState(
                    runState,
                    player.NetId,
                    out ApPlayerRunState state
                ) && state.Participation == ApParticipationKind.ApGuest
            )
            .Select(player => player.NetId)
            .Order()
            .ToArray();
    }

    private static bool IsDeathLinkWriter(
        Player player,
        RunState runState,
        out ApParticipationKind participation)
    {
        participation = ApParticipationKind.VanillaGuest;
        if (!ApRunData.TryGetPlayerState(
                runState,
                player.NetId,
                out ApPlayerRunState state
            ))
        {
            return false;
        }

        participation = state.Participation;
        if (participation == ApParticipationKind.OwnApSlot)
        {
            return MultiplayerSupport.IsLocalOwnApSlot
                && MultiplayerLocationChecks.IsLocalProgressOwner(player);
        }

        return participation == ApParticipationKind.ApGuest
            && MultiplayerSupport.IsLocalOwnApSlot
            && RunManager.Instance.NetService.Type == NetGameType.Host;
    }

    private static bool ShouldSuppressOutgoing(ulong playerNetId, out string reason)
    {
        lock (StateLock)
        {
            if (ActiveInboundDeaths.Contains(playerNetId))
            {
                RecentInboundLethalDamage.Remove(playerNetId);
                reason = "the death is being applied by an incoming DeathLink";
                return true;
            }

            if (RecentInboundLethalDamage.Remove(playerNetId, out DateTime receivedAt))
            {
                TimeSpan elapsed = DateTime.UtcNow - receivedAt;
                if (elapsed <= EchoFallbackWindow)
                {
                    reason = $"incoming lethal damage was received {elapsed.TotalSeconds:F2}s ago";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }
}
