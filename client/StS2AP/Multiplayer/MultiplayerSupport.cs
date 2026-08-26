using Archipelago.MultiClient.Net.Models;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.Patches;
using StS2AP.UI;
using StS2AP.Utils;
using static StS2AP.Data.ItemTable;

namespace StS2AP.Multiplayer;

/// <summary>
/// Owns the deliberately small experimental multiplayer profile. Singleplayer remains
/// unrestricted; real multiplayer fails closed unless a capability is listed in
/// <see cref="EnabledExperimentalFeatures"/>.
/// </summary>
public static class MultiplayerSupport
{
    // AP_MP: This is the master feature switchboard. Each enabled capability must have an
    // explicit replicated construction path and a single AP-side owner for external effects.
    private static readonly HashSet<MultiplayerFeature> EnabledExperimentalFeatures = new()
    {
        MultiplayerFeature.CharacterUnlocks,
        MultiplayerFeature.PressStartCheck,
        MultiplayerFeature.GoldRewards,
        MultiplayerFeature.CardRewards,
        MultiplayerFeature.RelicRewards,
        MultiplayerFeature.PotionRewards,
        MultiplayerFeature.AncientRewardChoices,
        MultiplayerFeature.CombatRewardLocations,
        MultiplayerFeature.FloorChecks,
        MultiplayerFeature.Shops,
        MultiplayerFeature.RestSites,
        MultiplayerFeature.Ancients,
        MultiplayerFeature.VictoryChecks,
        MultiplayerFeature.ProgressiveStarters,
        MultiplayerFeature.AscensionEffects,
        MultiplayerFeature.DeathLink,
        MultiplayerFeature.SaveAndReconnect,
    };

    internal static bool IsSynchronizedCombatActive =>
        CombatManager.Instance.IsStarting
        || !BetaMainCompatibility.IsActionSynchronizerCombatState(
            RunManager.Instance.ActionQueueSynchronizer.CombatState,
            nameof(ActionSynchronizerCombatState.NotInCombat)
        );

    private static readonly Dictionary<int, IndexedItemInfo> DeferredItems = new();

    private static NCharacterSelectScreen? _observedStartLobbyScreen;
    private static bool _experimentalEnabledForRun;
    private static bool _claimInvalidationNoticeShown;
    private static bool _apHistoryPrepared;
    private static string? _deferredSessionKey;
    private static ApSessionIdentity? _preparedSessionIdentity;
    private static ApParticipationKind? _activeParticipation;
    private static IReadOnlyList<ItemInfo> _preparedReceivedItems = Array.Empty<ItemInfo>();

    private readonly record struct ApSessionIdentity(string RoomSeed, int ApTeamId, int ApSlotId)
    {
        public override string ToString() =>
            $"{RoomSeed}/ap-team-{ApTeamId}/ap-slot-{ApSlotId}";
    }

    public static ApPlayDestination PendingDestination { get; private set; } =
        ApPlayDestination.None;

    public static ApParticipationKind PendingParticipation { get; private set; } =
        ApParticipationKind.VanillaGuest;

    public static bool IsRealMultiplayerRun { get; private set; }

    public static bool IsExperimentalMultiplayerRun =>
        IsRealMultiplayerRun && _experimentalEnabledForRun;

    public static bool IsLocalGuest => IsRealMultiplayerRun
        ? _activeParticipation == ApParticipationKind.VanillaGuest
        : PendingDestination == ApPlayDestination.Multiplayer
            && PendingParticipation == ApParticipationKind.VanillaGuest;

    public static bool IsLocalApGuest => IsRealMultiplayerRun
        ? _activeParticipation == ApParticipationKind.ApGuest
        : PendingDestination == ApPlayDestination.Multiplayer
            && PendingParticipation == ApParticipationKind.ApGuest;

    public static bool IsLocalOwnApSlot => IsRealMultiplayerRun
        ? _activeParticipation == ApParticipationKind.OwnApSlot
        : PendingDestination == ApPlayDestination.Multiplayer
            && PendingParticipation == ApParticipationKind.OwnApSlot;

    public static bool IsLocalApParticipant =>
        !IsLocalGuest && (IsRealMultiplayerRun || PendingDestination == ApPlayDestination.Multiplayer);

    public static bool UsesFrozenHostSettings => IsLocalApGuest
        || IsRealMultiplayerRun
            && IsLocalOwnApSlot
            && RunManager.Instance.NetService.Type == NetGameType.Host;

    public static bool ClaimsInvalidated { get; private set; }

    public static bool ExperimentalSettingEnabled =>
        ArchipelagoClient.LocalSettings.Value.EnableExperimentalMultiplayer;

    /// <summary>
    /// True while the player is entering multiplayer or is already in a real multiplayer run.
    /// The pending intent is needed because AP items can arrive in the lobby before RunManager
    /// has a RunState.
    /// </summary>
    public static bool IsMultiplayerScope =>
        IsRealMultiplayerRun || PendingDestination == ApPlayDestination.Multiplayer;

    public static IReadOnlyCollection<IndexedItemInfo> PendingUnsupportedItems =>
        DeferredItems.Values.OrderBy(item => item.Index).ToArray();

    public static string? PreparedApRoomSeed => _preparedSessionIdentity?.RoomSeed;

    public static int? PreparedApTeamId => _preparedSessionIdentity?.ApTeamId;

    public static int? PreparedApSlotId => _preparedSessionIdentity?.ApSlotId;

    public static bool InitialItemsLoaded => _apHistoryPrepared;

    public static bool HostReceiptCatalogReady => ApReceiptRelay.GuestCatalogReady;

