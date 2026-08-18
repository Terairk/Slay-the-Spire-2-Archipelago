using System.Text.Json;
using Archipelago.MultiClient.Net.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using StS2AP.Data;
using StS2AP.Extensions;
using StS2AP.Models;
using STS2RitsuLib;
using STS2RitsuLib.Combat.Rewards;
using STS2RitsuLib.Data;
using STS2RitsuLib.Networking.Sidecar;

namespace StS2AP.Utils;

/// <summary>
/// Mirrors AP-owned received rewards into the native MegaCrit reward lifecycle. The AP owner
/// publishes a concrete/recipe specification through RitsuLib Sidecar, every peer constructs
/// the same temporary RewardsSet, and the base game's reward and player-choice synchronizers
/// replicate the selected reward and all native alternative callbacks.
/// </summary>
public static class ApMirroredRewardDispatcher
{
    private const string GrantStoreKey = "multiplayer_grants";
    private const string SidecarMessageKey = "mirrored_received_reward_v1";

    private sealed record RuntimeReward(Reward Root, IReadOnlyList<Reward> SelectableChildren);

    private static readonly ModDataStoreCache<MultiplayerGrantState> GrantStore =
        RitsuLibFramework.GetDataStore(ModEntry.ModId)
            .CreateCache<MultiplayerGrantState>(GrantStoreKey);

    private static readonly RitsuLibSidecarJsonSerializer<ApMirroredRewardSpec> SpecSerializer =
        new();

    private static readonly RitsuLibSidecarSyncMessageDescriptor<ApMirroredRewardSpec> SpecDescriptor =
        new(
            ModEntry.ModId,
            SidecarMessageKey,
            SpecSerializer.Serialize,
            SpecSerializer.Deserialize,
            HandleMirroredSpec,
            LocationTargeted: true,
            ShouldBuffer: true,
            Mode: NetTransferMode.Reliable,
            FailurePolicy: RitsuLibSidecarSyncFailurePolicy.Required,
            BroadcastScope: RitsuLibSidecarSyncBroadcastScope.ReadyPeers,
            DispatchLocalOnBroadcast: false,
            LogLevel: LogLevel.Debug,
            ShouldBroadcast: true
        );

    private static readonly Dictionary<(ulong OwnerNetId, ApGrantId GrantId), RuntimeReward>
        RuntimeRewards = new();

    private static readonly HashSet<(ulong OwnerNetId, ApGrantId GrantId)> ActiveAttempts = new();

    private static MultiplayerGrantRunState? _activeOwnerRun;
    private static string? _activeRunIdentity;

    [ThreadStatic]
    private static ulong? _buildingCardRewardOwner;

    public static void Initialize()
    {
        RitsuLibSidecarSyncMessages.Register(SpecDescriptor);
    }

    /// <summary>Binds owner-private assignments and applied IDs to the newly launched run.</summary>
    public static bool BeginRun(RunState runState, out string reason)
    {
        reason = string.Empty;
        EndRun();

        if (MultiplayerSupport.PreparedApRoomSeed is not { } roomSeed
            || MultiplayerSupport.PreparedApTeamId is not { } apTeamId
            || MultiplayerSupport.PreparedApSlotId is not { } apSlotId)
        {
            reason = "The AP owner identity was not prepared before discrete grants were bound.";
            return false;
        }

        try
        {
            long startTime = RunManager.Instance.ToSave(preFinishedRoom: null).StartTime;
            _activeRunIdentity = $"{runState.Rng.StringSeed}:{startTime}";
            _activeOwnerRun = GrantStore.Value.Runs.LastOrDefault(entry =>
                entry.ApRoomSeed == roomSeed
                && entry.ApTeamId == apTeamId
                && entry.ApSlotId == apSlotId
                && entry.StsRunIdentity == _activeRunIdentity
            );

            if (_activeOwnerRun == null)
            {
                _activeOwnerRun = new MultiplayerGrantRunState
                {
                    ApRoomSeed = roomSeed,
                    ApTeamId = apTeamId,
                    ApSlotId = apSlotId,
                    StsRunIdentity = _activeRunIdentity,
                };
                GrantStore.Modify(document => document.Runs.Add(_activeOwnerRun));
                GrantStore.Save();
            }

            RestoreOwnerProgressAssignments(runState);
            LogUtility.Info(
                $"Bound discrete AP grant ledger: run={_activeRunIdentity}, "
                    + $"slot={apSlotId}, grants={_activeOwnerRun.Grants.Count}"
            );
            return true;
        }
        catch (Exception ex)
        {
            reason = $"The owner-private AP grant ledger could not be loaded: {ex.Message}";
            LogUtility.Error(reason);
            EndRun();
            return false;
        }
    }

