using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archipelago.MultiClient.Net.Models;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using StS2AP.Data;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.Patches;
using StS2AP.Persistence;
using StS2AP.UI;
using STS2RitsuLib.Combat.Rewards;
using STS2RitsuLib.Networking.Sidecar;

namespace StS2AP.Utils;

/// <summary>
/// Builds one immutable native AP reward-menu snapshot. In multiplayer the owner publishes the
/// complete recipe before any native RewardsSet begins, so every replica has matching reward
/// indexes and MegaCrit can own the entire selection lifecycle.
/// </summary>
public static class ApMirroredRewardDispatcher
{
    private const string SidecarMessageKey = "received_reward_menu_v1";
    private const string MaterializationAckKey = "reward_materialization_ack_v1";
    private const string MaterializationDecisionKey = "reward_materialization_decision_v1";
    private const string ReplicaNativeStrategyId = "replica_native_v1";
    private const string OwnerFinalApRngStrategyId = "ap_rng_owner_final_v1";
    private const string SilkenTressEffectId = "silken_tress_used_v1";
    private const string SilverCrucibleEffectId = "silver_crucible_times_used_v1";

    private interface IApCardRewardMaterializer
    {
        string WireId { get; }
        bool RequiresReplicaMaterialization { get; }
        Task<ApNativeCardReward> MaterializeOwner(ApMirroredRewardSpec spec, Player player);
        Task<ApNativeCardReward> MaterializeReplica(ApMirroredRewardSpec spec, Player player);
    }

    private interface IApPotionRewardMaterializer
    {
        string WireId { get; }
        bool RequiresReplicaMaterialization { get; }
        PotionModel MaterializeOwner(ApMirroredRewardSpec spec, Player player);
        PotionModel MaterializeReplica(ApMirroredRewardSpec spec, Player player);
    }

    private sealed class ReplicaNativeCardMaterializer : IApCardRewardMaterializer
    {
        public string WireId => ReplicaNativeStrategyId;
        public bool RequiresReplicaMaterialization => true;

        public async Task<ApNativeCardReward> MaterializeOwner(
            ApMirroredRewardSpec spec,
            Player player)
        {
            spec.StateBeforeMaterialization = CaptureMaterializationState(player);
            ApNativeCardReward reward = await MaterializeReplicaNativeCardReward(spec, player);
            spec.StateAfterMaterialization = CaptureMaterializationState(player);
            return reward;
        }

        public async Task<ApNativeCardReward> MaterializeReplica(
            ApMirroredRewardSpec spec,
            Player player)
        {
            ValidateMaterializationState(spec, player, spec.StateBeforeMaterialization, "pre");
            ApNativeCardReward reward = await MaterializeReplicaNativeCardReward(spec, player);
            ValidateSerializedModels(spec, reward.Cards.Select(SerializeCard), "card assignment");
            ValidateMaterializationState(spec, player, spec.StateAfterMaterialization, "post");
            return reward;
        }
    }

    private sealed class OwnerFinalApRngCardMaterializer : IApCardRewardMaterializer
    {
        public string WireId => OwnerFinalApRngStrategyId;
        public bool RequiresReplicaMaterialization => false;

        public Task<ApNativeCardReward> MaterializeOwner(
            ApMirroredRewardSpec spec,
            Player player) => MaterializeOwnerFinalApRngCardReward(spec, player);

        public Task<ApNativeCardReward> MaterializeReplica(
            ApMirroredRewardSpec spec,
            Player player) => Task.FromResult(RestoreCardReward(
                spec,
                player,
                DeserializeCards(spec, player),
                spec.CardCanReroll
            ));
    }

    private sealed class ReplicaNativePotionMaterializer : IApPotionRewardMaterializer
    {
        public string WireId => ReplicaNativeStrategyId;
        public bool RequiresReplicaMaterialization => true;

        public PotionModel MaterializeOwner(ApMirroredRewardSpec spec, Player player)
        {
            spec.StateBeforeMaterialization = CaptureMaterializationState(player);
            PotionModel potion = PotionFactory.CreateRandomPotionOutOfCombat(
                player,
                player.PlayerRng.Rewards
            ).ToMutable();
            spec.StateAfterMaterialization = CaptureMaterializationState(player);
            return potion;
        }

        public PotionModel MaterializeReplica(ApMirroredRewardSpec spec, Player player)
        {
            ValidateMaterializationState(spec, player, spec.StateBeforeMaterialization, "pre");
            PotionModel potion = PotionFactory.CreateRandomPotionOutOfCombat(
                player,
                player.PlayerRng.Rewards
            ).ToMutable();
            ValidateSerializedModels(spec, new[] { SerializePotion(potion) }, "potion assignment");
            ValidateMaterializationState(spec, player, spec.StateAfterMaterialization, "post");
            return potion;
        }
    }

    private sealed class OwnerFinalApRngPotionMaterializer : IApPotionRewardMaterializer
    {
        public string WireId => OwnerFinalApRngStrategyId;
        public bool RequiresReplicaMaterialization => false;

        public PotionModel MaterializeOwner(ApMirroredRewardSpec spec, Player player) =>
            PotionFactory.CreateRandomPotionOutOfCombat(
                player,
                CreateApRewardRng(spec, player, "potion")
            ).ToMutable();

        public PotionModel MaterializeReplica(ApMirroredRewardSpec spec, Player player) =>
            PotionModel.FromSerializable(
                Deserialize<SerializablePotion>(spec.SerializedModels.Single())
            );
    }

    private sealed class MaterializationAck
    {
        public Guid RunId { get; set; }
        public Guid MenuId { get; set; }
        public ulong OwnerNetId { get; set; }
        public ulong ReplicaNetId { get; set; }
        public string Digest { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Failure { get; set; } = string.Empty;
    }

    private sealed class MaterializationDecision
    {
        public Guid RunId { get; set; }
        public Guid MenuId { get; set; }
        public ulong OwnerNetId { get; set; }
        public string Digest { get; set; } = string.Empty;
        public bool Approved { get; set; }
        public string Failure { get; set; } = string.Empty;
    }

    private sealed class HostMaterializationAgreement
    {
        public required ApRewardMenuSpec Menu { get; init; }
        public required string Digest { get; init; }
        public required HashSet<ulong> ExpectedReplicas { get; init; }
        public Dictionary<ulong, MaterializationAck> Acks { get; } = new();
    }

    private static readonly RitsuLibSidecarJsonSerializer<ApRewardMenuSpec> MenuSerializer = new();
    private static readonly RitsuLibSidecarJsonSerializer<MaterializationAck> AckSerializer = new();
    private static readonly RitsuLibSidecarJsonSerializer<MaterializationDecision>
        DecisionSerializer = new();
    private static readonly RitsuLibSidecarSyncMessageDescriptor<ApRewardMenuSpec> MenuDescriptor =
        new(
            ModEntry.ModId,
            SidecarMessageKey,
            MenuSerializer.Serialize,
            MenuSerializer.Deserialize,
            HandleMenuSpec,
            LocationTargeted: true,
            ShouldBuffer: true,
            Mode: NetTransferMode.Reliable,
            FailurePolicy: RitsuLibSidecarSyncFailurePolicy.Required,
            BroadcastScope: RitsuLibSidecarSyncBroadcastScope.ReadyPeers,
            DispatchLocalOnBroadcast: false,
            LogLevel: LogLevel.Debug,
            ShouldBroadcast: true
        );
    private static readonly RitsuLibSidecarMessageDescriptor<MaterializationAck> AckDescriptor =
        new(
            ModEntry.ModId,
            MaterializationAckKey,
            AckSerializer.Serialize,
            AckSerializer.Deserialize,
            Required: true
        );
    private static readonly RitsuLibSidecarMessageDescriptor<MaterializationDecision>
        DecisionDescriptor = new(
            ModEntry.ModId,
            MaterializationDecisionKey,
            DecisionSerializer.Serialize,
            DecisionSerializer.Deserialize,
            Required: true
        );

    private static readonly Dictionary<(ApGrantId GrantId, ApMirroredRewardKind Kind), string>
        LastAttempts = new();
    private static readonly HashSet<(ulong OwnerNetId, Guid MenuId)> ActiveRemoteMenus = new();
    private static readonly Dictionary<(ulong OwnerNetId, int ItemIndex), ApNativeCardReward>
        ReplicaCardAssignments = new();
    private static readonly Dictionary<(ulong OwnerNetId, int ItemIndex), PotionModel>
        ReplicaPotionAssignments = new();
    private static readonly IApCardRewardMaterializer ReplicaNativeCardStrategy =
        new ReplicaNativeCardMaterializer();
    private static readonly IApCardRewardMaterializer OwnerFinalCardStrategy =
        new OwnerFinalApRngCardMaterializer();
    private static readonly IApPotionRewardMaterializer ReplicaNativePotionStrategy =
        new ReplicaNativePotionMaterializer();
    private static readonly IApPotionRewardMaterializer OwnerFinalPotionStrategy =
        new OwnerFinalApRngPotionMaterializer();
    private static readonly Dictionary<Guid, HostMaterializationAgreement> HostAgreements = new();
    private static readonly Dictionary<Guid, List<MaterializationAck>> EarlyAcks = new();
    private static readonly Dictionary<Guid, TaskCompletionSource<MaterializationDecision>>
        PendingDecisions = new();
    private static IDisposable? _ackSubscription;
    private static IDisposable? _decisionSubscription;
    private static int _agreementGeneration;

