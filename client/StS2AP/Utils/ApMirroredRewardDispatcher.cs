using System.Text.Json;
using Archipelago.MultiClient.Net.Models;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using StS2AP.Data;
using StS2AP.Extensions;
using StS2AP.Models;
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

    private static readonly RitsuLibSidecarJsonSerializer<ApRewardMenuSpec> MenuSerializer = new();
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

    private static readonly Dictionary<(ApGrantId GrantId, ApMirroredRewardKind Kind), string>
        LastAttempts = new();
    private static readonly HashSet<(ulong OwnerNetId, Guid MenuId)> ActiveRemoteMenus = new();

    [ThreadStatic]
    private static ulong? _buildingCardRewardOwner;

    private static string? _activeRunIdentity;

    public static string? ActiveRunIdentity => _activeRunIdentity;

    public static void Initialize() => RitsuLibSidecarSyncMessages.Register(MenuDescriptor);

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
        LogUtility.Info($"Bound native AP reward menus to run {_activeRunIdentity}");
        return true;
    }

    public static void EndRun()
    {
        _activeRunIdentity = null;
        ActiveRemoteMenus.Clear();
        LastAttempts.Clear();
        _buildingCardRewardOwner = null;
    }

    public static bool IsBuildingMirroredCardReward(Player player) =>
        _buildingCardRewardOwner == player.NetId;

    /// <summary>Opens the local player's current AP receipt catalog as a native reward screen.</summary>
    public static async Task<bool> OpenMenu()
    {
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
        try
        {
            spec = BuildOwnerMenuSpec(player, runState);
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Could not build native AP reward menu: {ex}");
            return false;
        }

        if (MultiplayerSupport.IsRealMultiplayerRun)
        {
            INetGameService netService = RunManager.Instance.NetService;
            bool sent = netService.Type == NetGameType.Host
                ? RitsuLibSidecarSyncMessages.Broadcast(netService, MenuDescriptor, spec)
                : RitsuLibSidecarSyncMessages.SendToHostAndBroadcast(netService, MenuDescriptor, spec);
            if (!sent)
            {
                LogUtility.Error($"Could not publish AP reward menu {spec.MenuId} to every peer");
                NotificationUtility.ShowRawText("Could not synchronize the AP reward menu.");
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

    private static ApRewardMenuSpec BuildOwnerMenuSpec(Player player, RunState runState)
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

        RelicRewardUtility.ReconcileBankedRewards(player);

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

                menu.Rewards.Add(BuildAssignedSpec(receipt, player, apSlotId, kind));
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

    private static ApMirroredRewardSpec BuildAssignedSpec(
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
                _buildingCardRewardOwner = player.NetId;
                try
                {
                    CardReward reward = GameUtility.GetOrAssignCardReward(itemIndex, player, rare)
                        ?? throw new InvalidOperationException($"Could not assign card reward {itemIndex}.");
                    spec.CardCanReroll = reward.CanReroll;
                    spec.SerializedModels = reward.Cards.Select(SerializeCard).ToList();
                }
                finally
                {
                    _buildingCardRewardOwner = null;
                }
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
                PotionModel potion = ArchipelagoClient.Progress.GetOrAssignPotion(itemIndex, player)
                    ?? throw new InvalidOperationException($"Could not assign potion reward {itemIndex}.");
                spec.SerializedModels.Add(SerializePotion(potion));
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
                PotionModel.FromSerializable(
                    Deserialize<SerializablePotion>(spec.SerializedModels.Single())
                ),
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
        CardCreationOptions options = CreateCardOptions(player, spec.IsRareCardReward);
        List<CardModel> cards = spec.SerializedModels
            .Select(serialized => player.RunState.LoadCard(
                Deserialize<SerializableCard>(serialized),
                player
            ))
            .ToList();
        return new ApNativeCardReward(
            cards,
            player,
            options,
            spec,
            spec.CardCanReroll
        );
    }

    private static Reward BuildAncientReward(ApMirroredRewardSpec spec, Player player)
    {
        if (spec.SerializedModels.Count != AncientRelicPool.ChoiceCount)
            throw new InvalidOperationException($"Ancient reward {spec.GrantId} had invalid choices.");

        var children = spec.SerializedModels
            .Select(serialized => (Reward)new ApNativeRelicReward(
                DeserializeRelic(serialized),
                player,
                spec,
                ApMirroredRewardKind.Ancient
            ))
            .ToList();
        return LinkedRewardSets.Create(children, player, LinkedRewardSelectionMode.ChooseOne);
    }

    private static Reward BuildStandardRelicReward(
        ApMirroredRewardSpec spec,
        Player player)
    {
        RelicModel relic = DeserializeRelic(spec.SerializedModels.Single());
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

    private static Task HandleMenuSpec(
        RitsuLibSidecarSyncMessageContext<ApRewardMenuSpec> context)
    {
        ApRewardMenuSpec menu = context.Message;
        if (menu.SchemaVersion != 1 || context.SenderNetId != menu.OwnerNetId)
            throw new InvalidOperationException("Invalid AP reward-menu owner or schema.");

        if (RunManager.Instance.NetService.Type == NetGameType.Host)
            ValidateMenuOnHost(menu);

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
            if (reward.OwnerNetId != menu.OwnerNetId || reward.ApSlotId != menu.ApSlotId)
                throw new InvalidOperationException("AP reward-menu entry had mismatched ownership.");
            if (ApRunData.IsReceiptUsed(runState, menu.OwnerNetId, reward.ReceivedItemIndex))
                throw new InvalidOperationException($"AP receipt {reward.GrantId} was already consumed.");

            if (ownerState.Participation == ApParticipationKind.ApGuest)
            {
                if (!ApReceiptRelay.TryGetHostReceipt(
                        reward.ReceivedItemIndex,
                        out ItemInfo hostReceipt
                    ))
                {
                    throw new InvalidOperationException(
                        $"AP Guest reward {reward.GrantId} was absent from the host receipt catalog."
                    );
                }

                var indexedHostReceipt = new IndexedItemInfo(
                    hostReceipt,
                    reward.ReceivedItemIndex
                );
                bool hasNativeKind = TryGetMirroredKind(
                    indexedHostReceipt,
                    out ApMirroredRewardKind hostKind
                );
                if (reward.Kind != ApMirroredRewardKind.Unavailable
                    && (!hasNativeKind || hostKind != reward.Kind))
                {
                    throw new InvalidOperationException(
                        $"AP Guest reward {reward.GrantId} did not match the host receipt kind."
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
        try
        {
            if (!ActiveRemoteMenus.Add(key))
                throw new InvalidOperationException($"AP reward menu {menu.MenuId} is already active.");
            if (!TryGetCurrentMenuOwner(menu, out RunState runState, out _))
                throw new InvalidOperationException("No matching player exists for the AP reward menu.");
            Player owner = runState.GetPlayer(menu.OwnerNetId)
                ?? throw new InvalidOperationException($"Player {menu.OwnerNetId} is not in the run.");
            RewardsSet set = BuildRewardsSet(menu, owner);
            await RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(set);
            sidecarCompletion.SetResult();
        }
        catch (Exception ex)
        {
            sidecarCompletion.SetException(ex);
            MultiplayerSupport.InvalidateRunClaims(
                $"remote AP reward menu {menu.MenuId} failed"
            );
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
            bool applied = await base.OnSelect();
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
            if (applied && LocalContext.IsMe(Player))
                CommitDiscreteReward(_itemIndex, ApMirroredRewardKind.Potion);
            return applied;
        }

        public override void OnSkipped() { }
    }

    private sealed class ApNativeCardReward : CardReward, IApNativeReward
    {
        private readonly int _itemIndex;
        private readonly bool _isRare;
        private readonly LocString _description;

        protected override string IconPath => _isRare
            ? ImageHelper.GetImagePath("ui/reward_screen/reward_icon_rare.png")
            : base.IconPath;

        public override LocString Description => _description;

        public ApNativeCardReward(
            IEnumerable<CardModel> cards,
            Player player,
            CardCreationOptions rerollOptions,
            ApMirroredRewardSpec spec,
            bool canReroll)
            : base(cards, CardCreationSource.Encounter, player, rerollOptions)
        {
            _itemIndex = spec.ReceivedItemIndex;
            _isRare = spec.IsRareCardReward;
            _description = CreateApDescription(
                new LocString("gameplay_ui", "COMBAT_REWARD_ADD_CARD"),
                spec
            );
            CanReroll = canReroll;
        }

        public bool CanClaim(out string reason) =>
            MultiplayerSupport.CanClaimReceivedReward(ApMirroredRewardKind.Card, out reason);
        public bool HasOriginText => true;
        public bool UseAncientStyle => false;

        protected override async Task<bool> OnSelect()
        {
            HashSet<CardModel>? deckBefore = LocalContext.IsMe(Player)
                ? Player.Deck.Cards.ToHashSet()
                : null;
            bool applied = await base.OnSelect();
            if (!applied || !LocalContext.IsMe(Player))
                return applied;

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