    public static SharedSlotCheckScope ConfiguredSharedSlotCheckScope =>
        string.Equals(
            ArchipelagoClient.LocalSettings.Value.SharedSlotCheckScope,
            "AllAPParticipants",
            StringComparison.Ordinal
        )
            ? SharedSlotCheckScope.AllApParticipants
            : SharedSlotCheckScope.HostCharacterOnly;

    /// <summary>
    /// Exposes only the currently displayed start lobby for read-only diagnostics. This is not
    /// a saved lobby handle: clients normally see their own staged AP contribution, while the
    /// host's lobby session contains the contributions merged from every peer.
    /// </summary>
    public static bool TryGetObservedStartLobby(out StartRunLobby lobby)
    {
        NCharacterSelectScreen? screen = _observedStartLobbyScreen;
        if (screen != null && GodotObject.IsInstanceValid(screen))
        {
            lobby = screen.Lobby;
            return true;
        }

        lobby = null!;
        return false;
    }

    /// <summary>
    /// Requests re-evaluation of the host's Ready UI after authoritative lobby staging changes
    /// or the final launch guard rejects a race. Defer the Godot work because either call can
    /// occur inside a network handler.
    /// </summary>
    public static void RequestHostLobbyRefresh(StartRunLobby lobby)
    {
        NCharacterSelectScreen? screen = _observedStartLobbyScreen;
        if (screen == null
            || !GodotObject.IsInstanceValid(screen)
            || !ReferenceEquals(screen.Lobby, lobby))
        {
            return;
        }

        Callable.From(RefreshObservedStartLobby).CallDeferred();
    }

    public static void ClearPendingPlaySelection()
    {
        PendingDestination = ApPlayDestination.None;
        PendingParticipation = ApParticipationKind.VanillaGuest;
        ApReceiptRelay.ResetGuestCatalog();
    }

    public static void BeginMultiplayerEntry()
    {
        PendingParticipation = ArchipelagoClient.IsConnected
            ? ApParticipationKind.OwnApSlot
            : string.Equals(
                ArchipelagoClient.LocalSettings.Value.GuestRewardMode,
                "APGuest",
                StringComparison.Ordinal
            )
                ? ApParticipationKind.ApGuest
                : ApParticipationKind.VanillaGuest;
        if (PendingParticipation == ApParticipationKind.ApGuest)
            ApReceiptRelay.ResetGuestCatalog();
        SelectDestination(ApPlayDestination.Multiplayer);
    }

    public static void BeginApBoundMultiplayerEntry()
    {
        PendingParticipation = ApParticipationKind.OwnApSlot;
        SelectDestination(ApPlayDestination.Multiplayer);
    }

    public static void SelectDestination(ApPlayDestination destination)
    {
        PendingDestination = destination;

        // The user can switch flows without reconnecting to AP. Keep the already-created
        // Death Link service aligned with the newly selected capability profile.
        if (ArchipelagoClient.IsConnected)
        {
            if (DeathLinkUtility.IsDeathLinkEnabled)
                ArchipelagoClient.DeathLinkController.EnableDeathLink();
            else
                ArchipelagoClient.DeathLinkController.DisableDeathLink();

            if (destination == ApPlayDestination.Singleplayer)
                PendingCheckUtility.ReconcileAndSend();
        }

        LogUtility.Info($"Selected AP play destination: {destination}");
    }

    public static bool IsFeatureEnabled(MultiplayerFeature feature)
    {
        if (!IsMultiplayerScope)
            return true;

        if (IsLocalGuest)
            return false;

        bool profileEnabled = IsRealMultiplayerRun
            ? _experimentalEnabledForRun
            : ExperimentalSettingEnabled;
        return profileEnabled && EnabledExperimentalFeatures.Contains(feature);
    }

    /// <summary>
    /// Shop inventories are local presentation state. Own-slot players and AP Guests apply
    /// their AP source's slot unlocks only to the locally displayed inventory; Vanilla Guests
    /// and remote inventory replicas remain native.
    /// </summary>
    public static bool ShouldApplyLocalShopUnlocks(Player player) =>
        IsFeatureEnabled(MultiplayerFeature.Shops)
        && MultiplayerLocationChecks.IsLocalProgressOwner(player);

    /// <summary>
    /// An AP-check page is shown only to the process that can write checks for its local player.
    /// AP Guests use the host slot's unlocks but never receive a competing shared-slot page.
    /// </summary>
    public static bool ShouldShowLocalShopChecks(Player player) =>
        ShouldApplyLocalShopUnlocks(player)
        && MultiplayerLocationChecks.IsCheckWriter(player);

    /// <summary>
    /// Feature gate for native callbacks that construct state for every player on every replica.
    /// Participant ownership is evaluated separately for the callback's concrete player.
    /// </summary>
    public static bool ShouldRunReplicatedConstruction(MultiplayerFeature feature)
    {
        if (!IsMultiplayerScope)
            return true;
        bool profileEnabled = IsRealMultiplayerRun
            ? _experimentalEnabledForRun
            : ExperimentalSettingEnabled;
        return profileEnabled && EnabledExperimentalFeatures.Contains(feature);
    }