    public static void EndRun()
    {
        _activeOwnerRun = null;
        _activeRunIdentity = null;
        RuntimeRewards.Clear();
        ActiveAttempts.Clear();
        _buildingCardRewardOwner = null;
    }

    public static string? ActiveRunIdentity => _activeRunIdentity;

    /// <summary>Captures every supported discrete receipt for owner-local diagnostics.</summary>
    public static IReadOnlyList<ApGrantSnapshot> CaptureGrantSnapshots()
    {
        Player? player = GameUtility.CurrentPlayer;
        ulong ownerNetId = player?.NetId ?? 0;
        int apSlotId = MultiplayerSupport.PreparedApSlotId ?? 0;
        var snapshots = new List<ApGrantSnapshot>();

        foreach (IndexedItemInfo receipt in ArchipelagoClient.Progress.AllReceivedItems
                     .OrderBy(item => item.Index))
        {
            if (!TryGetMirroredKind(receipt, out ApMirroredRewardKind kind))
                continue;

            MultiplayerGrantRecord? record = null;
            _activeOwnerRun?.Grants.TryGetValue(receipt.Index, out record);
            ApGrantState state;
            string? blockedReason = null;
            bool applied = record?.Applied == true
                || ArchipelagoClient.Progress.UsedItems.Contains(receipt.Index);
            if (applied)
            {
                state = ApGrantState.Applied;
            }
            else if (player == null)
            {
                state = ApGrantState.Blocked;
                blockedReason = "no active local player";
            }
            else if (receipt.Item.ItemId >= 10000
                && receipt.Item.GetCharacterOffset() != player.Character.GetCharacterOffset())
            {
                state = ApGrantState.Blocked;
                blockedReason = "belongs to another character";
            }
            else if (kind == ApMirroredRewardKind.Relic
                && !RelicRewardUtility.IsAvailableInRewardMenu(receipt, player))
            {
                state = ApGrantState.Blocked;
                blockedReason = "requires an earned relic reward";
            }
            else if (!MultiplayerSupport.CanClaimReceivedReward(kind, out blockedReason))
            {
                state = ApGrantState.Blocked;
            }
            else
            {
                state = ApGrantState.Claimable;
                blockedReason = null;
            }

            snapshots.Add(new ApGrantSnapshot(
                new ApGrantId(apSlotId, receipt.Index),
                receipt.Item.ItemDisplayName,
                ownerNetId,
                kind,
                state,
                DescribeAssignment(record),
                blockedReason,
                record?.LastAttempt
            ));
        }
        return snapshots;
    }

    /// <summary>
    /// True only during mirrored AP card population. Compatibility patches use this narrow
    /// boundary so native combat rewards remain untouched on every peer.
    /// </summary>
    public static bool IsBuildingMirroredCardReward(Player player) =>
        _buildingCardRewardOwner == player.NetId;

    public static async Task<bool> ExecuteCardReward(int itemIndex, bool rare, string itemName)
    {
        Player? player = GameUtility.CurrentPlayer;
        if (player == null)
            return false;

        var spec = CreateOwnerSpec(itemIndex, ApMirroredRewardKind.Card, itemName);
        spec.IsRareCardReward = rare;
        spec.CardRewardActIndex = rare ? null : GameUtility.GetCardRewardActIndex(itemIndex, player);
        ApplyPersistedAssignment(spec);
        return await ExecuteOwnerAttempt(spec, selectedChildIndex: null);
    }