    /// <summary>
    /// Retains the old every-replica generation path for targeted diagnostics. Production AP
    /// rewards use owner-final AP RNG and therefore do not put every ready peer on the critical path.
    /// </summary>
    internal static bool UseStrictReplicaMaterializationForDiagnostics { get; set; }

    private static readonly System.Reflection.FieldInfo CardRewardCardsField =
        AccessTools.Field(typeof(CardReward), "_cards")
        ?? throw new MissingFieldException(typeof(CardReward).FullName, "_cards");
    private static readonly System.Reflection.FieldInfo RunStateAllCardsField =
        AccessTools.Field(typeof(RunState), "_allCards")
        ?? throw new MissingFieldException(typeof(RunState).FullName, "_allCards");

    /*
     * Production card and potion assignments are generated once by the receipt owner from a stable,
     * receipt-specific AP RNG. The immutable final models are then materialized on other replicas.
     * Reviewed persistent relic transitions are transmitted as idempotent before/after effects.
     * Unknown/modded reward hooks are logged and ignored for AP generation.
     *
     * The former replica-native path remains behind the strategy interface for diagnosis. Only menu
     * entries using that strict strategy require the all-ready-peer materialization agreement. Both
     * paths restore final cards as live native CardReward objects, so pending-reward behavior such as
     * Glitter responding to Player.RelicObtained still works.
     */
    private static string? _activeRunIdentity;

    public static string? ActiveRunIdentity => _activeRunIdentity;

    public static void Initialize()
    {
        RitsuLibSidecarSyncMessages.Register(MenuDescriptor);
        _ackSubscription ??= RitsuLibSidecarTypedMessageRegistry.Subscribe(
            AckDescriptor,
            context => PostAgreementMessage(() => HandleMaterializationAck(
                context.SenderNetId,
                context.Message
            ))
        );
        _decisionSubscription ??= RitsuLibSidecarTypedMessageRegistry.Subscribe(
            DecisionDescriptor,
            context => PostAgreementMessage(() => HandleMaterializationDecision(
                context.SenderNetId,
                context.Message
            ))
        );
    }

    /// <summary>Binds menu assignments and receipt consumption to the current native run.</summary>
    public static bool BeginRun(RunState runState, out string reason)
    {
        reason = string.Empty;
        EndRun();

        if (!ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.RunId == Guid.Empty)
        {
            reason = "The host-owned AP run identity was missing when rewards were bound.";
            return false;
        }

        _activeRunIdentity = shared.RunId.ToString("N");
        try
        {
            RestoreReplicaAssignments(runState);
        }
        catch (Exception ex)
        {
            reason = $"Could not restore pending AP native rewards: {ex.GetBaseException().Message}";
            EndRun();
            return false;
        }
        LogUtility.Info($"Bound native AP reward menus to run {_activeRunIdentity}");
        return true;
    }

    private static void RestoreReplicaAssignments(RunState runState)
    {
        foreach (Player player in runState.Players.OrderBy(candidate => candidate.NetId))
        {
            if (LocalContext.IsMe(player))
            {
                foreach ((int itemIndex, CardReward reward) in
                         ArchipelagoClient.Progress.CardAssignments)
                {
                    if (reward is ApNativeCardReward native)
                        ReplicaCardAssignments[(player.NetId, itemIndex)] = native;
                }
                foreach ((int itemIndex, PotionModel potion) in
                         ArchipelagoClient.Progress.PotionAssignments)
                {
                    ReplicaPotionAssignments[(player.NetId, itemIndex)] = potion;
                }
                continue;
            }

            if (!ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state)
                || !state.Progress.Initialized)
            {
                continue;
            }

            foreach ((int itemIndex, ApCardAssignmentState assignment) in
                     state.Progress.CardAssignments.OrderBy(entry => entry.Key))
            {
                List<CardModel> cards = assignment.SerializedCards
                    .Select(serialized => runState.LoadCard(
                        Deserialize<SerializableCard>(serialized),
                        player
                    ))
                    .ToList();
                RestorePersistedCardAssignment(itemIndex, assignment, player, cards);
            }
            foreach ((int itemIndex, string serialized) in
                     state.Progress.PotionAssignments.OrderBy(entry => entry.Key))
            {
                ReplicaPotionAssignments[(player.NetId, itemIndex)] = PotionModel.FromSerializable(
                    Deserialize<SerializablePotion>(serialized)
                );
            }
        }
    }

    public static void EndRun()
    {
        Interlocked.Increment(ref _agreementGeneration);
        foreach (TaskCompletionSource<MaterializationDecision> pending in PendingDecisions.Values)
            pending.TrySetCanceled();
        _activeRunIdentity = null;
        ActiveRemoteMenus.Clear();
        LastAttempts.Clear();
        ReplicaCardAssignments.Clear();
        ReplicaPotionAssignments.Clear();
        HostAgreements.Clear();
        EarlyAcks.Clear();
        PendingDecisions.Clear();
    }

