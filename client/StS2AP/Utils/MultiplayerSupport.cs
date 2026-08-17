using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
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
    private static bool _experimentalEnabledForRun;
    private static bool _claimInvalidationNoticeShown;
    private static string? _deferredSessionKey;

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

    /// <summary>
    /// Keeps deferred items across reconnects to the same slot, but never leaks them into a
    /// different AP slot or multiworld selected in the same game process.
    /// </summary>
    public static void OnApSessionConnected()
    {
        string sessionKey = $"{ArchipelagoClient.Seed}\n{ArchipelagoClient.PlayerName}";
        if (_deferredSessionKey != null && _deferredSessionKey != sessionKey)
        {
            LogUtility.Info(
                $"Discarding {DeferredItems.Count} deferred multiplayer item(s) from the previous AP session"
            );
            DeferredItems.Clear();
        }

        _deferredSessionKey = sessionKey;
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
            || runLobby.ConnectedPlayerIds.Count != runState.Players.Count)
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