    public static async Task<bool> ExecuteRelicReward(int itemIndex, string itemName)
    {
        var spec = CreateOwnerSpec(itemIndex, ApMirroredRewardKind.Relic, itemName);
        ApplyPersistedAssignment(spec);
        return await ExecuteOwnerAttempt(spec, selectedChildIndex: null);
    }

    public static async Task<bool> ExecutePotionReward(int itemIndex, string itemName)
    {
        var spec = CreateOwnerSpec(itemIndex, ApMirroredRewardKind.Potion, itemName);
        ApplyPersistedAssignment(spec);
        return await ExecuteOwnerAttempt(spec, selectedChildIndex: null);
    }

    public static async Task<bool> ExecuteAncientReward(
        int itemIndex,
        string itemName,
        IReadOnlyList<RelicModel> choices,
        int selectedChoiceIndex)
    {
        var spec = CreateOwnerSpec(itemIndex, ApMirroredRewardKind.Ancient, itemName);
        ApplyPersistedAssignment(spec);
        if (spec.SerializedModels.Count == 0)
        {
            spec.SerializedModels = choices.Select(SerializeRelic).ToList();
            if (!PersistAssignment(spec, lastAttempt: null))
                return false;
        }

        return await ExecuteOwnerAttempt(spec, selectedChoiceIndex);
    }

    /// <summary>
    /// Assigns an Ancient choice without touching MegaCrit RNG or relic bags. The AP slot is
    /// included in the key, so two AP owners with the same received index get different choices.
    /// </summary>
    public static IReadOnlyList<RelicModel> GetOrAssignAncientChoices(int itemIndex, Player player)
    {
        var spec = CreateOwnerSpec(itemIndex, ApMirroredRewardKind.Ancient, "Progressive Ancient");
        ApplyPersistedAssignment(spec);
        if (spec.SerializedModels.Count == AncientRelicPool.ChoiceCount)
            return spec.SerializedModels.Select(DeserializeRelic).ToList();

        IReadOnlyList<RelicModel> choices =
            ArchipelagoClient.Progress.GetOrAssignAncientRelicChoices(
                itemIndex,
                player,
                $"{spec.ApSlotId}:{itemIndex}"
            );
        if (choices.Count != AncientRelicPool.ChoiceCount)
            return choices;

        spec.SerializedModels = choices.Select(SerializeRelic).ToList();
        if (!PersistAssignment(spec, lastAttempt: null))
            return Array.Empty<RelicModel>();

        return choices;
    }

    private static ApMirroredRewardSpec CreateOwnerSpec(
        int itemIndex,
        ApMirroredRewardKind kind,
        string itemName)
    {
        if (MultiplayerSupport.PreparedApSlotId is not { } apSlotId)
            throw new InvalidOperationException("No prepared AP slot exists for a mirrored reward.");
        Player player = GameUtility.CurrentPlayer
            ?? throw new InvalidOperationException("No local AP player exists for a mirrored reward.");

        EnsureRecord(itemIndex, kind, itemName);
        return new ApMirroredRewardSpec
        {
            ApSlotId = apSlotId,
            ReceivedItemIndex = itemIndex,
            OwnerNetId = player.NetId,
            Kind = kind,
        };
    }

    private static void ApplyPersistedAssignment(ApMirroredRewardSpec spec)
    {
        if (_activeOwnerRun == null
            || !_activeOwnerRun.Grants.TryGetValue(
                spec.ReceivedItemIndex,
                out MultiplayerGrantRecord? record
            )
            || record == null)
            return;

        if (record.Kind != spec.Kind)
        {
            throw new InvalidOperationException(
                $"AP grant {spec.GrantId} changed kind from {record.Kind} to {spec.Kind}."
            );
        }

        spec.IsRareCardReward = record.IsRareCardReward || spec.IsRareCardReward;
        spec.CardRewardActIndex ??= record.CardRewardActIndex;
        spec.CardCanReroll = record.CardCanReroll;
        spec.SerializedModels = record.SerializedModels.ToList();
    }