    private static void PostAgreementMessage(Action action)
    {
        int generation = Volatile.Read(ref _agreementGeneration);
        if (!RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
            {
                if (generation != Volatile.Read(ref _agreementGeneration))
                    return;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LogUtility.Error($"AP reward materialization agreement failed: {ex}");
                    MultiplayerSupport.InvalidateRunClaims(
                        "AP reward materialization agreement failed"
                    );
                }
            }))
        {
            LogUtility.Error("Could not dispatch AP reward materialization agreement.");
        }
    }

    private static TaskCompletionSource<MaterializationDecision> GetDecisionWaiter(Guid menuId)
    {
        if (!PendingDecisions.TryGetValue(menuId, out var pending))
        {
            pending = new TaskCompletionSource<MaterializationDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            PendingDecisions[menuId] = pending;
        }
        return pending;
    }

    private static void RegisterHostAgreement(ApRewardMenuSpec menu)
    {
        if (RunManager.Instance.NetService is not NetHostGameService host)
            throw new InvalidOperationException("Only the host can coordinate AP materialization.");
        if (HostAgreements.ContainsKey(menu.MenuId))
            return;

        var expected = host.ConnectedPeers
            .Where(peer => peer.readyForBroadcasting)
            .Select(peer => peer.peerId)
            .Append(host.NetId)
            .ToHashSet();
        var agreement = new HostMaterializationAgreement
        {
            Menu = menu,
            Digest = ComputeMaterializationDigest(menu),
            ExpectedReplicas = expected,
        };
        HostAgreements.Add(menu.MenuId, agreement);

        if (EarlyAcks.Remove(menu.MenuId, out List<MaterializationAck>? early))
        {
            foreach (MaterializationAck ack in early)
                ApplyMaterializationAck(agreement, ack);
        }

        int generation = Volatile.Read(ref _agreementGeneration);
        _ = FailAgreementAfterTimeout(menu.MenuId, generation);
    }

    private static async Task FailAgreementAfterTimeout(Guid menuId, int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        PostAgreementMessage(() =>
        {
            if (generation != Volatile.Read(ref _agreementGeneration)
                || !HostAgreements.TryGetValue(
                    menuId,
                    out HostMaterializationAgreement? agreement))
            {
                return;
            }

            string missing = string.Join(",", agreement.ExpectedReplicas.Except(
                agreement.Acks.Keys
            ));
            PublishMaterializationDecision(
                agreement,
                false,
                $"Timed out waiting for replicas [{missing}]."
            );
        });
    }

    private static void HandleMaterializationAck(ulong senderNetId, MaterializationAck ack)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host
            || senderNetId != ack.ReplicaNetId)
        {
            return;
        }

        if (!HostAgreements.TryGetValue(ack.MenuId, out HostMaterializationAgreement? agreement))
        {
            if (!EarlyAcks.TryGetValue(ack.MenuId, out List<MaterializationAck>? early))
            {
                early = new List<MaterializationAck>();
                EarlyAcks.Add(ack.MenuId, early);
            }
            early.Add(ack);
            return;
        }

        ApplyMaterializationAck(agreement, ack);
    }

    private static void ApplyMaterializationAck(
        HostMaterializationAgreement agreement,
        MaterializationAck ack)
    {
        if (ack.RunId != agreement.Menu.RunId
            || ack.OwnerNetId != agreement.Menu.OwnerNetId
            || !agreement.ExpectedReplicas.Contains(ack.ReplicaNetId)
            || !string.Equals(ack.Digest, agreement.Digest, StringComparison.Ordinal))
        {
            PublishMaterializationDecision(
                agreement,
                false,
                $"Replica {ack.ReplicaNetId} returned an invalid acknowledgement."
            );
            return;
        }

        agreement.Acks[ack.ReplicaNetId] = ack;
        if (!ack.Success)
        {
            PublishMaterializationDecision(
                agreement,
                false,
                $"Replica {ack.ReplicaNetId}: {ack.Failure}"
            );
            return;
        }

        if (agreement.ExpectedReplicas.All(agreement.Acks.ContainsKey))
            PublishMaterializationDecision(agreement, true, string.Empty);
    }

    private static void PublishMaterializationDecision(
        HostMaterializationAgreement agreement,
        bool approved,
        string failure)
    {
        if (!HostAgreements.Remove(agreement.Menu.MenuId))
            return;

        var decision = new MaterializationDecision
        {
            RunId = agreement.Menu.RunId,
            MenuId = agreement.Menu.MenuId,
            OwnerNetId = agreement.Menu.OwnerNetId,
            Digest = agreement.Digest,
            Approved = approved,
            Failure = failure,
        };
        if (!RitsuLibSidecarTypedMessageRegistry.Broadcast(
                RunManager.Instance.NetService,
                DecisionDescriptor,
                decision))
        {
            decision.Approved = false;
            decision.Failure = "The host could not broadcast materialization approval.";
        }
        ApplyMaterializationDecision(decision);
    }

    private static void HandleMaterializationDecision(
        ulong senderNetId,
        MaterializationDecision decision)
    {
        if (!BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out ulong hostNetId)
            || senderNetId != hostNetId)
        {
            return;
        }
        ApplyMaterializationDecision(decision);
    }

    private static void ApplyMaterializationDecision(MaterializationDecision decision)
    {
        if (GetDecisionWaiter(decision.MenuId).TrySetResult(decision))
        {
            LogUtility.Debug(
                $"AP materialization agreement {decision.MenuId}: approved={decision.Approved}"
            );
        }
    }

    private static async Task WaitForMaterializationDecision(ApRewardMenuSpec menu)
    {
        try
        {
            MaterializationDecision decision = await GetDecisionWaiter(menu.MenuId)
                .Task.WaitAsync(TimeSpan.FromSeconds(20));
            string digest = ComputeMaterializationDigest(menu);
            if (decision.RunId != menu.RunId
                || decision.OwnerNetId != menu.OwnerNetId
                || !string.Equals(decision.Digest, digest, StringComparison.Ordinal)
                || !decision.Approved)
            {
                throw new InvalidOperationException(
                    $"AP reward materialization {menu.MenuId} was rejected: {decision.Failure}"
                );
            }
        }
        finally
        {
            PendingDecisions.Remove(menu.MenuId);
        }
    }

    private static void ReportMaterialization(
        ApRewardMenuSpec menu,
        bool success,
        string failure = "")
    {
        INetGameService netService = RunManager.Instance.NetService;
        var ack = new MaterializationAck
        {
            RunId = menu.RunId,
            MenuId = menu.MenuId,
            OwnerNetId = menu.OwnerNetId,
            ReplicaNetId = netService.NetId,
            Digest = ComputeMaterializationDigest(menu),
            Success = success,
            Failure = failure,
        };
        if (netService.Type == NetGameType.Host)
        {
            HandleMaterializationAck(netService.NetId, ack);
        }
        else if (!RitsuLibSidecarTypedMessageRegistry.SendToHost(
                     netService,
                     AckDescriptor,
                     ack))
        {
            throw new InvalidOperationException(
                "Could not report AP reward materialization to the host."
            );
        }
    }

    private static string ComputeMaterializationDigest(ApRewardMenuSpec menu) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            MenuSerializer.Serialize(menu)
        ))[..16];

    /// <summary>Opens the local player's current AP receipt catalog as a native reward screen.</summary>
    public static async Task<bool> OpenMenu()
    {
        int apLifecycleVersion = ArchipelagoRewardUI.ApLifecycleVersion;
        Player? player = GameUtility.CurrentPlayer;
        if (player?.RunState is not RunState runState)
            return false;

        if (ArchipelagoRewardUI.IsOpen)
            return true;

        if (MultiplayerSupport.IsLocalGuest)
        {
            // A vanilla guest has no AP receipt source. Do not advance the synchronized reward-set
            // sequence for a screen which can never originate a selection.
            var emptySet = new RewardsSet(player);
            ArchipelagoRewardUI.ShowNativeMenu(
                emptySet,
                Guid.NewGuid(),
                synchronized: false,
                initiallyEmpty: true
            );
            return true;
        }

        ApRewardMenuSpec spec;
        bool ownerMaterializationStarted = false;
        try
        {
            var approvedRelics = await RelicReceiptMultiplayer.ApproveMenu(
                player, RelicRewardUtility.GetMenuReservationCandidates(player));
            if (apLifecycleVersion != ArchipelagoRewardUI.ApLifecycleVersion
                || (MultiplayerSupport.IsRealMultiplayerRun
                    && !ArchipelagoRewardUI.CanBuildMenuAfterAwait(apLifecycleVersion)))
                return false;
            ownerMaterializationStarted = true;
            spec = await BuildOwnerMenuSpec(player, runState, approvedRelics);
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Could not build native AP reward menu: {ex}");
            if (ownerMaterializationStarted && MultiplayerSupport.IsRealMultiplayerRun)
            {
                MultiplayerSupport.InvalidateRunClaims(
                    "AP reward owner materialization failed"
                );
            }
            NotificationUtility.ShowRawText("Could not prepare AP rewards. Try opening the menu again.");
            return false;
        }

        if (MultiplayerSupport.IsRealMultiplayerRun)
        {
            INetGameService netService = RunManager.Instance.NetService;
            bool needsAgreement = RequiresMaterializationAgreement(spec);
            if (needsAgreement)
            {
                GetDecisionWaiter(spec.MenuId);
                if (netService.Type == NetGameType.Host)
                    RegisterHostAgreement(spec);
            }
            bool sent = netService.Type == NetGameType.Host
                ? RitsuLibSidecarSyncMessages.Broadcast(netService, MenuDescriptor, spec)
                : RitsuLibSidecarSyncMessages.SendToHostAndBroadcast(netService, MenuDescriptor, spec);
            if (!sent)
            {
                if (needsAgreement)
                {
                    PendingDecisions.Remove(spec.MenuId);
                    HostAgreements.Remove(spec.MenuId);
                }
                LogUtility.Error($"Could not publish AP reward menu {spec.MenuId} to every peer");
                MultiplayerSupport.InvalidateRunClaims(
                    $"AP reward menu {spec.MenuId} could not reach every replica"
                );
                NotificationUtility.ShowRawText("Could not synchronize the AP reward menu.");
                return false;
            }

            if (needsAgreement)
            {
                try
                {
                    ReportMaterialization(spec, success: true);
                    await WaitForMaterializationDecision(spec);
                }
                catch (Exception ex)
                {
                    LogUtility.Error($"AP reward materialization was not approved: {ex}");
                    MultiplayerSupport.InvalidateRunClaims(
                        $"AP reward materialization {spec.MenuId} was not approved"
                    );
                    NotificationUtility.ShowRawText(
                        "Could not verify AP reward generation on every player."
                    );
                    return false;
                }
            }

            if (apLifecycleVersion != ArchipelagoRewardUI.ApLifecycleVersion
                || !ArchipelagoRewardUI.CanBuildMenuAfterAwait(apLifecycleVersion))
            {
                return false;
            }
        }

        RewardsSet set = BuildRewardsSet(spec, player);
        Task completion = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(set);
        ArchipelagoRewardUI.ShowNativeMenu(
            set,
            spec.MenuId,
            synchronized: true,
            initiallyEmpty: set.Rewards.Count == 0
        );
        ObserveOwnerCompletion(spec, completion);
        await Task.Yield();
        return true;
    }

    private static async Task<ApRewardMenuSpec> BuildOwnerMenuSpec(
        Player player,
        RunState runState,
        IReadOnlySet<int>? approvedRelics)
    {
        Guid runId = Guid.Empty;
        if (MultiplayerSupport.IsRealMultiplayerRun)
        {
            if (!ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
                || shared.RunId == Guid.Empty)
            {
                throw new InvalidOperationException("No shared AP run state exists.");
            }
            runId = shared.RunId;
        }

        int apSlotId = MultiplayerSupport.PreparedApSlotId ?? 0;
        var menu = new ApRewardMenuSpec
        {
            RunId = runId,
            MenuId = Guid.NewGuid(),
            ApSlotId = apSlotId,
            OwnerNetId = player.NetId,
        };

        RelicRewardUtility.ReconcileBankedRewards(player, approvedRelics);

        ApGoldClaim? gold = ApGrantDispatcher.MaterializeGoldClaim();
        if (gold != null)
        {
            menu.Gold = new ApMenuGoldSpec
            {
                SourceAmount = gold.SourceAmount,
                GrantedAmount = gold.GrantedAmount,
                RedeemedRawAfter = gold.RedeemedRawAfter,
            };
        }

        IEnumerable<IndexedItemInfo> receipts = ArchipelagoClient.Progress.AllReceivedItems
            .Concat(MultiplayerSupport.PendingUnsupportedItems)
            .GroupBy(receipt => receipt.Index)
            .Select(group => group.First())
            .OrderBy(receipt => receipt.Index);

        foreach (IndexedItemInfo receipt in receipts)
        {
            MultiplayerFeature feature = MultiplayerSupport.GetFeatureForItem(receipt);
            bool featureEnabled = MultiplayerSupport.IsFeatureEnabled(feature);
            if (featureEnabled)
            {
                if (!ArchipelagoClient.Progress.IsAvailableInRewardMenu(receipt, player)
                    || !TryGetMirroredKind(receipt, out ApMirroredRewardKind kind))
                {
                    continue;
                }

                if (kind == ApMirroredRewardKind.Relic && approvedRelics != null
                    && !approvedRelics.Contains(receipt.Index)) continue;

                menu.Rewards.Add(await BuildAssignedSpec(receipt, player, apSlotId, kind));
                continue;
            }

            bool belongsToCharacter = receipt.Item.ItemId < 10000
                || receipt.Item.GetCharacterOffset() == player.GetCharacterOffset();
            if (!MultiplayerSupport.IsMultiplayerScope
                || !belongsToCharacter
                || ArchipelagoClient.Progress.UsedItems.Contains(receipt.Index))
            {
                continue;
            }

            menu.Rewards.Add(new ApMirroredRewardSpec
            {
                ApSlotId = apSlotId,
                ReceivedItemIndex = receipt.Index,
                OwnerNetId = player.NetId,
                Kind = ApMirroredRewardKind.Unavailable,
                ItemName = receipt.Item.ItemDisplayName,
                SenderName = receipt.Item.Player.Name,
                FoundLocation = receipt.Item.LocationDisplayName,
                UnavailableReason = $"Unavailable in experimental multiplayer ({feature}).",
            });
        }

        // Persist all newly materialized assignments in one revision after the complete menu
        // snapshot exists. A no-change call is intentionally a cheap no-op.
        if (!ApRunData.PublishLocalProgress(player))
            throw new InvalidOperationException("The AP reward assignments could not reach the host.");

        menu.Rewards = menu.Rewards
            .OrderBy(spec => GetNativeOrder(spec.Kind))
            .ThenBy(spec => spec.ReceivedItemIndex)
            .ToList();
        return menu;
    }

    private static async Task<ApMirroredRewardSpec> BuildAssignedSpec(
        IndexedItemInfo receipt,
        Player player,
        int apSlotId,
        ApMirroredRewardKind kind)
    {
        int itemIndex = receipt.Index;
        var spec = new ApMirroredRewardSpec
        {
            ApSlotId = apSlotId,
            ReceivedItemIndex = itemIndex,
            OwnerNetId = player.NetId,
            Kind = kind,
            ItemName = receipt.Item.ItemDisplayName,
            SenderName = receipt.Item.Player.Name,
            FoundLocation = receipt.Item.LocationDisplayName,
        };

        switch (kind)
        {
            case ApMirroredRewardKind.Card:
            {
                bool rare = receipt.Item.GetCharacterSpecificItemID() == ItemTable.APItem.RareCardReward;
                spec.IsRareCardReward = rare;
                spec.CardRewardActIndex = rare ? null : GameUtility.GetCardRewardActIndex(itemIndex, player);
                bool isNew = !ArchipelagoClient.Progress.CardAssignments.TryGetValue(
                    itemIndex,
                    out CardReward? existing
                );

                ApNativeCardReward reward;
                if (isNew)
                {
                    IApCardRewardMaterializer strategy = GetDefaultCardStrategy();
                    spec.MaterializationStrategyId = strategy.WireId;
                    spec.RequiresNativeMaterialization = strategy.RequiresReplicaMaterialization;
                    reward = await strategy.MaterializeOwner(spec, player);
                    ArchipelagoClient.Progress.CardAssignments[itemIndex] = reward;
                    LogUtility.Info(
                        $"Materialized AP card reward {spec.GrantId} with {strategy.WireId} "
                            + $"for player {player.NetId}"
                    );
                }
                else
                {
                    reward = existing as ApNativeCardReward
                        ?? RestoreCardReward(spec, player, existing!.Cards, existing.CanReroll);
                    spec.MaterializationStrategyId = string.IsNullOrEmpty(
                        reward.MaterializationStrategyId
                    )
                        ? OwnerFinalApRngStrategyId
                        : reward.MaterializationStrategyId;
                    spec.AppliedEffects = CloneEffects(reward.AppliedEffects);
                    reward.Configure(spec);
                    ArchipelagoClient.Progress.CardAssignments[itemIndex] = reward;
                }

                ReplicaCardAssignments[(player.NetId, itemIndex)] = reward;
                spec.CardCanReroll = reward.CanReroll;
                spec.CardHasBeenRevealed = reward.HasBeenRevealed;
                spec.SerializedModels = reward.Cards.Select(SerializeCard).ToList();
                break;
            }
            case ApMirroredRewardKind.Relic:
            {
                IReadOnlyList<RelicModel> choices =
                    ArchipelagoClient.Progress.GetOrAssignRelicChoices(itemIndex, player, 1);
                if (choices.Count != 1)
                    throw new InvalidOperationException($"Could not assign relic reward {itemIndex}.");
                spec.SerializedModels.Add(SerializeRelic(choices[0]));
                break;
            }
            case ApMirroredRewardKind.Potion:
            {
                bool isNew = !ArchipelagoClient.Progress.PotionAssignments.TryGetValue(
                    itemIndex,
                    out PotionModel? potion
                );
                IApPotionRewardMaterializer strategy = isNew
                    ? GetDefaultPotionStrategy()
                    : OwnerFinalPotionStrategy;
                spec.MaterializationStrategyId = strategy.WireId;
                spec.RequiresNativeMaterialization =
                    isNew && strategy.RequiresReplicaMaterialization;
                if (isNew)
                {
                    potion = strategy.MaterializeOwner(spec, player);
                    ArchipelagoClient.Progress.PotionAssignments[itemIndex] = potion;
                    LogUtility.Info(
                        $"Materialized AP potion reward {spec.GrantId} with {strategy.WireId} "
                            + $"as {potion.Id}"
                    );
                }

                ReplicaPotionAssignments[(player.NetId, itemIndex)] = potion!;
                spec.SerializedModels.Add(SerializePotion(potion!));
                break;
            }
            case ApMirroredRewardKind.Ancient:
            {
                string? choiceKey = MultiplayerSupport.IsRealMultiplayerRun
                    ? $"{player.NetId}:{itemIndex}"
                    : null;
                IReadOnlyList<RelicModel> choices =
                    ArchipelagoClient.Progress.GetOrAssignAncientRelicChoices(
                        itemIndex,
                        player,
                        choiceKey
                    );
                if (choices.Count != AncientRelicPool.ChoiceCount)
                {
                    // The old AP menu represented a surplus Progressive Ancient, or a choice
                    // whose pool could not be built, as an empty disabled chest. Preserve that
                    // fail-closed row without preventing every other native reward from opening.
                    spec.Kind = ApMirroredRewardKind.Unavailable;
                    spec.ItemName = "Ancient Relic Choice Unavailable";
                    spec.UnavailableReason =
                        "No valid Act 2/3 Ancient relic choice is available for this receipt.";
                    break;
                }
                spec.SerializedModels = choices.Select(SerializeRelic).ToList();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return spec;
    }

    private static RewardsSet BuildRewardsSet(ApRewardMenuSpec menu, Player owner)
    {
        var rewards = new List<Reward>();
        if (menu.Gold != null)
            rewards.Add(new ApNativeGoldReward(menu.Gold.ToClaim(), owner));

        foreach (ApMirroredRewardSpec spec in menu.Rewards)
            rewards.Add(BuildNativeReward(spec, owner));

        return new RewardsSet(owner).WithCustomRewards(rewards);
    }

    private static Reward BuildNativeReward(ApMirroredRewardSpec spec, Player owner)
    {
        return spec.Kind switch
        {
            ApMirroredRewardKind.Card => BuildCardReward(spec, owner),
            ApMirroredRewardKind.Relic => BuildStandardRelicReward(spec, owner),
            ApMirroredRewardKind.Potion => new ApNativePotionReward(
                GetReplicaPotionAssignment(spec, owner),
                owner,
                spec
            ),
            ApMirroredRewardKind.Ancient => BuildAncientReward(spec, owner),
            ApMirroredRewardKind.Unavailable => new ApUnavailableReward(
                spec.ItemName,
                spec.UnavailableReason,
                owner,
                spec
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind)),
        };
    }

    private static Reward BuildCardReward(ApMirroredRewardSpec spec, Player player)
    {
        if (!ReplicaCardAssignments.TryGetValue(
                (player.NetId, spec.ReceivedItemIndex),
                out ApNativeCardReward? reward))
        {
            reward = RestoreCardReward(
                spec,
                player,
                DeserializeCards(spec, player),
                spec.CardCanReroll
            );
            ReplicaCardAssignments[(player.NetId, spec.ReceivedItemIndex)] = reward;
        }

        ValidateSerializedModels(spec, reward.Cards.Select(SerializeCard), "card assignment");
        return reward;
    }

    private static PotionModel GetReplicaPotionAssignment(
        ApMirroredRewardSpec spec,
        Player player)
    {
        if (!ReplicaPotionAssignments.TryGetValue(
                (player.NetId, spec.ReceivedItemIndex),
                out PotionModel? potion))
        {
            potion = PotionModel.FromSerializable(
                Deserialize<SerializablePotion>(spec.SerializedModels.Single())
            );
            ReplicaPotionAssignments[(player.NetId, spec.ReceivedItemIndex)] = potion;
        }

        // PotionFactory returns canonical pool entries, but PotionReward owns and may mutate the
        // offered potion. Normalize older in-memory/save-restored assignments here as well as at
        // their creation sites so an existing pending receipt can recover without being rerolled.
        if (!potion.IsMutable)
        {
            potion = potion.ToMutable();
            ReplicaPotionAssignments[(player.NetId, spec.ReceivedItemIndex)] = potion;
            if (LocalContext.IsMe(player)
                && ArchipelagoClient.Progress.PotionAssignments.ContainsKey(
                    spec.ReceivedItemIndex
                ))
            {
                ArchipelagoClient.Progress.PotionAssignments[spec.ReceivedItemIndex] = potion;
            }
        }

        ValidateSerializedModels(spec, new[] { SerializePotion(potion) }, "potion assignment");
        return potion;
    }

    private static Reward BuildAncientReward(ApMirroredRewardSpec spec, Player player)
    {
        if (spec.SerializedModels.Count != AncientRelicPool.ChoiceCount)
            throw new InvalidOperationException($"Ancient reward {spec.GrantId} had invalid choices.");

        var children = spec.SerializedModels
            .Select(serialized =>
            {
                RelicModel relic = DeserializeRelic(serialized);
                // These are fresh, unclaimed choices. Some Ancient saved-property setters (for
                // example Pumpkin Candle at zero kindle and Pael's Tooth with no stored cards)
                // mark a deserialized model Disabled even though the native Ancient presents the
                // same fresh model as Normal until its AfterObtained initialization runs.
                relic.Status = RelicStatus.Normal;
                return (Reward)new ApNativeRelicReward(
                    relic,
                    player,
                    spec,
                    ApMirroredRewardKind.Ancient
                );
            })
            .ToList();
        return LinkedRewardSets.Create(children, player, LinkedRewardSelectionMode.ChooseOne);
    }

    private static Reward BuildStandardRelicReward(
        ApMirroredRewardSpec spec,
        Player player)
    {
        RelicModel relic = DeserializeRelic(spec.SerializedModels.Single());
        RelicReceiptMultiplayer.RecordMenuAssignment(player, spec.ReceivedItemIndex, spec.SerializedModels.Single());
        StandardRelicPool.ReserveChoice(player, relic);
        return new ApNativeRelicReward(
            relic,
            player,
            spec,
            ApMirroredRewardKind.Relic
        );
    }

    private static CardCreationOptions CreateCardOptions(Player player, bool rare)
    {
        CardRarityOddsType rarity = rare
            ? CardRarityOddsType.BossEncounter
            : CardRarityOddsType.RegularEncounter;
        return BetaMainCompatibility.WithCombatRewardCompatibility(
            new CardCreationOptions(
                new[] { player.Character.CardPool },
                CardCreationSource.Encounter,
                rarity
            )
        );
    }

    private static IApCardRewardMaterializer GetDefaultCardStrategy() =>
        UseStrictReplicaMaterializationForDiagnostics
            ? ReplicaNativeCardStrategy
            : OwnerFinalCardStrategy;

    private static IApPotionRewardMaterializer GetDefaultPotionStrategy() =>
        UseStrictReplicaMaterializationForDiagnostics
            ? ReplicaNativePotionStrategy
            : OwnerFinalPotionStrategy;

    private static IApCardRewardMaterializer GetCardStrategy(string wireId) => wireId switch
    {
        ReplicaNativeStrategyId => ReplicaNativeCardStrategy,
        OwnerFinalApRngStrategyId => OwnerFinalCardStrategy,
        _ => throw new InvalidOperationException(
            $"Unknown AP card materialization strategy '{wireId}'."
        ),
    };

    private static IApPotionRewardMaterializer GetPotionStrategy(string wireId) => wireId switch
    {
        ReplicaNativeStrategyId => ReplicaNativePotionStrategy,
        OwnerFinalApRngStrategyId => OwnerFinalPotionStrategy,
        _ => throw new InvalidOperationException(
            $"Unknown AP potion materialization strategy '{wireId}'."
        ),
    };

    private static Rng CreateApRewardRng(
        ApMirroredRewardSpec spec,
        Player player,
        string domain)
    {
        string seedMaterial = string.Join(
            "|",
            "sts2ap-reward-rng-v1",
            domain,
            player.RunState.Rng.StringSeed,
            spec.ApSlotId,
            player.RunState.GetPlayerSlotIndex(player),
            player.GetCharacterOffset(),
            spec.ReceivedItemIndex
        );
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seedMaterial));
        return new Rng(BinaryPrimitives.ReadUInt64LittleEndian(digest));
    }

    private static async Task<ApNativeCardReward> MaterializeReplicaNativeCardReward(
        ApMirroredRewardSpec spec,
        Player player)
    {
        CardCreationOptions options = CreateCardOptions(player, spec.IsRareCardReward)
            .WithFlags(CardCreationFlags.IsCardReward);

        List<CardCreationResult> cards;
        List<AbstractModel> modifiers;
        bool modified;
        using (Patches_APCardRewardUpgradeOdds.EnterRewardAct(spec.CardRewardActIndex))
        {
            cards = Patches_APCardRewardUpgradeOdds.RunDeferringOptionHooks(
                () => CardFactory.CreateForReward(player, 3, options).ToList()
            );
            modified = Hook.TryModifyCardRewardOptions(
                player.RunState,
                player,
                cards,
                options,
                out modifiers
            );
        }
        if (modified)
            await Hook.AfterModifyingCardRewardOptions(player.RunState, modifiers);

        return new ApNativeCardReward(cards, player, options, spec, canReroll: false);
    }

    private static async Task<ApNativeCardReward> MaterializeOwnerFinalApRngCardReward(
        ApMirroredRewardSpec spec,
        Player player)
    {
        if (player.RunState is not RunState runState)
            throw new InvalidOperationException("AP reward generation requires a native run state.");
        var allCards = RunStateAllCardsField.GetValue(runState) as List<CardModel>
            ?? throw new InvalidOperationException("Could not inspect the native run card registry.");
        var preexistingCards = allCards.ToHashSet();
        Rng rng = CreateApRewardRng(spec, player, "card");
        CardCreationOptions options = CreateCardOptions(player, spec.IsRareCardReward)
            .WithFlags(CardCreationFlags.IsCardReward)
            .WithRngOverride(rng);

        int silkenBefore = player.GetRelic<SilkenTress>()?.IsUsedUp == true ? 1 : 0;
        int? crucibleBefore = player.GetRelic<SilverCrucible>()?.TimesUsed;
        List<CardCreationResult> cards;
        List<AbstractModel> modifiers;
        bool modified;
        using (Patches_APCardRewardUpgradeOdds.EnterApRewardRng(rng))
        using (Patches_APCardRewardUpgradeOdds.EnterRewardAct(spec.CardRewardActIndex))
        {
            cards = Patches_APCardRewardUpgradeOdds.RunDeferringOptionHooks(
                () => CardFactory.CreateForReward(player, 3, options).ToList()
            );

            modified = Hook.TryModifyCardRewardOptions(
                player.RunState,
                player,
                cards,
                options,
                out modifiers
            );
        }
        if (modified)
            await Hook.AfterModifyingCardRewardOptions(player.RunState, modifiers);

        var effects = new List<ApRewardEffectSpec>();
        int silkenAfter = player.GetRelic<SilkenTress>()?.IsUsedUp == true ? 1 : 0;
        if (silkenBefore != silkenAfter)
        {
            effects.Add(new ApRewardEffectSpec
            {
                EffectId = SilkenTressEffectId,
                BeforeValue = silkenBefore,
                AfterValue = silkenAfter,
            });
        }
        int? crucibleAfter = player.GetRelic<SilverCrucible>()?.TimesUsed;
        if (crucibleBefore.HasValue && crucibleAfter.HasValue
            && crucibleBefore.Value != crucibleAfter.Value)
        {
            effects.Add(new ApRewardEffectSpec
            {
                EffectId = SilverCrucibleEffectId,
                BeforeValue = crucibleBefore.Value,
                AfterValue = crucibleAfter.Value,
            });
        }
        spec.AppliedEffects = effects;

        // Final cards are the wire contract. Remove every temporary original/clone created by
        // native hooks and reload only those finals, matching the representation other replicas use.
        List<string> serializedFinalCards = cards.Select(result => SerializeCard(result.Card)).ToList();
        foreach (CardModel temporary in allCards
                     .Where(card => !preexistingCards.Contains(card))
                     .ToList())
        {
            runState.RemoveCard(temporary);
        }
        List<CardModel> normalizedCards = serializedFinalCards
            .Select(serialized => runState.LoadCard(
                Deserialize<SerializableCard>(serialized),
                player
            ))
            .ToList();
        spec.SerializedModels = serializedFinalCards;
        return RestoreCardReward(spec, player, normalizedCards, canReroll: false);
    }

    private static ApNativeCardReward RestoreCardReward(
        ApMirroredRewardSpec spec,
        Player player,
        IEnumerable<CardModel> cards,
        bool canReroll) =>
        new(
            cards.Select(card => new CardCreationResult(card)).ToList(),
            player,
            CreateCardOptions(player, spec.IsRareCardReward)
                .WithFlags(CardCreationFlags.IsCardReward),
            spec,
            canReroll
        );

    internal static CardReward RestorePersistedCardAssignment(
        int itemIndex,
        ApCardAssignmentState assignment,
        Player player,
        IReadOnlyList<CardModel> cards)
    {
        var spec = new ApMirroredRewardSpec
        {
            ReceivedItemIndex = itemIndex,
            OwnerNetId = player.NetId,
            Kind = ApMirroredRewardKind.Card,
            IsRareCardReward = assignment.IsRare,
            CardRewardActIndex = assignment.RewardActIndex,
            CardHasBeenRevealed = assignment.HasBeenRevealed,
            MaterializationStrategyId = string.IsNullOrEmpty(assignment.MaterializationStrategyId)
                ? OwnerFinalApRngStrategyId
                : assignment.MaterializationStrategyId,
            AppliedEffects = CloneEffects(assignment.AppliedEffects),
        };
        ApNativeCardReward reward = RestoreCardReward(
            spec,
            player,
            cards,
            assignment.CanReroll
        );
        ReplicaCardAssignments[(player.NetId, itemIndex)] = reward;
        return reward;
    }

    private static List<CardModel> DeserializeCards(ApMirroredRewardSpec spec, Player player) =>
        spec.SerializedModels
            .Select(serialized => player.RunState.LoadCard(
                Deserialize<SerializableCard>(serialized),
                player
            ))
            .ToList();

    private static string CaptureMaterializationState(Player player)
    {
        if (player.RunState is not RunState runState)
            throw new InvalidOperationException("AP reward materialization requires a native run state.");
        var allCards = RunStateAllCardsField.GetValue(runState) as List<CardModel>
            ?? throw new InvalidOperationException("Could not inspect the native run card registry.");

        return StableHash(Serialize(new
        {
            Player = player.ToSerializable(),
            RunRng = runState.Rng.ToSerializable(),
            RunOdds = runState.Odds.ToSerializable(),
            AllCards = allCards.Select(SerializeCard)
                .OrderBy(serialized => serialized, StringComparer.Ordinal)
                .ToList(),
        }));
    }

    private static void ValidateMaterializationState(
        ApMirroredRewardSpec spec,
        Player player,
        string expected,
        string boundary)
    {
        string actual = CaptureMaterializationState(player);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AP {spec.Kind} materialization {spec.GrantId} had a mismatched {boundary} "
                    + $"state (expectedHash={expected}, actualHash={actual})."
            );
        }
    }

    private static void ValidateSerializedModels(
        ApMirroredRewardSpec spec,
        IEnumerable<string> actualModels,
        string description)
    {
        string[] actual = actualModels.ToArray();
        if (!spec.SerializedModels.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"AP {description} {spec.GrantId} differed after native materialization "
                    + $"(expectedHash={StableHash(string.Join("\n", spec.SerializedModels))}, "
                    + $"actualHash={StableHash(string.Join("\n", actual))})."
            );
        }
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)
        ))[..16];

    private static List<ApRewardEffectSpec> CloneEffects(
        IEnumerable<ApRewardEffectSpec> effects) => effects
        .Select(effect => new ApRewardEffectSpec
        {
            EffectId = effect.EffectId,
            BeforeValue = effect.BeforeValue,
            AfterValue = effect.AfterValue,
        })
        .ToList();

    private static bool RequiresMaterializationAgreement(ApRewardMenuSpec menu) =>
        menu.Rewards.Any(reward => reward.RequiresNativeMaterialization);

    private static async Task ApplyOwnerFinalEffects(
        ApRewardMenuSpec menu,
        Player owner)
    {
        foreach (ApRewardEffectSpec effect in menu.Rewards
                     .OrderBy(reward => reward.ReceivedItemIndex)
                     .SelectMany(reward => reward.AppliedEffects))
        {
            switch (effect.EffectId)
            {
                case SilkenTressEffectId:
                {
                    SilkenTress relic = owner.GetRelic<SilkenTress>()
                        ?? throw new InvalidOperationException(
                            "An AP reward expected Silken Tress, but the replica did not have it."
                        );
                    int current = relic.IsUsedUp ? 1 : 0;
                    if (current == effect.AfterValue)
                        break;
                    if (current != effect.BeforeValue)
                        throw new InvalidOperationException(
                            $"Silken Tress AP effect expected {effect.BeforeValue}, found {current}."
                        );
                    await relic.AfterModifyingCardRewardOptions();
                    relic.InvokeExecutionFinished();
                    int after = relic.IsUsedUp ? 1 : 0;
                    if (after != effect.AfterValue)
                        throw new InvalidOperationException(
                            $"Silken Tress AP effect produced {after}, expected {effect.AfterValue}."
                        );
                    break;
                }
                case SilverCrucibleEffectId:
                {
                    SilverCrucible relic = owner.GetRelic<SilverCrucible>()
                        ?? throw new InvalidOperationException(
                            "An AP reward expected Silver Crucible, but the replica did not have it."
                        );
                    // A later pending AP assignment may already have advanced this monotonic
                    // counter past an earlier persisted transition in the same reopened menu.
                    if (relic.TimesUsed >= effect.AfterValue)
                        break;
                    if (relic.TimesUsed != effect.BeforeValue)
                        throw new InvalidOperationException(
                            $"Silver Crucible AP effect expected {effect.BeforeValue}, "
                                + $"found {relic.TimesUsed}."
                        );
                    await relic.AfterModifyingCardRewardOptions();
                    relic.InvokeExecutionFinished();
                    if (relic.TimesUsed != effect.AfterValue)
                        throw new InvalidOperationException(
                            $"Silver Crucible AP effect produced {relic.TimesUsed}, "
                                + $"expected {effect.AfterValue}."
                        );
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Unknown AP card-reward effect '{effect.EffectId}'."
                    );
            }
        }
    }

    private static async Task PrepareReplicaMaterializations(
        ApRewardMenuSpec menu,
        Player owner)
    {
        foreach (ApMirroredRewardSpec spec in menu.Rewards
                     .Where(reward => reward.RequiresNativeMaterialization)
                     .OrderBy(reward => reward.ReceivedItemIndex))
        {
            switch (spec.Kind)
            {
                case ApMirroredRewardKind.Card:
                {
                    if (ReplicaCardAssignments.TryGetValue(
                            (owner.NetId, spec.ReceivedItemIndex),
                            out ApNativeCardReward? existing))
                    {
                        ValidateSerializedModels(
                            spec,
                            existing.Cards.Select(SerializeCard),
                            "card assignment"
                        );
                        break;
                    }

                    IApCardRewardMaterializer strategy = GetCardStrategy(
                        spec.MaterializationStrategyId
                    );
                    if (!strategy.RequiresReplicaMaterialization)
                        throw new InvalidOperationException(
                            $"AP card strategy {strategy.WireId} requested unnecessary replica generation."
                        );
                    ApNativeCardReward reward = await strategy.MaterializeReplica(spec, owner);
                    ReplicaCardAssignments[(owner.NetId, spec.ReceivedItemIndex)] = reward;
                    break;
                }
                case ApMirroredRewardKind.Potion:
                {
                    if (ReplicaPotionAssignments.TryGetValue(
                            (owner.NetId, spec.ReceivedItemIndex),
                            out PotionModel? existing))
                    {
                        ValidateSerializedModels(
                            spec,
                            new[] { SerializePotion(existing) },
                            "potion assignment"
                        );
                        break;
                    }

                    IApPotionRewardMaterializer strategy = GetPotionStrategy(
                        spec.MaterializationStrategyId
                    );
                    if (!strategy.RequiresReplicaMaterialization)
                        throw new InvalidOperationException(
                            $"AP potion strategy {strategy.WireId} requested unnecessary replica generation."
                        );
                    PotionModel potion = strategy.MaterializeReplica(spec, owner);
                    ReplicaPotionAssignments[(owner.NetId, spec.ReceivedItemIndex)] = potion;
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"AP reward {spec.GrantId} requested unsupported native materialization "
                            + $"for {spec.Kind}."
                    );
            }
        }
    }

    private static Task HandleMenuSpec(
        RitsuLibSidecarSyncMessageContext<ApRewardMenuSpec> context)
    {
        ApRewardMenuSpec menu = context.Message;
        if (menu.SchemaVersion != 5 || context.SenderNetId != menu.OwnerNetId)
            throw new InvalidOperationException("Invalid AP reward-menu owner or schema.");

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        bool posted = RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
            CompleteRemoteMenu(menu, completion)
        );
        if (!posted)
        {
            completion.SetException(
                new InvalidOperationException("Godot main loop was unavailable for AP reward menu.")
            );
        }
        return completion.Task;
    }

    private static void ValidateMenuOnHost(ApRewardMenuSpec menu)
    {
        if (!TryGetCurrentMenuOwner(menu, out RunState runState, out ApPlayerRunState ownerState))
            throw new InvalidOperationException("AP reward menu did not match the active run owner.");

        if (ownerState.Participation == ApParticipationKind.OwnApSlot
            && ownerState.ApSlotId != menu.ApSlotId)
        {
            throw new InvalidOperationException("AP reward menu did not match its owner's slot.");
        }

        foreach (ApMirroredRewardSpec reward in menu.Rewards)
        {
            if (reward.SchemaVersion != 5)
                throw new InvalidOperationException("Invalid AP reward-menu entry schema.");
            if (reward.OwnerNetId != menu.OwnerNetId || reward.ApSlotId != menu.ApSlotId)
                throw new InvalidOperationException("AP reward-menu entry had mismatched ownership.");
            if (ApRunData.IsReceiptUsed(runState, menu.OwnerNetId, reward.ReceivedItemIndex))
                throw new InvalidOperationException($"AP receipt {reward.GrantId} was already consumed.");
            if (reward.Kind == ApMirroredRewardKind.Relic
                && !RelicReceiptMultiplayer.State(runState).CanUseMenu(
                    menu.OwnerNetId, reward.ReceivedItemIndex))
                throw new InvalidOperationException($"AP relic {reward.GrantId} has no host menu reservation.");

            if (reward.Kind is ApMirroredRewardKind.Card or ApMirroredRewardKind.Potion)
            {
                bool strict = reward.MaterializationStrategyId == ReplicaNativeStrategyId;
                bool ownerFinal = reward.MaterializationStrategyId == OwnerFinalApRngStrategyId;
                if (!strict && !ownerFinal)
                    throw new InvalidOperationException(
                        $"AP reward {reward.GrantId} used an unknown materialization strategy."
                    );
                if (reward.RequiresNativeMaterialization && !strict)
                    throw new InvalidOperationException(
                        $"AP reward {reward.GrantId} had an inconsistent materialization contract."
                    );
            }
            else if (reward.RequiresNativeMaterialization || reward.AppliedEffects.Count > 0)
            {
                throw new InvalidOperationException(
                    $"AP reward {reward.GrantId} attached card/potion materialization data to {reward.Kind}."
                );
            }

            if (reward.AppliedEffects.Select(effect => effect.EffectId).Distinct().Count()
                != reward.AppliedEffects.Count)
                throw new InvalidOperationException(
                    $"AP reward {reward.GrantId} repeated a persistent effect."
                );
            foreach (ApRewardEffectSpec effect in reward.AppliedEffects)
            {
                bool valid = effect.EffectId switch
                {
                    SilkenTressEffectId => effect.BeforeValue == 0 && effect.AfterValue == 1,
                    SilverCrucibleEffectId => effect.BeforeValue >= 0
                        && effect.AfterValue == effect.BeforeValue + 1,
                    _ => false,
                };
                if (reward.Kind != ApMirroredRewardKind.Card
                    || reward.MaterializationStrategyId != OwnerFinalApRngStrategyId
                    || !valid)
                {
                    throw new InvalidOperationException(
                        $"AP reward {reward.GrantId} had invalid effect '{effect.EffectId}'."
                    );
                }
            }
        }
    }

    private static bool TryGetCurrentMenuOwner(
        ApRewardMenuSpec menu,
        out RunState runState,
        out ApPlayerRunState ownerState)
    {
        runState = null!;
        ownerState = null!;
        if (RunManager.Instance.DebugOnlyGetState() is not RunState current
            || !ApRunData.TryGetSharedState(current, out ApRunSharedState shared)
            || shared.RunId != menu.RunId
            || !ApRunData.TryGetPlayerState(current, menu.OwnerNetId, out ownerState)
            || ownerState.Participation == ApParticipationKind.VanillaGuest)
        {
            return false;
        }
        runState = current;
        return true;
    }

    private static async void CompleteRemoteMenu(
        ApRewardMenuSpec menu,
        TaskCompletionSource sidecarCompletion)
    {
        var key = (menu.OwnerNetId, menu.MenuId);
        bool materializationReported = false;
        bool needsAgreement = RequiresMaterializationAgreement(menu);
        try
        {
            if (!ActiveRemoteMenus.Add(key))
                throw new InvalidOperationException($"AP reward menu {menu.MenuId} is already active.");
            if (!TryGetCurrentMenuOwner(menu, out RunState runState, out _))
                throw new InvalidOperationException("No matching player exists for the AP reward menu.");
            Player owner = runState.GetPlayer(menu.OwnerNetId)
                ?? throw new InvalidOperationException($"Player {menu.OwnerNetId} is not in the run.");
            if (RunManager.Instance.NetService.Type == NetGameType.Host)
            {
                if (needsAgreement)
                    RegisterHostAgreement(menu);
                ValidateMenuOnHost(menu);
            }
            if (needsAgreement)
                GetDecisionWaiter(menu.MenuId);
            await RelicReceiptMultiplayer.WaitForMenuReservations(owner,
                menu.Rewards.Where(r => r.Kind == ApMirroredRewardKind.Relic).Select(r => r.ReceivedItemIndex));
            if (needsAgreement)
            {
                await PrepareReplicaMaterializations(menu, owner);
                ReportMaterialization(menu, success: true);
                materializationReported = true;
                await WaitForMaterializationDecision(menu);
            }
            else
            {
                await ApplyOwnerFinalEffects(menu, owner);
            }
            RewardsSet set = BuildRewardsSet(menu, owner);
            await RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(set);
            sidecarCompletion.SetResult();
        }
        catch (Exception ex)
        {
            if (needsAgreement && !materializationReported
                && MultiplayerSupport.IsRealMultiplayerRun)
            {
                try
                {
                    ReportMaterialization(menu, success: false, ex.GetBaseException().Message);
                }
                catch (Exception reportException)
                {
                    LogUtility.Error(
                        $"Could not report failed AP materialization {menu.MenuId}: {reportException}"
                    );
                }
            }
            if (needsAgreement)
                PendingDecisions.Remove(menu.MenuId);
            sidecarCompletion.SetException(ex);
            if (MultiplayerSupport.IsRealMultiplayerRun && TryGetCurrentMenuOwner(menu, out _, out _))
                MultiplayerSupport.InvalidateRunClaims($"remote AP reward menu {menu.MenuId} failed");
        }
        finally
        {
            ActiveRemoteMenus.Remove(key);
        }
    }

    private static async void ObserveOwnerCompletion(ApRewardMenuSpec menu, Task completion)
    {
        try
        {
            await completion;
            LogUtility.Debug($"Native AP reward menu {menu.MenuId} completed");
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Native AP reward menu {menu.MenuId} failed: {ex}");
            MultiplayerSupport.InvalidateRunClaims($"AP reward menu {menu.MenuId} failed");
        }
    }

    internal static bool CommitDiscreteReward(int itemIndex, ApMirroredRewardKind kind)
    {
        if (!ArchipelagoClient.Progress.UsedItems.Contains(itemIndex))
            ArchipelagoClient.Progress.UsedItems.Add(itemIndex);

        switch (kind)
        {
            case ApMirroredRewardKind.Card:
                ArchipelagoClient.Progress.CardAssignments.Remove(itemIndex);
                break;
            case ApMirroredRewardKind.Relic:
                ArchipelagoClient.Progress.RelicChoiceAssignments.Remove(itemIndex);
                break;
        }

        int apSlotId = MultiplayerSupport.PreparedApSlotId ?? 0;
        LastAttempts[(new ApGrantId(apSlotId, itemIndex), kind)] = "applied";
        Player? player = GameUtility.CurrentPlayer;
        if (player != null && ApRunData.PublishLocalProgress(player))
            return true;

        MultiplayerSupport.InvalidateRunClaims(
            $"AP {kind} receipt {itemIndex} applied but its progress could not reach the host"
        );
        return false;
    }

    public static IReadOnlyList<ApGrantSnapshot> CaptureGrantSnapshots()
    {
        Player? player = GameUtility.CurrentPlayer;
        ulong ownerNetId = player?.NetId ?? 0;
        int apSlotId = MultiplayerSupport.PreparedApSlotId ?? 0;
        return ArchipelagoClient.Progress.AllReceivedItems
            .Where(receipt => TryGetMirroredKind(receipt, out _))
            .OrderBy(receipt => receipt.Index)
            .Select(receipt =>
            {
                TryGetMirroredKind(receipt, out ApMirroredRewardKind kind);
                bool applied = ArchipelagoClient.Progress.UsedItems.Contains(receipt.Index);
                string? blocked = null;
                ApGrantState state = applied
                    ? ApGrantState.Applied
                    : player != null && MultiplayerSupport.CanClaimReceivedReward(kind, out blocked)
                        ? ApGrantState.Claimable
                        : ApGrantState.Blocked;
                return new ApGrantSnapshot(
                    new ApGrantId(apSlotId, receipt.Index),
                    receipt.Item.ItemDisplayName,
                    ownerNetId,
                    kind,
                    state,
                    DescribeAssignment(kind, receipt.Index),
                    blocked,
                    LastAttempts.GetValueOrDefault((new ApGrantId(apSlotId, receipt.Index), kind))
                );
            })
            .ToArray();
    }

    private static int GetNativeOrder(ApMirroredRewardKind kind) => kind switch
    {
        ApMirroredRewardKind.Potion => 2,
        ApMirroredRewardKind.Relic or ApMirroredRewardKind.Ancient => 3,
        ApMirroredRewardKind.Card => 5,
        _ => 99,
    };

    private static bool TryGetMirroredKind(
        IndexedItemInfo receipt,
        out ApMirroredRewardKind kind)
    {
        kind = default;
        if (receipt.Item.ItemId < 10000)
            return false;
        switch (receipt.Item.GetCharacterSpecificItemID())
        {
            case ItemTable.APItem.CardReward:
            case ItemTable.APItem.RareCardReward:
                kind = ApMirroredRewardKind.Card;
                return true;
            case ItemTable.APItem.Relic:
                kind = ApMirroredRewardKind.Relic;
                return true;
            case ItemTable.APItem.Potion:
                kind = ApMirroredRewardKind.Potion;
                return true;
            case ItemTable.APItem.ProgressiveAncient:
                kind = ApMirroredRewardKind.Ancient;
                return true;
            default:
                return false;
        }
    }

    private static string DescribeAssignment(ApMirroredRewardKind kind, int itemIndex)
    {
        try
        {
            return kind switch
            {
                ApMirroredRewardKind.Card
                    when ArchipelagoClient.Progress.CardAssignments.TryGetValue(
                        itemIndex,
                        out CardReward? card) => string.Join(", ", card.Cards.Select(model =>
                            $"{model.Title} [{model.Id.Entry}]")),
                ApMirroredRewardKind.Relic
                    when ArchipelagoClient.Progress.RelicChoiceAssignments.TryGetValue(
                        itemIndex,
                        out List<RelicModel>? relics) => string.Join(", ", relics.Select(model =>
                            $"{model.Title.GetRawText()} [{model.Id.Entry}]")),
                ApMirroredRewardKind.Ancient
                    when ArchipelagoClient.Progress.AncientRelicChoiceAssignments.TryGetValue(
                        itemIndex,
                        out List<RelicModel>? ancients) => string.Join(", ", ancients.Select(model =>
                            $"{model.Title.GetRawText()} [{model.Id.Entry}]")),
                ApMirroredRewardKind.Potion
                    when ArchipelagoClient.Progress.PotionAssignments.TryGetValue(
                        itemIndex,
                        out PotionModel? potion) =>
                    $"{potion.Title.GetRawText()} [{potion.Id.Entry}]",
                _ => "<unassigned>",
            };
        }
        catch (Exception ex)
        {
            return $"<invalid assignment: {ex.GetBaseException().Message}>";
        }
    }

    private static string SerializeCard(CardModel card) => Serialize(card.ToSerializable());

    private static string SerializeRelic(RelicModel relic) =>
        Serialize((relic.IsMutable ? relic : relic.ToMutable()).ToSerializable());

    private static string SerializePotion(PotionModel potion) =>
        Serialize((potion.IsMutable ? potion : potion.ToMutable()).ToSerializable(-1));

    private static RelicModel DeserializeRelic(string json) =>
        RelicModel.FromSerializable(Deserialize<SerializableRelic>(json));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializationUtility.CombinedOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializationUtility.CombinedOptions)
        ?? throw new InvalidOperationException($"Could not deserialize AP model {typeof(T).Name}.");

    private static int _descriptionSequence;
    private const int RewardOriginFontSize = 16;

    private static LocString CreateApDescription(LocString primary, ApMirroredRewardSpec spec) =>
        CreateApDescription(primary.GetFormattedText(), spec);

    private static LocString CreateApDescription(string primary, ApMirroredRewardSpec spec)
    {
        string location = string.IsNullOrWhiteSpace(spec.FoundLocation)
            ? string.Empty
            : $" ({spec.FoundLocation})";
        string origin = string.IsNullOrWhiteSpace(spec.SenderName)
            ? string.Empty
            : $"\n[font_size={RewardOriginFontSize}]"
                + $"[blue]from {spec.SenderName}{location}[/blue][/font_size]";
        string key = $"AP_NATIVE_REWARD_{System.Threading.Interlocked.Increment(ref _descriptionSequence)}";
        TextUtility.RegisterLocString(key, primary + origin, "ap");
        return new LocString("ap", key);
    }

    internal interface IApNativeReward
    {
        bool CanClaim(out string reason);
        bool HasOriginText { get; }
        bool UseAncientStyle { get; }
    }

    private sealed class ApNativeGoldReward : GoldReward, IApNativeReward
    {
        private readonly ApGoldClaim _claim;

        public ApNativeGoldReward(ApGoldClaim claim, Player player)
            : base(claim.GrantedAmount, player) => _claim = claim;

        public bool CanClaim(out string reason) => MultiplayerSupport.CanClaimGold(out reason);
        public bool HasOriginText => false;
        public bool UseAncientStyle => false;

        protected override async Task<bool> OnSelect()
        {
            bool applied = await base.OnSelect();
            if (applied && LocalContext.IsMe(Player))
                ApGrantDispatcher.CommitGoldClaim(_claim);
            return applied;
        }
    }

    private sealed class ApNativeRelicReward : RelicReward, IApNativeReward
    {
        private readonly int _itemIndex;
        private readonly ApMirroredRewardKind _kind;
        private readonly LocString _description;

        public override LocString Description => _description;

        public ApNativeRelicReward(
            RelicModel relic,
            Player player,
            ApMirroredRewardSpec spec,
            ApMirroredRewardKind kind)
            : base(relic, player)
        {
            _itemIndex = spec.ReceivedItemIndex;
            _kind = kind;
            _description = CreateApDescription(relic.Title, spec);
        }

        public bool CanClaim(out string reason) =>
            MultiplayerSupport.CanClaimReceivedReward(_kind, out reason);
        public bool HasOriginText => true;
        public bool UseAncientStyle => _kind == ApMirroredRewardKind.Ancient;

        protected override async Task<bool> OnSelect()
        {
            if (_kind == ApMirroredRewardKind.Relic && !RelicReceiptMultiplayer.CanUseMenu(Player, _itemIndex))
                throw new InvalidOperationException($"AP relic receipt {Player.NetId}:{_itemIndex} is not approved for this menu.");
            bool applied = await base.OnSelect();
            if (applied && _kind == ApMirroredRewardKind.Relic)
                RelicReceiptMultiplayer.ConsumeMenu(Player, _itemIndex);
            if (applied && LocalContext.IsMe(Player))
                CommitDiscreteReward(_itemIndex, _kind);
            return applied;
        }

        public override void OnSkipped() { }
    }

    private sealed class ApNativePotionReward : PotionReward, IApNativeReward
    {
        private readonly int _itemIndex;
        private readonly LocString _description;

        public override LocString Description => _description;

        public ApNativePotionReward(
            PotionModel potion,
            Player player,
            ApMirroredRewardSpec spec)
            : base(potion, player)
        {
            _itemIndex = spec.ReceivedItemIndex;
            _description = CreateApDescription(potion.Title, spec);
        }

        public bool CanClaim(out string reason) =>
            MultiplayerSupport.CanClaimReceivedReward(ApMirroredRewardKind.Potion, out reason);
        public bool HasOriginText => true;
        public bool UseAncientStyle => false;

        protected override async Task<bool> OnSelect()
        {
            bool applied = await base.OnSelect();
            if (applied)
                ReplicaPotionAssignments.Remove((Player.NetId, _itemIndex));
            if (applied && LocalContext.IsMe(Player))
                CommitDiscreteReward(_itemIndex, ApMirroredRewardKind.Potion);
            return applied;
        }

        public override void OnSkipped() { }
    }

    internal sealed class ApNativeCardReward : CardReward, IApNativeReward
    {
        private readonly int _itemIndex;
        private bool _isRare;
        private int? _rewardActIndex;
        private bool _hasBeenRevealed;
        private string _materializationStrategyId = string.Empty;
        private List<ApRewardEffectSpec> _appliedEffects = new();
        private LocString _description;

        internal bool IsRare => _isRare;
        internal int? RewardActIndex => _rewardActIndex;
        internal bool HasBeenRevealed => _hasBeenRevealed;
        internal string MaterializationStrategyId => _materializationStrategyId;
        internal IReadOnlyList<ApRewardEffectSpec> AppliedEffects => _appliedEffects;

        protected override string IconPath => _isRare
            ? ImageHelper.GetImagePath("ui/reward_screen/reward_icon_rare.png")
            : base.IconPath;

        public override LocString Description => _description;

        public ApNativeCardReward(
            IReadOnlyList<CardCreationResult> cards,
            Player player,
            CardCreationOptions options,
            ApMirroredRewardSpec spec,
            bool canReroll)
            : base(options, cards.Count, player)
        {
            var nativeCards = CardRewardCardsField.GetValue(this) as List<CardCreationResult>
                ?? throw new InvalidOperationException("Could not access native CardReward choices.");
            nativeCards.AddRange(cards);
            _itemIndex = spec.ReceivedItemIndex;
            _description = new LocString("gameplay_ui", "COMBAT_REWARD_ADD_CARD");
            Configure(spec);
            CanReroll = canReroll;
        }

        internal void Configure(ApMirroredRewardSpec spec)
        {
            _isRare = spec.IsRareCardReward;
            _rewardActIndex = spec.CardRewardActIndex;
            _hasBeenRevealed = spec.CardHasBeenRevealed;
            if (!string.IsNullOrEmpty(spec.MaterializationStrategyId))
                _materializationStrategyId = spec.MaterializationStrategyId;
            _appliedEffects = CloneEffects(spec.AppliedEffects);
            _description = CreateApDescription(
                new LocString("gameplay_ui", "COMBAT_REWARD_ADD_CARD"),
                spec
            );
        }

        public bool CanClaim(out string reason) =>
            MultiplayerSupport.CanClaimReceivedReward(ApMirroredRewardKind.Card, out reason);
        public bool HasOriginText => true;
        public bool UseAncientStyle => false;

        protected override async Task<bool> OnSelect()
        {
            bool newlyRevealed = !_hasBeenRevealed;
            _hasBeenRevealed = true;
            HashSet<CardModel>? deckBefore = LocalContext.IsMe(Player)
                ? Player.Deck.Cards.ToHashSet()
                : null;
            bool applied = await base.OnSelect();
            if (!applied)
            {
                if (newlyRevealed && LocalContext.IsMe(Player)
                    && !ApRunData.PublishLocalProgress(Player))
                {
                    MultiplayerSupport.InvalidateRunClaims(
                        $"AP card receipt {_itemIndex} was revealed but its progress "
                            + "could not reach the host"
                    );
                }
                return applied;
            }
            ReplicaCardAssignments.Remove((Player.NetId, _itemIndex));
            if (!LocalContext.IsMe(Player))
                return true;

            foreach (CardModel selected in Player.Deck.Cards
                         .Where(card => deckBefore != null && !deckBefore.Contains(card)))
            {
                await GameUtility.AddCardRewardToCombatDrawPile(selected, Player);
            }
            CommitDiscreteReward(_itemIndex, ApMirroredRewardKind.Card);
            return true;
        }

        public override void OnSkipped() { }
    }

    private sealed class ApUnavailableReward : Reward, IApNativeReward
    {
        private readonly LocString _description;
        private readonly string _reason;

        protected override RewardType RewardType => RewardType.None;
        public override int RewardsSetIndex => 99;
        public override LocString Description => _description;
        public override bool IsPopulated => true;

        public ApUnavailableReward(
            string itemName,
            string reason,
            Player player,
            ApMirroredRewardSpec spec)
            : base(player)
        {
            _description = CreateApDescription(itemName, spec);
            _reason = reason;
        }

        public bool CanClaim(out string reason)
        {
            reason = _reason;
            return false;
        }

        public bool HasOriginText => true;
        public bool UseAncientStyle => false;

        public override void Populate() { }
        protected override Task<bool> OnSelect() => Task.FromResult(false);
        public override Control CreateIcon() => new();
        public override void OnSkipped() { }
        public override void MarkContentAsSeen() { }
    }
}
