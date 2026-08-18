using Archipelago.MultiClient.Net.Models;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Extensions;
using StS2AP.Models;
using StS2AP.UI;
using static StS2AP.Data.ItemTable;

namespace StS2AP.Utils;

/// <summary>
/// Owns the deliberately small experimental multiplayer profile. Singleplayer remains
/// unrestricted; real multiplayer fails closed unless a capability is listed in
/// <see cref="EnabledExperimentalFeatures"/>.
/// </summary>
public static class MultiplayerSupport
{
    private static readonly HashSet<MultiplayerFeature> EnabledExperimentalFeatures = new()
    {
        MultiplayerFeature.CharacterUnlocks,
        MultiplayerFeature.PressStartCheck,
        MultiplayerFeature.GoldRewards,
    };

    private static readonly Dictionary<int, IndexedItemInfo> DeferredItems = new();

    private static RunLobby? _observedRunLobby;
    private static NCharacterSelectScreen? _observedStartLobbyScreen;
    private static bool _experimentalEnabledForRun;
    private static bool _claimInvalidationNoticeShown;
    private static bool _apHistoryPrepared;
    private static string? _deferredSessionKey;
    private static ApSessionIdentity? _preparedSessionIdentity;

    private readonly record struct ApSessionIdentity(string RoomSeed, int ApTeamId, int ApSlotId)
    {
        public override string ToString() =>
            $"{RoomSeed}/ap-team-{ApTeamId}/ap-slot-{ApSlotId}";
    }

    public static ApPlayDestination PendingDestination { get; private set; } =
        ApPlayDestination.Singleplayer;

    public static bool IsRealMultiplayerRun { get; private set; }

    public static bool IsExperimentalMultiplayerRun =>
        IsRealMultiplayerRun && _experimentalEnabledForRun;

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