    public static MultiplayerFeature GetFeatureForItem(IndexedItemInfo indexedItem)
    {
        var item = indexedItem.Item;
        if (item.ItemId < 10000)
            return IsUniversalCombatBuff(item.ItemId)
                ? MultiplayerFeature.GoldRewards
                : MultiplayerFeature.UnknownReceivedItems;

        return item.GetCharacterSpecificItemID() switch
        {
            APItem.Unlock => MultiplayerFeature.CharacterUnlocks,
            APItem.OneGold or APItem.FiveGold or APItem.CombatGold or APItem.EliteGold
                or APItem.BossGold => MultiplayerFeature.GoldRewards,
            APItem.CardReward or APItem.RareCardReward => MultiplayerFeature.CardRewards,
            APItem.Relic => MultiplayerFeature.RelicRewards,
            APItem.Potion => MultiplayerFeature.PotionRewards,
            APItem.ProgressiveRest or APItem.ProgressiveSmith => MultiplayerFeature.RestSites,
            APItem.ProgressiveAncient => MultiplayerFeature.AncientRewardChoices,
            APItem.ShopCardSlot or APItem.NeutralShopCardSlot or APItem.ShopRelicSlot
                or APItem.ShopPotionSlot or APItem.ProgressiveShopRemove =>
                    MultiplayerFeature.Shops,
            APItem.ProgressiveStarterCard or APItem.ProgressiveStarterRelic =>
                MultiplayerFeature.ProgressiveStarters,
            APItem.SwarmingElites or APItem.WearyTraveler or APItem.Poverty
                or APItem.TightBelt or APItem.AscenderBane or APItem.Inflation
                or APItem.Scarcity or APItem.ToughEnemies or APItem.DeadlyEnemies
                or APItem.DoubleBoss => MultiplayerFeature.AscensionEffects,
            _ => MultiplayerFeature.UnknownReceivedItems,
        };
    }

    // AP_MP: Unsupported receipt types are held here instead of mutating replicated state.
    public static bool ShouldDeferItem(IndexedItemInfo item) =>
        IsMultiplayerScope && !IsFeatureEnabled(GetFeatureForItem(item));

    public static void DeferItem(IndexedItemInfo item)
    {
        if (DeferredItems.TryAdd(item.Index, item))
        {
            LogUtility.Warn(
                $"Deferred AP item index {item.Index} ({item.Item.ItemName}); "
                    + $"multiplayer feature {GetFeatureForItem(item)} is disabled"
            );
        }
    }

    /// <summary>Rejects a reconnect that would replace the AP owner of an active STS lobby/run.</summary>
    public static bool ValidateApSessionIdentity(
        string roomSeed,
        int apTeamId,
        int apSlotId,
        out string reason)
    {
        reason = string.Empty;
        var candidate = new ApSessionIdentity(roomSeed, apTeamId, apSlotId);
        bool identityLocked =
            ApReconnectController.IsActive
            || _observedStartLobbyScreen != null
            || IsRealMultiplayerRun;
        if (identityLocked
            && _preparedSessionIdentity is { } expected
            && expected != candidate)
        {
            reason = $"Expected AP session {expected}, but connected to {candidate}.";
            return false;
        }

        return true;
    }

    /// <summary>Records every successful login so deferred state cannot cross AP identities.</summary>
    public static void NoteApSessionConnected(string roomSeed, int apTeamId, int apSlotId)
    {
        var identity = new ApSessionIdentity(roomSeed, apTeamId, apSlotId);
        string sessionKey = identity.ToString();
        if (_deferredSessionKey != null && _deferredSessionKey != sessionKey)
        {
            LogUtility.Info(
                $"Discarding {DeferredItems.Count} deferred multiplayer item(s) from the previous AP session"
            );
            DeferredItems.Clear();
            _apHistoryPrepared = false;
        }

        _preparedSessionIdentity = identity;
        _deferredSessionKey = sessionKey;
    }

