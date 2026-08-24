using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.Persistence;
using STS2RitsuLib.Networking.ManagedActions;

namespace StS2AP.Utils;

/// <summary>
/// Synchronizes progressive starter transitions through MegaCrit's native action queue. The AP
/// receipt owner authors concrete model recipes; every replica executes those recipes only in a
/// non-combat action slot.
/// </summary>
public static class ProgressiveStarterMultiplayer
{
    private const int SchemaVersion = 1;
    private const string ActionKey = "progressive_starter_v1";

    private static readonly RitsuLibManagedNetActionDescriptor<ApProgressiveStarterActionMessage>
        ActionDescriptor = new(
            ModuleId: ModEntry.ModId,
            ActionKey: ActionKey,
            Serialize: static message => JsonSerializer.SerializeToUtf8Bytes(message),
            Deserialize: DeserializeMessage,
            Execute: ExecuteAction,
            ActionType: GameActionType.NonCombat
        );
    private static readonly Dictionary<
        (Guid RunId, ulong PlayerNetId, ApProgressiveStarterActionMessage.StarterKind Kind),
        ApProgressiveStarterKindState
    > PendingSpecifications = new();
    private static readonly System.Reflection.FieldInfo? AllRunCardsField =
        AccessTools.Field(typeof(RunState), "_allCards");

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        RitsuLibManagedNetActions.Register(ActionDescriptor);
        _initialized = true;
    }

    public static void EndRun() => PendingSpecifications.Clear();

    /// <summary>
    /// Enqueues one initialization projection for every player governed by this process's AP
    /// slot. The fixed host additionally owns its AP Guests; a non-host own-slot process owns only
    /// its local player.
    /// </summary>
    public static void BeginRun(RunState runState, Player localPlayer)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsLocalOwnApSlot
            || !MultiplayerSupport.IsFeatureEnabled(MultiplayerFeature.ProgressiveStarters)
            || !TryGetActionIdentity(runState, localPlayer, out Guid runId, out int apSlotId))
        {
            return;
        }

        try
        {
            var targets = new List<ApProgressiveStarterActionMessage.Target>();
            foreach (Player player in GetAuthoredPlayers(runState, localPlayer))
                AddInitializationTargets(player, targets);

            if (targets.Count == 0)
                return;

            var message = new ApProgressiveStarterActionMessage
            {
                RunId = runId,
                ActionId = Guid.NewGuid(),
                OwnerNetId = localPlayer.NetId,
                ApSlotId = apSlotId,
                Reason = ApProgressiveStarterActionMessage.ActionReason.Initialization,
                Targets = targets
                    .OrderBy(target => target.PlayerNetId)
                    .ThenBy(target => target.Kind)
                    .ToList(),
            };

            Request(message, localPlayer, "initialization");
        }
        catch (Exception ex)
        {
            FailCapture("initialization", ex);
        }
    }

    /// <summary>
    /// Converts exactly one live AP receipt into one ordered action. Receipts for characters not
    /// active under this writer remain banked and are projected by a future initialization.
    /// </summary>
    public static void ReceiveLiveReceipt(
        int receivedItemIndex,
        long characterOffset,
        ApProgressiveStarterActionMessage.StarterKind kind,
        int receivedCount)
    {
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.IsLocalOwnApSlot
            || !MultiplayerSupport.IsFeatureEnabled(MultiplayerFeature.ProgressiveStarters)
            || receivedCount is < 1 or > 2
            || GameUtility.CurrentPlayer is not Player localPlayer
            || localPlayer.RunState is not RunState runState
            || !TryGetActionIdentity(runState, localPlayer, out Guid runId, out int apSlotId))
        {
            return;
        }

        try
        {
            var targets = new List<ApProgressiveStarterActionMessage.Target>();
            foreach (Player player in GetAuthoredPlayers(runState, localPlayer))
            {
                long? playerOffset = player.GetCharacterOffset();
                if (playerOffset != characterOffset || !IsEnabledFor(player, kind))
                    continue;

                ApProgressiveStarterKindState specification = GetOrCaptureSpecification(player, kind);
                targets.Add(new ApProgressiveStarterActionMessage.Target
                {
                    PlayerNetId = player.NetId,
                    Kind = kind,
                    TargetTier = specification.Supported
                        ? (ProgressiveStarterTier)receivedCount
                        : ProgressiveStarterTier.Unsupported,
                    Specification = Clone(specification),
                });
            }

            if (targets.Count == 0)
                return;

            var message = new ApProgressiveStarterActionMessage
            {
                RunId = runId,
                ActionId = Guid.NewGuid(),
                OwnerNetId = localPlayer.NetId,
                ApSlotId = apSlotId,
                ReceivedItemIndex = receivedItemIndex,
                CharacterOffset = characterOffset,
                Reason = ApProgressiveStarterActionMessage.ActionReason.LiveReceipt,
                Targets = targets.OrderBy(target => target.PlayerNetId).ToList(),
            };

            Request(message, localPlayer, $"receipt {receivedItemIndex}");
        }
        catch (Exception ex)
        {
            FailCapture($"receipt {receivedItemIndex}", ex);
        }
    }

    private static void AddInitializationTargets(
        Player player,
        ICollection<ApProgressiveStarterActionMessage.Target> targets)
    {
        long? offset = player.GetCharacterOffset();
        if (!offset.HasValue)
            return;

        if (IsEnabledFor(player, ApProgressiveStarterActionMessage.StarterKind.Card))
        {
            ApProgressiveStarterKindState specification = GetOrCaptureSpecification(
                player,
                ApProgressiveStarterActionMessage.StarterKind.Card
            );
            targets.Add(new ApProgressiveStarterActionMessage.Target
            {
                PlayerNetId = player.NetId,
                Kind = ApProgressiveStarterActionMessage.StarterKind.Card,
                TargetTier = specification.Supported
                    ? GetReceivedTier(ArchipelagoClient.Progress.ProgressiveStarterCards, offset.Value)
                    : ProgressiveStarterTier.Unsupported,
                Specification = Clone(specification),
            });
        }

        if (IsEnabledFor(player, ApProgressiveStarterActionMessage.StarterKind.Relic))
        {
            ApProgressiveStarterKindState specification = GetOrCaptureSpecification(
                player,
                ApProgressiveStarterActionMessage.StarterKind.Relic
            );
            targets.Add(new ApProgressiveStarterActionMessage.Target
            {
                PlayerNetId = player.NetId,
                Kind = ApProgressiveStarterActionMessage.StarterKind.Relic,
                TargetTier = specification.Supported
                    ? GetReceivedTier(ArchipelagoClient.Progress.ProgressiveStarterRelics, offset.Value)
                    : ProgressiveStarterTier.Unsupported,
                Specification = Clone(specification),
            });
        }
    }

    private static IEnumerable<Player> GetAuthoredPlayers(RunState runState, Player owner)
    {
        bool ownerIsHost = RunManager.Instance.NetService.Type == NetGameType.Host;
        foreach (Player player in runState.Players.OrderBy(player => player.NetId))
        {
            if (!ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState state))
                continue;

            if (player.NetId == owner.NetId
                && state.Participation == ApParticipationKind.OwnApSlot)
            {
                yield return player;
            }
            else if (ownerIsHost && state.Participation == ApParticipationKind.ApGuest)
            {
                yield return player;
            }
        }
    }

    private static bool TryGetActionIdentity(
        RunState runState,
        Player owner,
        out Guid runId,
        out int apSlotId)
    {
        runId = Guid.Empty;
        apSlotId = 0;
        if (!ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.RunId == Guid.Empty
            || !ApRunData.TryGetPlayerState(runState, owner.NetId, out ApPlayerRunState state)
            || state.Participation != ApParticipationKind.OwnApSlot
            || !state.ApSlotId.HasValue)
        {
            return false;
        }

        runId = shared.RunId;
        apSlotId = state.ApSlotId.Value;
        return true;
    }

    private static bool IsEnabledFor(
        Player player,
        ApProgressiveStarterActionMessage.StarterKind kind) =>
        ApPlayerContextResolver.TryGetRewardSettings(player, out ArchipelagoSettings settings)
        && kind switch
        {
            ApProgressiveStarterActionMessage.StarterKind.Card =>
                settings.ProgressiveStarterCard,
            ApProgressiveStarterActionMessage.StarterKind.Relic =>
                settings.ProgressiveStarterRelic,
            _ => false,
        };

    private static ApProgressiveStarterKindState GetOrCaptureSpecification(
        Player player,
        ApProgressiveStarterActionMessage.StarterKind kind)
    {
        if (player.RunState is RunState runState
            && ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState playerState))
        {
            ApProgressiveStarterKindState saved = SelectKind(playerState.ProgressiveStarters, kind);
            if (saved.Initialized)
                return Clone(saved);

            if (ApRunData.TryGetSharedState(runState, out ApRunSharedState shared))
            {
                var key = (shared.RunId, player.NetId, kind);
                if (PendingSpecifications.TryGetValue(key, out ApProgressiveStarterKindState? pending))
                    return Clone(pending);

                ApProgressiveStarterKindState captured = kind ==
                        ApProgressiveStarterActionMessage.StarterKind.Card
                    ? CaptureCardSpecification(player)
                    : CaptureRelicSpecification(player);
                PendingSpecifications[key] = Clone(captured);
                return captured;
            }
        }

        return kind == ApProgressiveStarterActionMessage.StarterKind.Card
            ? CaptureCardSpecification(player)
            : CaptureRelicSpecification(player);
    }

    private static ApProgressiveStarterKindState CaptureCardSpecification(Player player)
    {
        RunState? captureRunState = null;
        HashSet<CardModel>? preexistingRunCards = null;
        captureRunState = player.RunState as RunState
            ?? throw new InvalidOperationException(
                $"Player {player.NetId} is not attached to a concrete RunState."
            );
        preexistingRunCards = new HashSet<CardModel>(
            GetAllRunCards(captureRunState),
            ReferenceEqualityComparer.Instance
        );
        try
        {
            var tooth = (ArchaicTooth)ModelDb.Relic<ArchaicTooth>().ToMutable();
            if (!tooth.SetupForPlayer(player))
                return Unsupported("card", player);

            if (tooth.StarterCard?.Id is not ModelId baseId
                || tooth.AncientCard?.Id is not ModelId upgradedId)
                throw new InvalidOperationException("Archaic Tooth produced an incomplete recipe.");

            CardModel? baseCard = FindDeckCard(player, baseId.ToString());
            if (baseCard == null)
                throw new InvalidOperationException(
                    $"Archaic Tooth selected absent starter card {baseId}."
                );

            return new ApProgressiveStarterKindState
            {
                Initialized = true,
                Supported = true,
                BaseId = baseId.ToString(),
                UpgradedId = upgradedId.ToString(),
                SerializedBaseModel = Serialize(baseCard.ToSerializable()),
                SerializedUpgradeRelic = Serialize(tooth.ToSerializable()),
                AppliedTier = ProgressiveStarterTier.Basic,
            };
        }
        finally
        {
            RemoveSetupOnlyCards(captureRunState, preexistingRunCards);
        }
    }

    private static ApProgressiveStarterKindState CaptureRelicSpecification(Player player)
    {
        var touch = (TouchOfOrobas)ModelDb.Relic<TouchOfOrobas>().ToMutable();
        if (!touch.SetupForPlayer(player))
            return Unsupported("relic", player);

        if (touch.StarterRelic is not ModelId baseId
            || touch.UpgradedRelic is not ModelId upgradedId)
            throw new InvalidOperationException("Touch of Orobas produced an incomplete recipe.");

        RelicModel? baseRelic = FindOwnedRelic(player, baseId.ToString());
        if (baseRelic == null)
            throw new InvalidOperationException(
                $"Touch of Orobas selected absent starter relic {baseId}."
            );

        return new ApProgressiveStarterKindState
        {
            Initialized = true,
            Supported = true,
            BaseId = baseId.ToString(),
            UpgradedId = upgradedId.ToString(),
            SerializedBaseModel = Serialize(
                (baseRelic.IsMutable ? baseRelic : baseRelic.ToMutable()).ToSerializable()
            ),
            SerializedUpgradeRelic = Serialize(touch.ToSerializable()),
            AppliedTier = ProgressiveStarterTier.Basic,
        };
    }

    private static ApProgressiveStarterKindState Unsupported(string kind, Player player)
    {
        LogUtility.Warn(
            $"Progressive Starter {kind} is enabled, but {player.Character.Id.Entry} has no "
                + "compatible Orobas mapping; leaving it unchanged."
        );
        return new ApProgressiveStarterKindState
        {
            Initialized = true,
            Supported = false,
            AppliedTier = ProgressiveStarterTier.Unsupported,
        };
    }

    private static void Request(
        ApProgressiveStarterActionMessage message,
        Player owner,
        string description)
    {
        if (RitsuLibManagedNetActions.Request(
                RunManager.Instance,
                ActionDescriptor,
                message,
                owner.NetId
            ))
        {
            LogUtility.Info(
                $"Requested managed Progressive Starter {description} {message.ActionId} "
                    + $"with {message.Targets.Count} target(s)."
            );
            return;
        }

        string reason = $"could not enqueue Progressive Starter {description}";
        LogUtility.Error(reason);
        MultiplayerSupport.InvalidateRunClaims(reason);
        NotificationUtility.ShowRawText("Could not synchronize a Progressive Starter item.");
    }

    private static void FailCapture(string description, Exception ex)
    {
        string reason = $"could not author Progressive Starter {description}: {ex.Message}";
        LogUtility.Error($"{reason}\n{ex}");
        MultiplayerSupport.InvalidateRunClaims(reason);
        NotificationUtility.ShowRawText("Could not synchronize a Progressive Starter item.");
    }

    private static ApProgressiveStarterActionMessage DeserializeMessage(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ApProgressiveStarterActionMessage>(bytes) ?? new();
        }
        catch (JsonException ex)
        {
            LogUtility.Warn($"Could not deserialize managed Progressive Starter payload: {ex.Message}");
            return new ApProgressiveStarterActionMessage();
        }
    }

    private static async Task ExecuteAction(
        RitsuLibManagedNetActionContext<ApProgressiveStarterActionMessage> context)
    {
        ApProgressiveStarterActionMessage message = context.Message;
        if (!TryValidate(message, context.Player, out RunState runState))
        {
            string reason = $"invalid managed Progressive Starter action {message.ActionId}";
            LogUtility.Error(reason);
            MultiplayerSupport.InvalidateRunClaims(reason);
            throw new InvalidOperationException($"Invalid Progressive Starter action {message.ActionId}.");
        }

        try
        {
            foreach (ApProgressiveStarterActionMessage.Target target in message.Targets)
            {
                Player player = runState.GetPlayer(target.PlayerNetId)
                    ?? throw new InvalidOperationException(
                        $"Progressive Starter target {target.PlayerNetId} is absent."
                    );
                await ApplyTarget(runState, player, target);
            }
        }
        catch (Exception ex)
        {
            string reason = $"managed Progressive Starter {message.ActionId} failed: {ex.Message}";
            LogUtility.Error($"{reason}\n{ex}");
            MultiplayerSupport.InvalidateRunClaims(reason);
            throw;
        }
    }

    private static bool TryValidate(
        ApProgressiveStarterActionMessage message,
        Player owner,
        out RunState runState)
    {
        runState = null!;
        if (!MultiplayerSupport.IsRealMultiplayerRun
            || !MultiplayerSupport.ShouldRunReplicatedConstruction(
                MultiplayerFeature.ProgressiveStarters
            )
            || message.SchemaVersion != SchemaVersion
            || message.RunId == Guid.Empty
            || message.ActionId == Guid.Empty
            || message.Reason is not (
                ApProgressiveStarterActionMessage.ActionReason.Initialization
                or ApProgressiveStarterActionMessage.ActionReason.LiveReceipt
            )
            || message.OwnerNetId != owner.NetId
            || message.Targets.Count is < 1 or > 16
            || message.ReceivedItemIndex < 0
            || RunManager.Instance.DebugOnlyGetState() is not RunState current
            || !ApRunData.TryGetSharedState(current, out ApRunSharedState shared)
            || shared.RunId != message.RunId
            || !ApRunData.TryGetPlayerState(current, owner.NetId, out ApPlayerRunState ownerState)
            || ownerState.Participation != ApParticipationKind.OwnApSlot
            || ownerState.ApSlotId != message.ApSlotId)
        {
            return false;
        }

        if (message.Reason == ApProgressiveStarterActionMessage.ActionReason.Initialization
            && (message.ReceivedItemIndex != 0 || message.CharacterOffset.HasValue))
        {
            return false;
        }

        bool ownerIsHost = BetaMainCompatibility.TryGetHostNetId(
                RunManager.Instance.NetService,
                out ulong hostNetId
            ) && owner.NetId == hostNetId;
        var identities = new HashSet<(ulong, ApProgressiveStarterActionMessage.StarterKind)>();
        foreach (ApProgressiveStarterActionMessage.Target target in message.Targets)
        {
            if (!identities.Add((target.PlayerNetId, target.Kind))
                || current.GetPlayer(target.PlayerNetId) is not Player player
                || !ApRunData.TryGetPlayerState(
                    current,
                    target.PlayerNetId,
                    out ApPlayerRunState targetState
                )
                || (target.PlayerNetId != owner.NetId
                    && (!ownerIsHost
                        || targetState.Participation != ApParticipationKind.ApGuest))
                || (target.PlayerNetId == owner.NetId
                    && targetState.Participation != ApParticipationKind.OwnApSlot)
                || !IsEnabledFor(player, target.Kind)
                || target.TargetTier is < ProgressiveStarterTier.Unsupported
                    or > ProgressiveStarterTier.Upgraded
                || !ValidateSpecification(target.Specification, target.TargetTier, player, target.Kind))
            {
                return false;
            }

            if (message.Reason == ApProgressiveStarterActionMessage.ActionReason.LiveReceipt
                && (message.ReceivedItemIndex <= 0
                    || !message.CharacterOffset.HasValue
                    || player.GetCharacterOffset() != message.CharacterOffset
                    || (target.Specification.Supported
                        && target.TargetTier is not (
                            ProgressiveStarterTier.Basic
                            or ProgressiveStarterTier.Upgraded
                        ))))
            {
                return false;
            }
        }

        runState = current;
        return true;
    }

    private static bool ValidateSpecification(
        ApProgressiveStarterKindState specification,
        ProgressiveStarterTier targetTier,
        Player player,
        ApProgressiveStarterActionMessage.StarterKind kind)
    {
        if (!specification.Initialized)
            return false;
        if (!specification.Supported)
        {
            return targetTier == ProgressiveStarterTier.Unsupported
                && specification.AppliedTier == ProgressiveStarterTier.Unsupported
                && specification.BaseId == null
                && specification.UpgradedId == null
                && specification.SerializedBaseModel == null
                && specification.SerializedUpgradeRelic == null;
        }
        if (targetTier == ProgressiveStarterTier.Unsupported
            || specification.AppliedTier is < ProgressiveStarterTier.None
                or > ProgressiveStarterTier.Upgraded
            || string.IsNullOrWhiteSpace(specification.BaseId)
            || string.IsNullOrWhiteSpace(specification.UpgradedId)
            || string.IsNullOrWhiteSpace(specification.SerializedBaseModel)
            || string.IsNullOrWhiteSpace(specification.SerializedUpgradeRelic))
        {
            return false;
        }

        try
        {
            RelicModel upgradeRelic;
            if (kind == ApProgressiveStarterActionMessage.StarterKind.Card)
            {
                SerializableCard baseCard = Deserialize<SerializableCard>(
                    specification.SerializedBaseModel
                );
                if (!IdEquals(baseCard.Id?.ToString(), specification.BaseId))
                    return false;
            }
            else
            {
                RelicModel baseRelic = RelicModel.FromSerializable(
                    Deserialize<SerializableRelic>(specification.SerializedBaseModel)
                );
                if (!IdEquals(baseRelic.Id.ToString(), specification.BaseId))
                    return false;
            }
            upgradeRelic = RelicModel.FromSerializable(
                Deserialize<SerializableRelic>(specification.SerializedUpgradeRelic)
            );
            return kind switch
            {
                ApProgressiveStarterActionMessage.StarterKind.Card =>
                    upgradeRelic is ArchaicTooth tooth
                    && IdEquals(tooth.StarterCard?.Id?.ToString(), specification.BaseId)
                    && IdEquals(tooth.AncientCard?.Id?.ToString(), specification.UpgradedId),
                ApProgressiveStarterActionMessage.StarterKind.Relic =>
                    upgradeRelic is TouchOfOrobas touch
                    && IdEquals(touch.StarterRelic?.ToString(), specification.BaseId)
                    && IdEquals(touch.UpgradedRelic?.ToString(), specification.UpgradedId),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            LogUtility.Warn(
                $"Invalid Progressive Starter {kind} specification for {player.NetId}: {ex.Message}"
            );
            return false;
        }
    }

    private static async Task ApplyTarget(
        RunState runState,
        Player player,
        ApProgressiveStarterActionMessage.Target target)
    {
        if (!ApRunData.TryGetPlayerState(runState, player.NetId, out ApPlayerRunState playerState))
            throw new InvalidOperationException($"No AP run state exists for {player.NetId}.");

        ApProgressiveStarterKindState current = SelectKind(playerState.ProgressiveStarters, target.Kind);
        if (!current.Initialized)
        {
            current = Clone(target.Specification);
            SetKind(playerState.ProgressiveStarters, target.Kind, current);
        }
        else if (!SpecificationsMatch(current, target.Specification))
        {
            throw new InvalidOperationException(
                $"Progressive Starter {target.Kind} recipe changed for {player.NetId}."
            );
        }

        if (!current.Supported)
        {
            if (!ApRunData.SetProgressiveStarterState(
                    runState,
                    player.NetId,
                    playerState.ProgressiveStarters
                ))
            {
                throw new InvalidOperationException(
                    $"Could not persist unsupported Progressive Starter state for {player.NetId}."
                );
            }
            LogUtility.Info(
                $"Managed Progressive Starter {target.Kind} is unsupported for "
                    + $"{player.Character.Id.Entry} ({player.NetId}); no mutation was applied."
            );
            return;
        }

        ProgressiveStarterTier targetTier = target.TargetTier;
        if (current.AppliedTier == ProgressiveStarterTier.Basic
            && targetTier == ProgressiveStarterTier.None)
        {
            await RemoveBase(player, target.Kind, current);
            current.AppliedTier = ProgressiveStarterTier.None;
        }
        else
        {
            if (current.AppliedTier == ProgressiveStarterTier.None
                && targetTier >= ProgressiveStarterTier.Basic)
            {
                await RestoreBase(player, target.Kind, current);
                current.AppliedTier = ProgressiveStarterTier.Basic;
            }

            if (current.AppliedTier == ProgressiveStarterTier.Basic
                && targetTier == ProgressiveStarterTier.Upgraded)
            {
                await GrantUpgradeRelic(player, current);
                current.AppliedTier = ProgressiveStarterTier.Upgraded;
            }
        }

        if (current.AppliedTier != targetTier)
        {
            throw new InvalidOperationException(
                $"Progressive Starter {target.Kind} for {player.NetId} could not transition "
                    + $"from {current.AppliedTier} to {targetTier}."
            );
        }

        SetKind(playerState.ProgressiveStarters, target.Kind, current);
        if (!ApRunData.SetProgressiveStarterState(
                runState,
                player.NetId,
                playerState.ProgressiveStarters
            ))
        {
            throw new InvalidOperationException(
                $"Could not persist Progressive Starter state for {player.NetId}."
            );
        }

        LogUtility.Success(
            $"Managed Progressive Starter {target.Kind} applied tier {targetTier} for "
                + $"{player.Character.Id.Entry} ({player.NetId})."
        );
    }

    private static async Task RemoveBase(
        Player player,
        ApProgressiveStarterActionMessage.StarterKind kind,
        ApProgressiveStarterKindState state)
    {
        if (kind == ApProgressiveStarterActionMessage.StarterKind.Card)
        {
            CardModel? card = FindDeckCard(player, state.BaseId!);
            if (card != null)
                await CardPileCmd.RemoveFromDeck(card, showPreview: false);
            return;
        }

        RelicModel? relic = FindOwnedRelic(player, state.BaseId!);
        if (relic != null)
            await RelicCmd.Remove(relic);
    }

    private static async Task RestoreBase(
        Player player,
        ApProgressiveStarterActionMessage.StarterKind kind,
        ApProgressiveStarterKindState state)
    {
        if (kind == ApProgressiveStarterActionMessage.StarterKind.Card)
        {
            if (FindDeckCard(player, state.BaseId!) != null)
                return;

            CardModel card = player.RunState.LoadCard(
                Deserialize<SerializableCard>(state.SerializedBaseModel!),
                player
            );
            var addResult = await CardPileCmd.Add(card, PileType.Deck, skipVisuals: true);
            if (!addResult.success)
                throw new InvalidOperationException($"The game rejected starter card {card.Id}.");
            return;
        }

        if (FindOwnedRelic(player, state.BaseId!) != null)
            return;

        RelicModel relic = RelicModel.FromSerializable(
            Deserialize<SerializableRelic>(state.SerializedBaseModel!)
        );
        await RelicCmd.Obtain(relic, player);
        if (FindOwnedRelic(player, state.BaseId!) == null)
            throw new InvalidOperationException($"The game rejected starter relic {state.BaseId}.");
    }

    private static async Task GrantUpgradeRelic(
        Player player,
        ApProgressiveStarterKindState state)
    {
        RelicModel relic = RelicModel.FromSerializable(
            Deserialize<SerializableRelic>(state.SerializedUpgradeRelic!)
        );
        if (FindOwnedRelic(player, relic.Id.ToString()) != null)
            return;

        await RelicCmd.Obtain(relic, player);
        if (FindOwnedRelic(player, relic.Id.ToString()) == null)
            throw new InvalidOperationException($"The game rejected Orobas relic {relic.Id}.");
    }

    private static ProgressiveStarterTier GetReceivedTier(
        IReadOnlyDictionary<long, int> received,
        long characterOffset)
    {
        received.TryGetValue(characterOffset, out int count);
        return (ProgressiveStarterTier)Math.Clamp(count, 0, 2);
    }

    private static ApProgressiveStarterKindState SelectKind(
        ApProgressiveStarterPlayerState state,
        ApProgressiveStarterActionMessage.StarterKind kind) =>
        kind == ApProgressiveStarterActionMessage.StarterKind.Card ? state.Card : state.Relic;

    private static void SetKind(
        ApProgressiveStarterPlayerState state,
        ApProgressiveStarterActionMessage.StarterKind kind,
        ApProgressiveStarterKindState value)
    {
        if (kind == ApProgressiveStarterActionMessage.StarterKind.Card)
            state.Card = value;
        else
            state.Relic = value;
    }

    private static bool SpecificationsMatch(
        ApProgressiveStarterKindState left,
        ApProgressiveStarterKindState right) =>
        left.Supported == right.Supported
        && string.Equals(left.BaseId, right.BaseId, StringComparison.Ordinal)
        && string.Equals(left.UpgradedId, right.UpgradedId, StringComparison.Ordinal)
        && string.Equals(
            left.SerializedBaseModel,
            right.SerializedBaseModel,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.SerializedUpgradeRelic,
            right.SerializedUpgradeRelic,
            StringComparison.Ordinal
        );

    private static ApProgressiveStarterKindState Clone(
        ApProgressiveStarterKindState source) => new()
    {
        Initialized = source.Initialized,
        Supported = source.Supported,
        BaseId = source.BaseId,
        UpgradedId = source.UpgradedId,
        SerializedBaseModel = source.SerializedBaseModel,
        SerializedUpgradeRelic = source.SerializedUpgradeRelic,
        AppliedTier = source.AppliedTier,
    };

    private static CardModel? FindDeckCard(Player player, string idEntry) =>
        player.Deck.Cards.FirstOrDefault(card =>
            string.Equals(card.Id.ToString(), idEntry, StringComparison.OrdinalIgnoreCase));

    private static RelicModel? FindOwnedRelic(Player player, string idEntry) =>
        player.Relics.FirstOrDefault(relic =>
            string.Equals(relic.Id.ToString(), idEntry, StringComparison.OrdinalIgnoreCase));

    private static bool IdEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Archaic Tooth's public setup method creates its preview transformation through
    /// RunState.CreateCard. Recipe capture happens only on the action author, so that preview must
    /// be removed again before networking or the replicas would begin with different run cards.
    /// </summary>
    private static IReadOnlyList<CardModel> GetAllRunCards(RunState runState) =>
        AllRunCardsField?.GetValue(runState) as IReadOnlyList<CardModel>
        ?? throw new MissingFieldException(typeof(RunState).FullName, "_allCards");

    private static void RemoveSetupOnlyCards(
        RunState runState,
        IReadOnlySet<CardModel> preexistingRunCards)
    {
        foreach (CardModel card in GetAllRunCards(runState)
                     .Where(card => !preexistingRunCards.Contains(card))
                     .ToArray())
        {
            runState.RemoveCard(card);
        }
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializationUtility.CombinedOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializationUtility.CombinedOptions)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
}