        bool profileEnabled = IsRealMultiplayerRun
            ? _experimentalEnabledForRun
            : ExperimentalSettingEnabled;
        return profileEnabled && EnabledExperimentalFeatures.Contains(feature);
    }

    public static MultiplayerFeature GetFeatureForItem(IndexedItemInfo indexedItem)
    {
        var item = indexedItem.Item;
        if (item.ItemId < 10000)
            return MultiplayerFeature.CombatEffects;

        return item.GetCharacterSpecificItemID() switch
        {
            APItem.Unlock => MultiplayerFeature.CharacterUnlocks,
            APItem.OneGold or APItem.FiveGold or APItem.CombatGold or APItem.EliteGold
                or APItem.BossGold => MultiplayerFeature.GoldRewards,
            APItem.CardReward or APItem.RareCardReward => MultiplayerFeature.CardRewards,
            APItem.Relic => MultiplayerFeature.RelicRewards,
            APItem.Potion => MultiplayerFeature.PotionRewards,
            APItem.ProgressiveRest or APItem.ProgressiveSmith => MultiplayerFeature.RestSites,
            APItem.ProgressiveAncient => MultiplayerFeature.Ancients,
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
        for (int index = 0; index < receivedItems.Count; index++)
        {
            ItemInfo item = receivedItems[index];
            var indexedItem = new IndexedItemInfo(item, index + 1);
            MultiplayerFeature feature = GetFeatureForItem(indexedItem);
            if (feature == MultiplayerFeature.CharacterUnlocks)
            {
                GameUtility.UnlockCharacter(item);
            }
            else if (feature != MultiplayerFeature.GoldRewards)
            {
                DeferItem(indexedItem);
            }
        }

        ApGrantDispatcher.RebuildGoldBank(receivedItems);
        _preparedSessionIdentity = identity;
        _deferredSessionKey = sessionKey;
        _apHistoryPrepared = true;
        RefreshObservedStartLobby();
        LogUtility.Info(
            $"Prepared AP multiplayer session {identity}: receipts={receivedItems.Count}, "
                + $"deferred={DeferredItems.Count}"
        );
        return true;
    }

    public static void OnApDisconnected()
    {
        _apHistoryPrepared = false;
        // AP socket termination may be raised off the Godot main thread.
        Callable.From(RefreshObservedStartLobby).CallDeferred();
    }

    public static bool CanEnterMultiplayerLobby(out string reason)
    {
        if (!ArchipelagoClient.IsConnected)
        {
            reason = "Connect to Archipelago before opening the multiplayer lobby.";
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

    public static bool CanEmbark(CharacterModel character, out string reason)
    {
        if (!CanEnterMultiplayerLobby(out reason))
            return false;

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

        return CanEmbark(localPlayer.Character, out reason);
    }

    public static void ObserveStartLobby(NCharacterSelectScreen screen)
    {
        if (PendingDestination != ApPlayDestination.Multiplayer)
            return;

        _observedStartLobbyScreen = screen;
        RefreshObservedStartLobby();
    }

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
            NConfirmButton embarkButton = screen.GetNode<NConfirmButton>("ConfirmButton");
            if (!CanEnterMultiplayerLobby(out _))
            {
                if (screen.Lobby.LocalPlayer.isReady)
                {
                    // Use the native beta UI transition so auto-unready restores character
                    // buttons and the waiting panel as well as changing the lobby flag.
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
                embarkButton.Disable();
                return;
            }

            if (screen.Lobby.LocalPlayer.isReady)
                return;

            if (CanEmbark(screen.Lobby.LocalPlayer.character, out _))
                embarkButton.Enable();
            else
                embarkButton.Disable();
        }
        catch (Exception ex)
        {
            LogUtility.Warn($"Could not refresh AP multiplayer lobby readiness: {ex.Message}");
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

        _observedRunLobby = RunManager.Instance.RunLobby;
        if (_observedRunLobby != null)
        {
            _observedRunLobby.RemotePlayerDisconnected += OnRemotePlayerDisconnected;
            _observedRunLobby.LocalPlayerDisconnected += OnLocalPlayerDisconnected;
        }

        CombatManager.Instance.CombatEnded += OnCombatEnded;

        LogUtility.Info(
            $"Experimental AP multiplayer launched: enabled={_experimentalEnabledForRun}, "
                + $"netType={RunManager.Instance.NetService.Type}, localNetId={localPlayer.NetId}, "
                + $"players=[{string.Join(",", runState.Players.Select(p => p.NetId))}]"
        );
        return localPlayer;
    }

    public static void EndRun()
    {
        if (_observedRunLobby != null)
        {
            _observedRunLobby.RemotePlayerDisconnected -= OnRemotePlayerDisconnected;
            _observedRunLobby.LocalPlayerDisconnected -= OnLocalPlayerDisconnected;
            _observedRunLobby = null;
        }

        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        IsRealMultiplayerRun = false;
        _experimentalEnabledForRun = false;
        ClaimsInvalidated = false;
        _claimInvalidationNoticeShown = false;
        ApGrantDispatcher.EndRun();
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

        if (ClaimsInvalidated)
        {
            reason = "A multiplayer peer disconnected. Start a fresh run to claim AP rewards.";
            return false;
        }

        if (CombatManager.Instance.IsInProgress)
        {
            reason = "Multiplayer gold can only be claimed outside combat.";
            return false;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        var runLobby = RunManager.Instance.RunLobby;
        if (runState == null || runLobby == null
            || runLobby.Players.Count != runState.Players.Count)
        {
            reason = "All multiplayer peers must be connected to claim AP rewards.";
            return false;
        }

        return true;
    }

    private static void OnRemotePlayerDisconnected(ulong playerId) =>
        InvalidateClaims($"remote player {playerId} disconnected");

    private static void OnLocalPlayerDisconnected() =>
        InvalidateClaims("the local game disconnected from its multiplayer host");

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
            "AP multiplayer rewards are disabled after a peer disconnect. Start a fresh run."
        )).CallDeferred();
    }

    public static void InvalidateRunClaims(string reason) => InvalidateClaims(reason);

    private static void OnCombatEnded(MegaCrit.Sts2.Core.Rooms.CombatRoom _)
    {
        if (IsExperimentalMultiplayerRun && ArchipelagoRewardUI.IsOpen)
            Callable.From(ArchipelagoRewardUI.ShowRewards).CallDeferred();
    }
}