    /// <summary>
    /// Deterministically prepares the deliberately small multiplayer receipt profile while
    /// initial SDK callbacks are still blocked. Unsupported receipts are retained by index,
    /// character unlocks are replayed idempotently, and gold is rebuilt separately.
    /// </summary>
    public static bool PrepareApSession(
        string roomSeed,
        int apTeamId,
        int apSlotId,
        IReadOnlyList<ItemInfo> receivedItems,
        out string reason)
    {
        reason = string.Empty;
        if (!ValidateApSessionIdentity(roomSeed, apTeamId, apSlotId, out reason))
            return false;

        if (ArchipelagoClient.Settings?.Characters == null
            || ArchipelagoClient.Settings.Characters.Count == 0)
        {
            reason = "The AP slot did not provide any usable character settings.";
            return false;
        }

        var identity = new ApSessionIdentity(roomSeed, apTeamId, apSlotId);
        string sessionKey = identity.ToString();

        DeferredItems.Clear();
        ArchipelagoClient.Progress.AllReceivedItems.Clear();
        ArchipelagoClient.Progress.ProgressiveAncients.Clear();
        ArchipelagoClient.Progress.ProgressiveRests.Clear();
        ArchipelagoClient.Progress.ProgressiveSmiths.Clear();
        ArchipelagoClient.Progress.ProgressiveStarterCards.Clear();
        ArchipelagoClient.Progress.ProgressiveStarterRelics.Clear();
        ArchipelagoClient.Progress.ProgressiveStarterCardBaseId = null;
        ArchipelagoClient.Progress.ProgressiveStarterCardUpgradedId = null;
        ArchipelagoClient.Progress.ProgressiveStarterCardTier =
            ProgressiveStarterTier.Unsupported;
        ArchipelagoClient.Progress.ProgressiveStarterRelicBaseId = null;
        ArchipelagoClient.Progress.ProgressiveStarterRelicUpgradedId = null;
        ArchipelagoClient.Progress.ProgressiveStarterRelicTier =
            ProgressiveStarterTier.Unsupported;
        ArchipelagoClient.Progress.ShopCardSlotsReceived.Clear();
        ArchipelagoClient.Progress.ShopNeutralSlotsReceived.Clear();
        ArchipelagoClient.Progress.ShopRelicSlotsReceived.Clear();
        ArchipelagoClient.Progress.ShopPotionSlotsReceived.Clear();
        ArchipelagoClient.Progress.ShopRemovesReceived.Clear();
        var ancientCounts = new Dictionary<long, int>();
        for (int index = 0; index < receivedItems.Count; index++)
        {
            ItemInfo item = receivedItems[index];
            var indexedItem = new IndexedItemInfo(item, index + 1);
            MultiplayerFeature feature = GetFeatureForItem(indexedItem);
            if (feature == MultiplayerFeature.CharacterUnlocks)
            {
                GameUtility.UnlockCharacter(item);
            }
            else if (feature == MultiplayerFeature.GoldRewards)
            {
                // Aggregate gold is reconstructed below rather than stored as discrete rows.
            }
            else if (!IsFeatureEnabled(feature))
            {
                DeferItem(indexedItem);
            }
            else if (item.ItemId >= 10000
                && item.GetCharacterSpecificItemID() == APItem.ProgressiveAncient)
            {
                long characterOffset = item.GetCharacterOffset();
                ancientCounts.TryGetValue(characterOffset, out int count);
                count++;
                ancientCounts[characterOffset] = count;
                ArchipelagoClient.Progress.ProgressiveAncients[characterOffset] = count;

                if (ArchipelagoClient.Settings.AncientRelicLocation == AncientRelicLocation.Anytime
                    && (!ArchipelagoClient.Settings.NeowSanity || count > 1))
                {
                    ArchipelagoClient.Progress.AllReceivedItems.Add(indexedItem);
                }
            }
            else if (feature == MultiplayerFeature.RestSites && item.ItemId >= 10000)
            {
                Dictionary<long, int>? counts = item.GetCharacterSpecificItemID() switch
                {
                    APItem.ProgressiveRest => ArchipelagoClient.Progress.ProgressiveRests,
                    APItem.ProgressiveSmith => ArchipelagoClient.Progress.ProgressiveSmiths,
                    _ => null,
                };
                if (counts != null)
                {
                    long characterOffset = item.GetCharacterOffset();
                    counts.TryGetValue(characterOffset, out int count);
                    counts[characterOffset] = count + 1;
                }
            }
            else if (feature == MultiplayerFeature.ProgressiveStarters
                && item.ItemId >= 10000)
            {
                Dictionary<long, int>? counts = item.GetCharacterSpecificItemID() switch
                {
                    APItem.ProgressiveStarterCard =>
                        ArchipelagoClient.Progress.ProgressiveStarterCards,
                    APItem.ProgressiveStarterRelic =>
                        ArchipelagoClient.Progress.ProgressiveStarterRelics,
                    _ => null,
                };
                if (counts != null)
                {
                    long characterOffset = item.GetCharacterOffset();
                    counts.TryGetValue(characterOffset, out int count);
                    counts[characterOffset] = count + 1;
                }
            }
            else if (feature == MultiplayerFeature.Shops && item.ItemId >= 10000)
            {
                Dictionary<long, int>? counts = item.GetCharacterSpecificItemID() switch
                {
                    APItem.ShopCardSlot => ArchipelagoClient.Progress.ShopCardSlotsReceived,
                    APItem.NeutralShopCardSlot =>
                        ArchipelagoClient.Progress.ShopNeutralSlotsReceived,
                    APItem.ShopRelicSlot => ArchipelagoClient.Progress.ShopRelicSlotsReceived,
                    APItem.ShopPotionSlot => ArchipelagoClient.Progress.ShopPotionSlotsReceived,
                    APItem.ProgressiveShopRemove =>
                        ArchipelagoClient.Progress.ShopRemovesReceived,
                    _ => null,
                };
                if (counts != null)
                {
                    long characterOffset = item.GetCharacterOffset();
                    counts.TryGetValue(characterOffset, out int count);
                    counts[characterOffset] = count + 1;
                }
                ArchipelagoClient.Progress.AllReceivedItems.Add(indexedItem);
            }
            else
            {
                ArchipelagoClient.Progress.AllReceivedItems.Add(indexedItem);
            }
        }

        ApGrantDispatcher.RebuildGoldBank(receivedItems);
        _preparedSessionIdentity = identity;
        _deferredSessionKey = sessionKey;
        _preparedReceivedItems = receivedItems.ToArray();

        // Durable consumption and assignments are restored separately from the host-owned
        // ApRunProgressState snapshot. This flag says only that the transient receipt catalog is
        // complete enough to reconcile against that progress.
        _apHistoryPrepared = true;
        RefreshObservedStartLobby();
        LogUtility.Info(
            $"Prepared AP multiplayer session {identity}: receipts={receivedItems.Count}, "
                + $"deferred={DeferredItems.Count}"
        );
        return true;
    }

    public static bool PrepareApGuestSession(
        string roomSeed,
        int apTeamId,
        int apSlotId,
        ArchipelagoSettings hostSettings,
        IReadOnlyList<ItemInfo> receivedItems,
        out string reason)
    {
        // The fixed STS host, not any AP identity previously used by this process, owns an
        // AP Guest's receipt source. Clear a stale pre-lobby identity, but never weaken the
        // identity lock after a run has actually bound this Net ID.
        if (!IsRealMultiplayerRun)
            _preparedSessionIdentity = null;
        if (!ValidateApSessionIdentity(roomSeed, apTeamId, apSlotId, out reason))
            return false;
        ArchipelagoClient.UseMultiplayerHostSettings(hostSettings);
        ArchipelagoClient.RebuildUnlockedCharactersFromSettings();
        return PrepareApSession(roomSeed, apTeamId, apSlotId, receivedItems, out reason);
    }