    private static async Task<bool> ExecuteOwnerAttempt(
        ApMirroredRewardSpec spec,
        int? selectedChildIndex)
    {
        if (!MultiplayerSupport.CanClaimReceivedReward(spec.Kind, out string blockedReason))
        {
            LogUtility.Warn($"AP grant {spec.GrantId} blocked: {blockedReason}");
            SetLastAttempt(spec, $"blocked: {blockedReason}");
            return false;
        }

        if (_activeOwnerRun?.Grants.TryGetValue(spec.ReceivedItemIndex, out var existing) == true
            && existing.Applied)
        {
            LogUtility.Warn($"Ignoring duplicate applied AP grant {spec.GrantId}");
            return true;
        }

        var netService = RunManager.Instance.NetService;
        bool sent = netService.Type == NetGameType.Host
            ? RitsuLibSidecarSyncMessages.Broadcast(netService, SpecDescriptor, spec)
            : RitsuLibSidecarSyncMessages.SendToHostAndBroadcast(netService, SpecDescriptor, spec);
        if (!sent)
        {
            SetLastAttempt(spec, "sidecar-send-failed");
            LogUtility.Error($"Could not publish mirrored AP grant {spec.GrantId} to every peer");
            return false;
        }

        bool selectionStarted = false;
        try
        {
            RuntimeReward runtime = GetOrBuildRuntimeReward(spec);
            var key = (spec.OwnerNetId, spec.GrantId);
            if (!ActiveAttempts.Add(key))
                throw new InvalidOperationException($"AP grant {spec.GrantId} already has an active attempt.");

            var rewardsSet = new RewardsSet(GameUtility.CurrentPlayer!)
                .WithCustomRewards(new List<Reward> { runtime.Root });
            Task completion = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewardsSet);

            if (!PersistRuntimeAssignment(spec, runtime, lastAttempt: null))
            {
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                await completion;
                return false;
            }

            Reward selectedReward = selectedChildIndex.HasValue
                ? runtime.SelectableChildren.ElementAtOrDefault(selectedChildIndex.Value)
                    ?? throw new ArgumentOutOfRangeException(nameof(selectedChildIndex))
                : runtime.Root;

            selectionStarted = true;
            bool consumed = await RunManager.Instance.RewardsSetSynchronizer
                .SelectLocalReward(selectedReward);
            if (!consumed)
            {
                RunManager.Instance.RewardsSetSynchronizer.SkipLocalRewardsSet();
                await completion;
                string attempt = spec.Kind == ApMirroredRewardKind.Potion
                    ? "no-slot"
                    : "skipped";
                PersistRuntimeAssignment(spec, runtime, attempt);
                LogUtility.Info($"AP grant {spec.GrantId} remains claimable after {attempt}");
                return false;
            }

            await completion;
            if (!MarkApplied(spec))
            {
                MultiplayerSupport.InvalidateRunClaims(
                    $"AP grant {spec.GrantId} applied but its ledger could not be persisted"
                );
            }

            RuntimeRewards.Remove((spec.OwnerNetId, spec.GrantId));
            LogUtility.Success($"Applied mirrored AP grant {spec.GrantId} ({spec.Kind})");
            return true;
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Mirrored AP grant {spec.GrantId} failed: {ex}");
            if (selectionStarted)
            {
                MarkApplied(spec);
                MultiplayerSupport.InvalidateRunClaims(
                    $"AP grant {spec.GrantId} failed after native selection started"
                );
                return true;
            }

            SetLastAttempt(spec, $"failed: {ex.GetBaseException().Message}");
            return false;
        }
        finally
        {
            ActiveAttempts.Remove((spec.OwnerNetId, spec.GrantId));
        }
    }

    private static Task HandleMirroredSpec(
        RitsuLibSidecarSyncMessageContext<ApMirroredRewardSpec> context)
    {
        var spec = context.Message;
        if (spec.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported mirrored reward schema {spec.SchemaVersion}.");
        if (context.SenderNetId != spec.OwnerNetId)
        {
            throw new InvalidOperationException(
                $"Mirrored reward owner {spec.OwnerNetId} did not match sender {context.SenderNetId}."
            );
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        bool posted = RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
            CompleteRemoteAttempt(spec, completion)
        );
        if (!posted)
        {
            completion.SetException(
                new InvalidOperationException("Godot main loop was unavailable for mirrored reward dispatch.")
            );
        }
        return completion.Task;
    }

    private static async void CompleteRemoteAttempt(
        ApMirroredRewardSpec spec,
        TaskCompletionSource completion)
    {
        var key = (spec.OwnerNetId, spec.GrantId);
        try
        {
            RunState runState = RunManager.Instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("No run state exists for a mirrored reward.");
            Player owner = runState.GetPlayer(spec.OwnerNetId)
                ?? throw new InvalidOperationException(
                    $"Mirrored AP owner {spec.OwnerNetId} is not in the current run."
                );
            if (!ActiveAttempts.Add(key))
                throw new InvalidOperationException($"AP grant {spec.GrantId} already has an active remote attempt.");

            RuntimeReward runtime = GetOrBuildRuntimeReward(spec);
            var rewardsSet = new RewardsSet(owner)
                .WithCustomRewards(new List<Reward> { runtime.Root });
            LogUtility.Debug(
                $"Remote peer began mirrored AP grant {spec.GrantId} for owner {owner.NetId}"
            );
            await RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewardsSet);
            RuntimeRewards.Remove(key);
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
            MultiplayerSupport.InvalidateRunClaims(
                $"remote mirrored AP grant {spec.GrantId} failed"
            );
        }
        finally
        {
            ActiveAttempts.Remove(key);
        }
    }

    private static RuntimeReward GetOrBuildRuntimeReward(ApMirroredRewardSpec spec)
    {
        var key = (spec.OwnerNetId, spec.GrantId);
        if (RuntimeRewards.TryGetValue(key, out RuntimeReward? existing))
            return existing;

        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("No active run exists for mirrored reward construction.");
        Player player = runState.GetPlayer(spec.OwnerNetId)
            ?? throw new InvalidOperationException($"Player {spec.OwnerNetId} is not in the active run.");

        RuntimeReward runtime = spec.Kind switch
        {
            ApMirroredRewardKind.Card => BuildCardReward(spec, player),
            ApMirroredRewardKind.Relic => BuildRelicReward(spec, player),
            ApMirroredRewardKind.Potion => BuildPotionReward(spec, player),
            ApMirroredRewardKind.Ancient => BuildAncientReward(spec, player),
            _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind)),
        };
        RuntimeRewards[key] = runtime;
        return runtime;
    }

    private static RuntimeReward BuildCardReward(ApMirroredRewardSpec spec, Player player)
    {
        var rarity = spec.IsRareCardReward
            ? CardRarityOddsType.BossEncounter
            : CardRarityOddsType.RegularEncounter;
        var options = BetaMainCompatibility.WithCombatRewardCompatibility(
            new CardCreationOptions(
                new[] { player.Character.CardPool },
                CardCreationSource.Encounter,
                rarity
            )
        );

        CardReward reward;
        _buildingCardRewardOwner = player.NetId;
        try
        {
            if (spec.SerializedModels.Count > 0)
            {
                var cards = spec.SerializedModels
                    .Select(serialized => player.RunState.LoadCard(
                        Deserialize<SerializableCard>(serialized),
                        player
                    ))
                    .ToList();
                reward = new CardReward(
                    cards,
                    CardCreationSource.Encounter,
                    player,
                    options
                )
                {
                    CanReroll = spec.CardCanReroll,
                };
            }
            else
            {
                reward = new CardReward(options, 3, player)
                {
                    CanReroll = player.Relics.Any(relic => relic is Driftwood),
                };
                if (spec.CardRewardActIndex.HasValue)
                    Patches.Patches_APCardRewardUpgradeOdds.PopulateForAct(
                        reward,
                        spec.CardRewardActIndex.Value
                    );
                else
                    reward.Populate();
            }
        }
        finally
        {
            _buildingCardRewardOwner = null;
        }

        return new RuntimeReward(reward, Array.Empty<Reward>());
    }

    private static RuntimeReward BuildRelicReward(ApMirroredRewardSpec spec, Player player)
    {
        RelicReward reward;
        if (spec.SerializedModels.Count > 0)
        {
            reward = new RelicReward(DeserializeRelic(spec.SerializedModels[0]), player);
        }
        else
        {
            reward = new RelicReward(player);
            reward.Populate();
        }
        return new RuntimeReward(reward, Array.Empty<Reward>());
    }

    private static RuntimeReward BuildPotionReward(ApMirroredRewardSpec spec, Player player)
    {
        PotionReward reward;
        if (spec.SerializedModels.Count > 0)
        {
            PotionModel potion = PotionModel.FromSerializable(
                Deserialize<SerializablePotion>(spec.SerializedModels[0])
            );
            reward = new PotionReward(potion, player);
        }
        else
        {
            reward = new PotionReward(player);
            reward.Populate();
        }
        return new RuntimeReward(reward, Array.Empty<Reward>());
    }

    private static RuntimeReward BuildAncientReward(ApMirroredRewardSpec spec, Player player)
    {
        if (spec.SerializedModels.Count != AncientRelicPool.ChoiceCount)
        {
            throw new InvalidOperationException(
                $"Ancient AP grant {spec.GrantId} requires {AncientRelicPool.ChoiceCount} relics."
            );
        }

        var children = spec.SerializedModels
            .Select(serialized => (Reward)new RelicReward(DeserializeRelic(serialized), player))
            .ToList();
        // AP_MP: Ritsu's LinkedRewardSet owns replicated child selection. The AP reward
        // overlay is presentation only and must keep using these stable child positions.
        LinkedRewardSet linked = LinkedRewardSets.Create(
            children,
            player,
            LinkedRewardSelectionMode.ChooseOne
        );
        return new RuntimeReward(linked, children);
    }

    private static bool PersistRuntimeAssignment(
        ApMirroredRewardSpec spec,
        RuntimeReward runtime,
        string? lastAttempt)
    {
        spec.SerializedModels = runtime.Root switch
        {
            CardReward card => card.Cards.Select(SerializeCard).ToList(),
            RelicReward relic when relic.Relic != null => new List<string>
                { SerializeRelic(relic.Relic) },
            PotionReward potion when potion.Potion != null => new List<string>
                { SerializePotion(potion.Potion) },
            LinkedRewardSet => spec.SerializedModels,
            _ => spec.SerializedModels,
        };
        if (runtime.Root is CardReward cardReward)
            spec.CardCanReroll = cardReward.CanReroll;
        return PersistAssignment(spec, lastAttempt);
    }

    private static bool PersistAssignment(ApMirroredRewardSpec spec, string? lastAttempt)
    {
        if (_activeOwnerRun == null || spec.ApSlotId != _activeOwnerRun.ApSlotId)
            return false;

        MultiplayerGrantRecord record = EnsureRecord(
            spec.ReceivedItemIndex,
            spec.Kind,
            FindItemName(spec.ReceivedItemIndex)
        );
        record.IsRareCardReward = spec.IsRareCardReward;
        record.CardRewardActIndex = spec.CardRewardActIndex;
        record.CardCanReroll = spec.CardCanReroll;
        record.SerializedModels = spec.SerializedModels.ToList();
        record.LastAttempt = lastAttempt;
        return SaveOwnerLedger();
    }

    private static bool MarkApplied(ApMirroredRewardSpec spec)
    {
        if (_activeOwnerRun == null)
            return false;
        MultiplayerGrantRecord record = EnsureRecord(
            spec.ReceivedItemIndex,
            spec.Kind,
            FindItemName(spec.ReceivedItemIndex)
        );
        record.Applied = true;
        record.LastAttempt = "applied";
        if (!ArchipelagoClient.Progress.UsedItems.Contains(spec.ReceivedItemIndex))
            ArchipelagoClient.Progress.UsedItems.Add(spec.ReceivedItemIndex);
        return SaveOwnerLedger();
    }

    private static void SetLastAttempt(ApMirroredRewardSpec spec, string attempt)
    {
        if (_activeOwnerRun == null)
            return;
        MultiplayerGrantRecord record = EnsureRecord(
            spec.ReceivedItemIndex,
            spec.Kind,
            FindItemName(spec.ReceivedItemIndex)
        );
        record.LastAttempt = attempt;
        SaveOwnerLedger();
    }

    private static MultiplayerGrantRecord EnsureRecord(
        int itemIndex,
        ApMirroredRewardKind kind,
        string itemName)
    {
        if (_activeOwnerRun == null)
            throw new InvalidOperationException("No active owner grant ledger is bound.");
        if (!_activeOwnerRun.Grants.TryGetValue(itemIndex, out MultiplayerGrantRecord? record))
        {
            record = new MultiplayerGrantRecord
            {
                Kind = kind,
                ItemName = itemName,
            };
            _activeOwnerRun.Grants[itemIndex] = record;
        }
        else if (record.Kind != kind)
        {
            throw new InvalidOperationException(
                $"Received item index {itemIndex} was already assigned as {record.Kind}."
            );
        }

        if (string.IsNullOrWhiteSpace(record.ItemName))
            record.ItemName = itemName;
        return record;
    }

    private static bool SaveOwnerLedger()
    {
        try
        {
            GrantStore.Save();
            return true;
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Failed to persist discrete AP grant ledger: {ex}");
            return false;
        }
    }

    private static void RestoreOwnerProgressAssignments(RunState runState)
    {
        if (_activeOwnerRun == null)
            return;
        Player? localPlayer = runState.GetPlayer(RunManager.Instance.NetService.NetId);
        if (localPlayer == null)
            return;

        foreach ((int itemIndex, MultiplayerGrantRecord record) in _activeOwnerRun.Grants)
        {
            if (record.Applied && !ArchipelagoClient.Progress.UsedItems.Contains(itemIndex))
                ArchipelagoClient.Progress.UsedItems.Add(itemIndex);
            if (record.SerializedModels.Count == 0)
                continue;

            try
            {
                switch (record.Kind)
                {
                    case ApMirroredRewardKind.Ancient:
                        ArchipelagoClient.Progress.AncientRelicChoiceAssignments[itemIndex] =
                            record.SerializedModels.Select(DeserializeRelic).ToList();
                        break;
                    case ApMirroredRewardKind.Relic:
                        ArchipelagoClient.Progress.RelicChoiceAssignments[itemIndex] =
                            record.SerializedModels.Select(DeserializeRelic).ToList();
                        break;
                    case ApMirroredRewardKind.Potion:
                        ArchipelagoClient.Progress.PotionAssignments[itemIndex] =
                            PotionModel.FromSerializable(
                                Deserialize<SerializablePotion>(record.SerializedModels[0])
                            ).CanonicalInstance;
                        break;
                }
            }
            catch (Exception ex)
            {
                LogUtility.Error(
                    $"Failed to restore AP assignment {_activeOwnerRun.ApSlotId}:{itemIndex}: {ex.Message}"
                );
            }
        }
    }

    private static string FindItemName(int itemIndex) =>
        ArchipelagoClient.Progress.AllReceivedItems
            .Concat(MultiplayerSupport.PendingUnsupportedItems)
            .FirstOrDefault(item => item.Index == itemIndex)?.Item.ItemDisplayName
        ?? $"Received item {itemIndex}";

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

    private static string DescribeAssignment(MultiplayerGrantRecord? record)
    {
        if (record == null || record.SerializedModels.Count == 0)
            return "<unassigned>";
        try
        {
            return record.Kind switch
            {
                ApMirroredRewardKind.Card => string.Join(", ", record.SerializedModels.Select(json =>
                {
                    CardModel card = CardModel.FromSerializable(Deserialize<SerializableCard>(json));
                    return $"{card.Title} [{card.Id.Entry}]";
                })),
                ApMirroredRewardKind.Relic or ApMirroredRewardKind.Ancient =>
                    string.Join(", ", record.SerializedModels.Select(json =>
                    {
                        RelicModel relic = DeserializeRelic(json);
                        return $"{relic.Title.GetRawText()} [{relic.Id.Entry}]";
                    })),
                ApMirroredRewardKind.Potion => string.Join(", ", record.SerializedModels.Select(json =>
                {
                    PotionModel potion = PotionModel.FromSerializable(
                        Deserialize<SerializablePotion>(json)
                    );
                    return $"{potion.Title.GetRawText()} [{potion.Id.Entry}]";
                })),
                _ => "<unknown assignment>",
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
        ?? throw new InvalidOperationException($"Could not deserialize mirrored model {typeof(T).Name}.");
}