    public static void OnApDisconnected()
    {
        _apHistoryPrepared = false;
        // AP socket termination may be raised off the Godot main thread.
        Callable.From(RefreshObservedStartLobby).CallDeferred();
    }

    public static bool CanEnterMultiplayerLobby(out string reason)
    {
        if (PendingParticipation is ApParticipationKind.VanillaGuest or ApParticipationKind.ApGuest)
        {
            reason = string.Empty;
            return true;
        }

        if (!ArchipelagoClient.IsConnected)
        {
            reason = "This AP-bound player must reconnect before opening the multiplayer lobby.";
            return false;
        }

        if (!_apHistoryPrepared || _preparedSessionIdentity == null)
        {
            reason = "Archipelago is still preparing slot settings and received-item history.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Hosting is AP-authoritative, so only a process connected to and prepared for its own AP
    /// slot may create a lobby. Disconnected players remain free to enter the native Join flow.
    /// </summary>
    public static bool CanHostMultiplayer(out string reason)
    {
        if (!ArchipelagoClient.IsConnected)
        {
            reason = "Connect to an Archipelago slot before hosting multiplayer. You can still join as a guest.";
            return false;
        }

        if (PendingDestination != ApPlayDestination.Multiplayer
            || PendingParticipation != ApParticipationKind.OwnApSlot)
        {
            reason = "Archipelago has not prepared this player to host with its connected slot.";
            return false;
        }

        return CanEnterMultiplayerLobby(out reason);
    }

    public static bool CanEmbark(CharacterModel character, out string reason)
    {
        if (!CanEnterMultiplayerLobby(out reason))
            return false;

        if (PendingParticipation == ApParticipationKind.VanillaGuest)
        {
            reason = string.Empty;
            return true;
        }

        if (PendingParticipation == ApParticipationKind.ApGuest
            && !HostReceiptCatalogReady)
        {
            reason = "Waiting for the host's AP settings and received-item catalog.";
            return false;
        }

        if (!ArchipelagoClient.Settings.Characters.ContainsKey(character.Id.Entry))
        {
            reason = $"Character {character.Id.Entry} is not configured for this AP slot.";
            return false;
        }

        if (!ArchipelagoClient.Progress.UnlockedCharacters.Any(
                unlocked => unlocked.Id == character.Id))
        {
            reason = $"Character {character.Id.Entry} is not unlocked for this AP slot.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool CanLaunchRun(RunState runState, out string reason)
    {
        Player? localPlayer = runState.Players.FirstOrDefault(
            player => player.NetId == RunManager.Instance.NetService.NetId
        );
        if (localPlayer == null)
        {
            reason = "The local STS multiplayer player could not be resolved.";
            return false;
        }

        if (ApRunData.TryGetLocalPlayerState(runState, localPlayer.NetId, out ApPlayerRunState savedState)
            && !ValidateReturningPlayerIdentity(savedState, out reason))
        {
            reason = "The saved campaign cannot be loaded by this AP identity: " + reason;
            return false;
        }

        return CanEmbark(localPlayer.Character, out reason);
    }

    public static void ObserveStartLobby(NCharacterSelectScreen screen)
    {
        if (PendingDestination != ApPlayDestination.Multiplayer)
            return;

        _observedStartLobbyScreen = screen;
        if (PendingParticipation == ApParticipationKind.ApGuest
            && !HostReceiptCatalogReady)
        {
            ApReceiptRelay.RequestSnapshot(screen.Lobby.NetService);
        }
        ApRunData.StageLocalPlayer(screen.Lobby);
        RefreshObservedStartLobby();
    }

    internal static void NotifyApGuestCatalogInstalled() =>
        Callable.From(RefreshObservedStartLobby).CallDeferred();

    internal static void NotifyApGuestCatalogInvalidated() =>
        Callable.From(RefreshObservedStartLobby).CallDeferred();

    public static void StopObservingStartLobby(NCharacterSelectScreen screen)
    {
        if (ReferenceEquals(_observedStartLobbyScreen, screen))
            _observedStartLobbyScreen = null;
    }

    private static void RefreshObservedStartLobby()
    {
        NCharacterSelectScreen? screen = _observedStartLobbyScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
        {
            _observedStartLobbyScreen = null;
            return;
        }

        try
        {
            // A shared-slot AP Guest initially opens this screen with every AP character
            // locked. The host catalog is installed later on the Godot main thread, so refresh
            // visibility/unlocks before re-evaluating readiness.
            Patches_UnlockCharacters.OverrideCharacterSelectMenuOptions
                .RefreshForCurrentParticipation(screen);
            ApRunData.StageLocalPlayer(screen.Lobby);
            NConfirmButton embarkButton = screen.GetNode<NConfirmButton>("ConfirmButton");
            if (!CanEnterMultiplayerLobby(out _))
            {
                EnsureLocalPlayerUnready(screen);
                embarkButton.Disable();
                return;
            }

            if (screen.Lobby.NetService.Type == NetGameType.Host
                && !ApRunData.TryValidateHostLobbyContributions(
                    screen.Lobby,
                    out string hostBlockedReason))
            {
                bool wasReady = BetaMainCompatibility.IsLocalPlayerReady(screen.Lobby);
                EnsureLocalPlayerUnready(screen);
                embarkButton.Disable();
                if (wasReady)
                {
                    NotificationUtility.ShowRawText(
                        $"Host became unready: {hostBlockedReason}"
                    );
                }
                return;
            }

            if (BetaMainCompatibility.IsLocalPlayerReady(screen.Lobby))
                return;

            if (CanEmbark(BetaMainCompatibility.GetLocalCharacter(screen.Lobby), out _))
                embarkButton.Enable();
            else
                embarkButton.Disable();
        }
        catch (Exception ex)
        {
            LogUtility.Warn($"Could not refresh AP multiplayer lobby readiness: {ex.Message}");
        }
    }

    private static void EnsureLocalPlayerUnready(NCharacterSelectScreen screen)
    {
        if (!BetaMainCompatibility.IsLocalPlayerReady(screen.Lobby))
            return;

        // Use the native UI transition so auto-unready restores character buttons and the
        // waiting panel as well as changing the lobby flag.
        try
        {
            var nativeUnready = AccessTools.Method(
                typeof(NCharacterSelectScreen),
                "OnUnreadyPressed"
            );
            if (nativeUnready != null)
                nativeUnready.Invoke(screen, new object?[] { null });
            else
                screen.Lobby.SetReady(ready: false);
        }
        catch (Exception ex)
        {
            LogUtility.Warn(
                $"Native AP lobby auto-unready failed: {ex.GetBaseException().Message}"
            );
            screen.Lobby.SetReady(ready: false);
        }
    }

    public static IReadOnlyList<IndexedItemInfo> TakeDeferredItemsForSingleplayer()
    {
        if (PendingDestination != ApPlayDestination.Singleplayer || IsRealMultiplayerRun)
            return Array.Empty<IndexedItemInfo>();

        var items = DeferredItems.Values.OrderBy(item => item.Index).ToArray();
        DeferredItems.Clear();
        return items;
    }

    /// <summary>
    /// Binds AP ownership only after MegaCrit has assigned LocalContext in RunManager.Launch.
    /// </summary>
    public static Player? BeginRun(RunState runState)
    {
        EndRun();

        IsRealMultiplayerRun =
            RunManager.Instance.NetService.Type != NetGameType.Singleplayer;
        _experimentalEnabledForRun = ExperimentalSettingEnabled;
        PendingDestination = IsRealMultiplayerRun
            ? ApPlayDestination.Multiplayer
            : ApPlayDestination.Singleplayer;

        if (!IsRealMultiplayerRun)
            return null;

        if (!_experimentalEnabledForRun)
        {
            LogUtility.Warn(
                "A multiplayer run launched without the experimental AP setting; "
                    + "AP multiplayer binding remains disabled"
            );
            return null;
        }

        Player? localPlayer;
        try
        {
            localPlayer = LocalContext.GetMe(runState);
        }
        catch (Exception ex)
        {
            ClaimsInvalidated = true;
            LogUtility.Error($"Could not resolve the local multiplayer player: {ex.Message}");
            return null;
        }

        if (localPlayer == null)
        {
            ClaimsInvalidated = true;
            LogUtility.Error("Could not bind the local AP player after multiplayer launch");
            return null;
        }

        _activeParticipation = PendingParticipation;
        if (ApRunData.TryGetLocalPlayerState(runState, localPlayer.NetId, out var savedPlayerState))
        {
            _activeParticipation = savedPlayerState.Participation;
            if (!ValidateReturningPlayerIdentity(savedPlayerState, out string identityReason))
            {
                ClaimsInvalidated = true;
                LogUtility.Error($"Saved AP multiplayer identity mismatch: {identityReason}");
                Callable.From(() => NotificationUtility.ShowRawText(
                    "This saved campaign belongs to a different AP participation identity. "
                        + "AP progress and rewards are disabled for this run."
                )).CallDeferred();
            }
        }

        if (_activeParticipation == ApParticipationKind.OwnApSlot
            && RunManager.Instance.NetService.Type == NetGameType.Host
            && ApRunData.TryGetSharedState(runState, out ApRunSharedState hostShared)
            && hostShared.HostSettings != null)
        {
            ArchipelagoClient.UseMultiplayerHostSettings(hostShared.HostSettings);
        }

        if (_activeParticipation == ApParticipationKind.ApGuest)
        {
            if (!ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
                || shared.HostSettings == null
                || !BetaMainCompatibility.TryGetHostNetId(
                    RunManager.Instance.NetService,
                    out ulong hostNetId
                )
                || !ApRunData.TryGetPlayerState(runState, hostNetId, out ApPlayerRunState hostState)
                || hostState.ApRoomSeed == null
                || hostState.ApTeamId == null
                || hostState.ApSlotId == null)
            {
                ClaimsInvalidated = true;
                LogUtility.Error("AP Guest launched without frozen host settings/source identity.");
                return localPlayer;
            }

            ArchipelagoClient.UseMultiplayerHostSettings(shared.HostSettings);
            _preparedSessionIdentity = new ApSessionIdentity(
                hostState.ApRoomSeed,
                hostState.ApTeamId.Value,
                hostState.ApSlotId.Value
            );
            _preparedReceivedItems = ApReceiptRelay.GetGuestItems();
            if (!HostReceiptCatalogReady)
                ApReceiptRelay.RequestSnapshot(RunManager.Instance.NetService);
        }

        LogUtility.Info(
            $"Experimental AP multiplayer launched: enabled={_experimentalEnabledForRun}, "
                + $"netType={RunManager.Instance.NetService.Type}, localNetId={localPlayer.NetId}, "
                + $"players=[{string.Join(",", runState.Players.Select(p => p.NetId))}]"
        );
        return localPlayer;
    }

    private static bool ValidateReturningPlayerIdentity(
        ApPlayerRunState savedState,
        out string reason)
    {
        if (savedState.Participation != PendingParticipation)
        {
            reason = $"saved participation is {savedState.Participation}, but this process "
                + $"entered as {PendingParticipation}";
            return false;
        }

        if (savedState.Participation != ApParticipationKind.OwnApSlot)
        {
            reason = string.Empty;
            return true;
        }

        if (_preparedSessionIdentity is not { } prepared
            || savedState.ApRoomSeed == null
            || savedState.ApTeamId == null
            || savedState.ApSlotId == null)
        {
            reason = "the saved or currently prepared AP slot identity is incomplete";
            return false;
        }

        if (!string.Equals(savedState.ApRoomSeed, prepared.RoomSeed, StringComparison.Ordinal)
            || savedState.ApTeamId != prepared.ApTeamId
            || savedState.ApSlotId != prepared.ApSlotId)
        {
            reason = $"saved={savedState.ApRoomSeed}/ap-team-{savedState.ApTeamId}/"
                + $"ap-slot-{savedState.ApSlotId}, prepared={prepared}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static IReadOnlyList<ItemInfo> GetPreparedReceivedItems() => _preparedReceivedItems;

    /// <summary>
    /// Returns the current authoritative SDK history for a connected own-slot process. Lobby
    /// staging uses this instead of the connection-time snapshot so receipts received while the
    /// character screen is open are included before launch.
    /// </summary>
    public static IReadOnlyList<ItemInfo> GetCurrentOwnSlotReceivedItems() =>
        ArchipelagoClient.IsConnected
            ? ArchipelagoClient.Session.Items.AllItemsReceived
            : _preparedReceivedItems;

    public static bool RestorePreparedReceiptView(out string reason)
    {
        if (_preparedSessionIdentity is not { } identity)
        {
            reason = "No AP receipt source is bound to the local multiplayer player.";
            return false;
        }

        // Login can complete before the SDK has replayed the slot's complete received-item
        // history. The launch boundary is later and must refresh an own-slot participant from
        // the authoritative SDK list instead of restoring the early connection snapshot.
        IReadOnlyList<ItemInfo> receivedItems = _preparedReceivedItems;
        if (IsLocalOwnApSlot && ArchipelagoClient.IsConnected)
            receivedItems = ArchipelagoClient.Session.Items.AllItemsReceived.ToArray();

        if (!PrepareApSession(
            identity.RoomSeed,
            identity.ApTeamId,
            identity.ApSlotId,
            receivedItems,
            out reason
        ))
        {
            return false;
        }

        if (IsLocalOwnApSlot)
        {
            _preparedReceivedItems = receivedItems.ToArray();
            ApReceiptRelay.ReplaceHostCatalog(
                identity.RoomSeed,
                identity.ApTeamId,
                identity.ApSlotId,
                receivedItems
            );
        }
        return true;
    }

    public static ArchipelagoSettings? CreateEffectiveHostSettingsSnapshot()
    {
        ArchipelagoSettings? source = ArchipelagoClient.Settings;
        if (source == null)
            return null;

        ClientSettings local = ArchipelagoClient.LocalSettings.Value;
        // TODO: is there seriously no automatic setter for this? where snapshot = source and then do slight modifications after
        var snapshot = new ArchipelagoSettings
        {
            AscensionLevel = source.AscensionLevel,
            ShouldShuffleAllCards = source.ShouldShuffleAllCards,
            IsSeeded = source.IsSeeded,
            NoCharactersLocked = source.NoCharactersLocked,
            NumCharsGoal = source.NumCharsGoal,
            TotalCharacters = source.TotalCharacters,
            NeowSanity = source.NeowSanity,
            AncientRelicLocation = source.AncientRelicLocation,
            AncientRelicPool = source.AncientRelicPool,
            RelicRewardsAvailableAnytime = local.OverrideRelicRewardsAvailableAnytime
                ? local.RelicRewardsAvailableAnytime
                : source.RelicRewardsAvailableAnytime,
            ReleaseOnVictory = source.ReleaseOnVictory,
            CampfireSanity = source.CampfireSanity,
            GoldSanity = source.GoldSanity,
            PotionSanity = source.PotionSanity,
            Floorsanity = source.Floorsanity,
            ProgressiveStarterCard = source.ProgressiveStarterCard,
            ProgressiveStarterRelic = source.ProgressiveStarterRelic,
            ShopSanity = source.ShopSanity,
            ShopCardSlots = source.ShopCardSlots,
            ShopNeutralSlots = source.ShopNeutralSlots,
            ShopRelicSlots = source.ShopRelicSlots,
            ShopPotionSlots = source.ShopPotionSlots,
            ShopRemoveSlots = source.ShopRemoveSlots,
            ShopSanityCosts = source.ShopSanityCosts,
            IsDeathLinkEnabled = local.OverrideDeathLinkOptions
                ? local.EnableDeathLink
                : source.IsDeathLinkEnabled,
            EnableDeathFragments = local.OverrideDeathLinkOptions
                ? local.EnableDeathFragments
                : source.EnableDeathFragments,
            DeathLinkDamagePercent = local.OverrideDeathLinkOptions
                ? local.DeathLinkPercentDamage
                : source.DeathLinkDamagePercent,
            APWorldVersion = source.APWorldVersion,
        };

        foreach ((string key, CharacterConfig config) in source.Characters)
            snapshot.Characters[key] = CloneCharacterConfig(config);
        foreach ((string key, CharacterConfig config) in source.UnrecognizedCharacters)
            snapshot.UnrecognizedCharacters[key] = CloneCharacterConfig(config);
        return snapshot;
    }

    public static ArchipelagoSettings? GetHostSettingsForReceiptRelay()
    {
        if (IsRealMultiplayerRun
            && RunManager.Instance.DebugOnlyGetState() is RunState runState
            && ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            && shared.HostSettings != null)
        {
            return shared.HostSettings;
        }
        return CreateEffectiveHostSettingsSnapshot();
    }

    public static void RestoreFrozenHostSettingsForActiveRun()
    {
        if (!IsRealMultiplayerRun
            || RunManager.Instance.NetService.Type != NetGameType.Host
            || RunManager.Instance.DebugOnlyGetState() is not RunState runState
            || !ApRunData.TryGetSharedState(runState, out ApRunSharedState shared)
            || shared.HostSettings == null)
        {
            return;
        }
        ArchipelagoClient.UseMultiplayerHostSettings(shared.HostSettings);
    }

    private static CharacterConfig CloneCharacterConfig(CharacterConfig source) => new()
    {
        Name = source.Name,
        OptionName = source.OptionName,
        CharOffset = source.CharOffset,
        OfficialName = source.OfficialName,
        Seed = source.Seed,
        Locked = source.Locked,
        ModNum = source.ModNum,
        Ascension = new HashSet<string>(source.Ascension, StringComparer.Ordinal),
    };

    public static void EndRun()
    {
        IsRealMultiplayerRun = false;
        _experimentalEnabledForRun = false;
        _activeParticipation = null;
        ClaimsInvalidated = false;
        _claimInvalidationNoticeShown = false;
        ApRunData.EndRun();
        ManagedActionRequestScheduler.EndRun();
        ApGrantDispatcher.EndRun();
        ApMirroredRewardDispatcher.EndRun();
        ProgressiveStarterMultiplayer.EndRun();
        AscensionMultiplayer.EndRun();
        AncientMultiplayer.EndRun();
        DeathLinkMultiplayer.EndRun();
    }

    public static bool CanClaimGold(out string reason)
    {
        reason = string.Empty;
        if (!IsRealMultiplayerRun)
            return true;

        if (!IsExperimentalMultiplayerRun)
        {
            reason = "Experimental AP multiplayer is not enabled for this run.";
            return false;
        }

        if (!IsFeatureEnabled(MultiplayerFeature.GoldRewards))
        {
            reason = "Gold rewards are not enabled for this multiplayer profile.";
            return false;
        }

        if (IsLocalApGuest && !HostReceiptCatalogReady)
        {
            reason = "Waiting for a complete host AP receipt catalog.";
            return false;
        }

        if (ClaimsInvalidated)
        {
            reason = "This AP multiplayer run encountered an unrecoverable binding or grant failure.";
            return false;
        }

        if (!RunManager.Instance.NetService.IsConnected)
        {
            reason = "The local game is disconnected from its multiplayer session.";
            return false;
        }

        if (IsSynchronizedCombatActive)
        {
            reason = "Multiplayer gold can only be claimed outside combat.";
            return false;
        }

        return true;
    }

    /// <summary>Shared safety gate for discrete mirrored reward flows.</summary>
    public static bool CanClaimReceivedReward(ApMirroredRewardKind kind, out string reason)
    {
        reason = string.Empty;
        if (!IsRealMultiplayerRun)
            return true;

        MultiplayerFeature feature = kind switch
        {
            ApMirroredRewardKind.Card => MultiplayerFeature.CardRewards,
            ApMirroredRewardKind.Relic => MultiplayerFeature.RelicRewards,
            ApMirroredRewardKind.Potion => MultiplayerFeature.PotionRewards,
            ApMirroredRewardKind.Ancient => MultiplayerFeature.AncientRewardChoices,
            _ => MultiplayerFeature.UnknownReceivedItems,
        };
        if (!IsExperimentalMultiplayerRun)
        {
            reason = "Experimental AP multiplayer is not enabled for this run.";
            return false;
        }
        if (!IsFeatureEnabled(feature))
        {
            reason = $"{feature} is not enabled for this multiplayer profile.";
            return false;
        }
        if (IsLocalApGuest && !HostReceiptCatalogReady)
        {
            reason = "Waiting for a complete host AP receipt catalog.";
            return false;
        }
        if (ClaimsInvalidated)
        {
            reason = "This AP multiplayer run encountered an unrecoverable binding or grant failure.";
            return false;
        }
        if (!RunManager.Instance.NetService.IsConnected)
        {
            reason = "The local game is disconnected from its multiplayer session.";
            return false;
        }
        if (IsSynchronizedCombatActive)
        {
            reason = "Multiplayer AP rewards can only be claimed outside combat.";
            return false;
        }

        return true;
    }

    private static void InvalidateClaims(string reason)
    {
        ClaimsInvalidated = true;
        LogUtility.Error(
            $"Experimental AP multiplayer claims disabled for this run because {reason}. "
                + "A fresh run is required."
        );

        if (_claimInvalidationNoticeShown)
            return;

        _claimInvalidationNoticeShown = true;
        Callable.From(() => NotificationUtility.ShowRawText(
            "AP multiplayer rewards are disabled after an unrecoverable run error. Start a fresh run."
        )).CallDeferred();
    }

    public static void InvalidateRunClaims(string reason) => InvalidateClaims(reason);

}
