using Archipelago.MultiClient.Net.DataPackage;
using Archipelago.MultiClient.Net.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;
using StS2AP.Models;
using STS2RitsuLib.Networking.Sidecar;

namespace StS2AP.Utils;

/// <summary>
/// Relays the fixed host's authoritative AP receipt history to AP Guests. Full snapshots are used
/// only for binding/recovery; normal receipts are ordered deltas. Each guest's consumption and
/// stable assignments remain in that guest's Net-ID-keyed <see cref="ApPlayerRunState"/>.
/// </summary>
public static class ApReceiptRelay
{
    private const string CatalogMessageKey = "host_ap_receipt_catalog_v2";
    private const string RequestMessageKey = "host_ap_receipt_catalog_request_v1";
    private static readonly object CatalogLock = new();
    private static readonly RitsuLibSidecarJsonSerializer<ApReceiptCatalogMessage>
        CatalogSerializer = new();
    private static readonly RitsuLibSidecarMessageDescriptor<ApReceiptCatalogMessage>
        CatalogDescriptor = new(
            ModEntry.ModId,
            CatalogMessageKey,
            CatalogSerializer.Serialize,
            CatalogSerializer.Deserialize,
            Required: true
        );
    private static readonly RitsuLibSidecarJsonSerializer<ApReceiptCatalogRequestMessage>
        RequestSerializer = new();
    private static readonly RitsuLibSidecarMessageDescriptor<ApReceiptCatalogRequestMessage>
        RequestDescriptor = new(
            ModEntry.ModId,
            RequestMessageKey,
            RequestSerializer.Serialize,
            RequestSerializer.Deserialize,
            Required: true
        );

    private static readonly SortedDictionary<int, ItemInfo> HostItems = new();
    private static readonly SortedDictionary<int, ItemInfo> GuestItems = new();
    private static IDisposable? _catalogSubscription;
    private static IDisposable? _requestSubscription;
    private static IDisposable? _handshakeSubscription;
    private static string _hostRoomSeed = string.Empty;
    private static int _hostTeamId;
    private static int _hostSlotId;
    private static int _hostRevision;
    private static string _guestRoomSeed = string.Empty;
    private static int _guestTeamId;
    private static int _guestSlotId;
    private static int _guestRevision;
    private static ArchipelagoSettings? _guestHostSettings;
    private static bool _snapshotRequestOutstanding;
    private static bool _guestHasCompleteCatalog;
    private static volatile bool _guestCatalogReady;

    /// <summary>True only after the current catalog has been installed into the local AP view.</summary>
    public static bool GuestCatalogReady => _guestCatalogReady;

    public static void Initialize()
    {
        if (_catalogSubscription != null)
            return;
        _catalogSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(
            CatalogDescriptor,
            OnCatalogReceived
        );
        _requestSubscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(
            RequestDescriptor,
            OnSnapshotRequested
        );
        _handshakeSubscription = RitsuLibSidecarEvents.OnHandshakeCompleted(evt =>
        {
            INetGameService? netService = RunManager.Instance.NetService;
            if (netService != null
                && netService.Type == NetGameType.Host
                && netService.IsConnected)
            {
                // This is only a proactive delivery of the host-owned catalogue. A guest can
                // request the same snapshot later if this handshake-time send is missed.
                PublishSnapshot(netService, evt.PeerNetId);
            }
        });
    }

    public static void ReplaceHostCatalog(
        string roomSeed,
        int apTeamId,
        int apSlotId,
        IReadOnlyList<ItemInfo> items)
    {
        lock (CatalogLock)
        {
            bool sameIdentity = string.Equals(_hostRoomSeed, roomSeed, StringComparison.Ordinal)
                && _hostTeamId == apTeamId
                && _hostSlotId == apSlotId;
            _hostRoomSeed = roomSeed;
            _hostTeamId = apTeamId;
            _hostSlotId = apSlotId;
            _hostRevision = sameIdentity ? Math.Max(1, _hostRevision + 1) : 1;
            HostItems.Clear();
            for (int index = 0; index < items.Count; index++)
                HostItems[index + 1] = items[index];
        }
    }

    public static void PublishCurrentRunSnapshot()
    {
        // AP login is deliberately completed before the native multiplayer lobby exists.
        // RunManager therefore has no NetService yet, and there is nobody to receive a
        // snapshot until the native host/client connection has been established.
        INetGameService? netService = RunManager.Instance.NetService;
        if (netService != null
            && netService.Type == NetGameType.Host
            && netService.IsConnected)
        {
            PublishSnapshot(netService, targetNetId: null);
        }
    }

    /// <summary>Sends one new/changed receipt without resending the full host history.</summary>
    public static void PublishLiveReceipt(IndexedItemInfo receipt)
    {
        INetGameService? netService = RunManager.Instance.NetService;
        ApReceiptCatalogMessage? message = null;
        lock (CatalogLock)
        {
            if (_hostRevision == 0 || string.IsNullOrEmpty(_hostRoomSeed))
                return;
            if (HostItems.TryGetValue(receipt.Index, out ItemInfo? existing)
                && HasSameIdentity(existing, receipt.Item))
            {
                return;
            }

            int baseRevision = _hostRevision;
            HostItems[receipt.Index] = receipt.Item;
            _hostRevision++;

            // Initial AP history is replayed before the native lobby exists. Always advance the
            // local catalog so a later full-snapshot request sees that history; only the network
            // delta itself depends on an active fixed-host transport.
            if (netService != null
                && netService.Type == NetGameType.Host
                && netService.IsConnected)
            {
                message = CreateCatalogMessage(
                    isFullSnapshot: false,
                    baseRevision,
                    new[] { ToWireItem(receipt.Index, receipt.Item) }
                );
            }
        }
        if (message != null && netService != null)
            RitsuLibSidecarTypedMessageRegistry.Broadcast(netService, CatalogDescriptor, message);
    }

    /// <summary>Sends one targeted full-snapshot request to the fixed STS host.</summary>
    public static void RequestSnapshot(INetGameService netService, bool force = false)
    {
        if (netService.Type == NetGameType.Host
            || !netService.IsConnected
            || !MultiplayerSupport.IsLocalApGuest)
        {
            return;
        }

        ApReceiptCatalogRequestMessage request;
        lock (CatalogLock)
        {
            if (_snapshotRequestOutstanding && !force)
                return;
            request = new ApReceiptCatalogRequestMessage
            {
                KnownRoomSeed = _guestRoomSeed,
                KnownRevision = _guestRevision,
            };
            _snapshotRequestOutstanding = true;
        }

        if (!RitsuLibSidecarTypedMessageRegistry.SendToHost(
                netService,
                RequestDescriptor,
                request
            ))
        {
            lock (CatalogLock)
                _snapshotRequestOutstanding = false;
        }
    }

    public static IReadOnlyList<ItemInfo> GetGuestItems()
    {
        lock (CatalogLock)
            return GuestItems.Values.ToArray();
    }

    public static bool TryGetHostReceipt(int index, out ItemInfo item)
    {
        lock (CatalogLock)
            return HostItems.TryGetValue(index, out item!);
    }

    public static void ResetGuestCatalog()
    {
        lock (CatalogLock)
        {
            GuestItems.Clear();
            _guestRoomSeed = string.Empty;
            _guestTeamId = 0;
            _guestSlotId = 0;
            _guestRevision = 0;
            _guestHostSettings = null;
            _snapshotRequestOutstanding = false;
            _guestHasCompleteCatalog = false;
            _guestCatalogReady = false;
        }
    }

    private static void PublishSnapshot(INetGameService netService, ulong? targetNetId)
    {
        ApReceiptCatalogMessage message;
        lock (CatalogLock)
        {
            if (_hostRevision == 0 || string.IsNullOrEmpty(_hostRoomSeed))
                return;
            message = CreateCatalogMessage(
                isFullSnapshot: true,
                baseRevision: 0,
                HostItems.Select(pair => ToWireItem(pair.Key, pair.Value))
            );
        }

        if (targetNetId.HasValue)
        {
            RitsuLibSidecarTypedMessageRegistry.SendToPeer(
                netService,
                targetNetId.Value,
                CatalogDescriptor,
                message
            );
        }
        else
        {
            RitsuLibSidecarTypedMessageRegistry.Broadcast(
                netService,
                CatalogDescriptor,
                message
            );
        }
    }

    private static ApReceiptCatalogMessage CreateCatalogMessage(
        bool isFullSnapshot,
        int baseRevision,
        IEnumerable<ApReceiptWireItem> items) => new()
    {
        RoomSeed = _hostRoomSeed,
        ApTeamId = _hostTeamId,
        ApSlotId = _hostSlotId,
        BaseRevision = baseRevision,
        Revision = _hostRevision,
        IsFullSnapshot = isFullSnapshot,
        HostSettings = isFullSnapshot
            ? MultiplayerSupport.GetHostSettingsForReceiptRelay()
            : null,
        Items = items.ToList(),
    };

    private static ApReceiptWireItem ToWireItem(int index, ItemInfo item) => new()
    {
        Index = index,
        // AP Guests deliberately have no AP SDK session, so every wire item must carry its
        // complete names/player context rather than the SDK's session-dependent compact form.
        SerializedItem = item.ToSerializable().ToJson(true),
    };

    private static void OnSnapshotRequested(
        RitsuLibSidecarTypedDispatchContext<ApReceiptCatalogRequestMessage> context)
    {
        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type != NetGameType.Host || context.Message.SchemaVersion != 1)
            return;

        LogUtility.Debug(
            $"Sending targeted AP receipt snapshot to {context.SenderNetId}; "
                + $"peerRevision={context.Message.KnownRevision}"
        );
        PublishSnapshot(netService, context.SenderNetId);
    }

    private static void OnCatalogReceived(
        RitsuLibSidecarTypedDispatchContext<ApReceiptCatalogMessage> context)
    {
        if (!MultiplayerSupport.IsLocalApGuest)
            return;

        INetGameService netService = RunManager.Instance.NetService;
        if (!BetaMainCompatibility.TryGetHostNetId(netService, out ulong hostNetId)
            || context.SenderNetId != hostNetId)
        {
            LogUtility.Warn("Ignored AP receipt catalog from a non-host peer.");
            return;
        }

        ApReceiptCatalogMessage message = context.Message;
        if (message.SchemaVersion != 2
            || message.Revision <= 0
            || string.IsNullOrEmpty(message.RoomSeed)
            || message.IsFullSnapshot && (message.BaseRevision != 0 || message.HostSettings == null)
            || !message.IsFullSnapshot && message.HostSettings != null)
        {
            LogUtility.Warn("Ignored malformed AP receipt catalog.");
            return;
        }

        var decoded = new List<(int Index, ItemInfo Item)>();
        try
        {
            foreach (ApReceiptWireItem wire in message.Items)
            {
                if (wire.Index <= 0 || string.IsNullOrEmpty(wire.SerializedItem))
                    throw new InvalidOperationException(
                        "Receipt wire item had no stable index or payload."
                    );
                decoded.Add((wire.Index, FromWireItem(wire.SerializedItem)));
            }
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Could not decode the host AP receipt catalog: {ex}");
            InvalidateAndRequestRecovery(netService, "receipt payload could not be decoded");
            return;
        }

        bool needsRecovery = false;
        int installRevision = 0;
        lock (CatalogLock)
        {
            bool sameIdentity = string.Equals(
                    _guestRoomSeed,
                    message.RoomSeed,
                    StringComparison.Ordinal
                )
                && _guestTeamId == message.ApTeamId
                && _guestSlotId == message.ApSlotId;

            if (message.IsFullSnapshot)
            {
                if (sameIdentity && message.Revision < _guestRevision)
                    return;
                GuestItems.Clear();
                _guestHostSettings = message.HostSettings;
                _guestHasCompleteCatalog = true;
                _guestCatalogReady = false;
                _snapshotRequestOutstanding = false;
            }
            else if (!_guestHasCompleteCatalog
                || !sameIdentity
                || message.BaseRevision != _guestRevision
                || message.Revision != message.BaseRevision + 1)
            {
                _guestHasCompleteCatalog = false;
                _guestCatalogReady = false;
                _snapshotRequestOutstanding = false;
                needsRecovery = true;
            }

            if (!needsRecovery)
            {
                foreach ((int index, ItemInfo item) in decoded)
                    GuestItems[index] = item;
                _guestRoomSeed = message.RoomSeed;
                _guestTeamId = message.ApTeamId;
                _guestSlotId = message.ApSlotId;
                _guestRevision = message.Revision;
                installRevision = _guestRevision;
            }
        }

        if (needsRecovery)
        {
            InvalidateAndRequestRecovery(netService, "receipt catalog revision gap");
            return;
        }

        ScheduleGuestCatalogInstall(installRevision);
    }

    private static void ScheduleGuestCatalogInstall(int expectedRevision)
    {
        bool posted = RitsuLibSidecarGodotMainLoopScheduling.TryPostToMainLoop(() =>
        {
            string roomSeed;
            int teamId;
            int slotId;
            ArchipelagoSettings hostSettings;
            ItemInfo[] items;
            lock (CatalogLock)
            {
                if (_guestRevision != expectedRevision || _guestHostSettings == null)
                    return;
                roomSeed = _guestRoomSeed;
                teamId = _guestTeamId;
                slotId = _guestSlotId;
                hostSettings = _guestHostSettings;
                items = GuestItems.Values.ToArray();
            }

            if (!MultiplayerSupport.PrepareApGuestSession(
                    roomSeed,
                    teamId,
                    slotId,
                    hostSettings,
                    items,
                    out string reason
                ))
            {
                lock (CatalogLock)
                    _guestCatalogReady = false;
                LogUtility.Error($"Could not install AP Guest receipt catalog: {reason}");
                MultiplayerSupport.NotifyApGuestCatalogInvalidated();
                return;
            }

            lock (CatalogLock)
            {
                if (_guestRevision != expectedRevision)
                    return;
                _guestCatalogReady = true;
            }
            MultiplayerSupport.NotifyApGuestCatalogInstalled();
            LogUtility.Info(
                $"Installed AP Guest receipt catalog revision {expectedRevision}: "
                    + $"receipts={items.Length}"
            );
        });
        if (!posted)
        {
            lock (CatalogLock)
                _guestCatalogReady = false;
            LogUtility.Error("Could not schedule AP Guest receipt catalog installation.");
            MultiplayerSupport.NotifyApGuestCatalogInvalidated();
        }
    }

    private static void InvalidateAndRequestRecovery(
        INetGameService netService,
        string reason)
    {
        lock (CatalogLock)
        {
            _guestHasCompleteCatalog = false;
            _guestCatalogReady = false;
            _snapshotRequestOutstanding = false;
        }
        LogUtility.Warn($"AP Guest catalog invalidated: {reason}; requesting full snapshot");
        MultiplayerSupport.NotifyApGuestCatalogInvalidated();
        RequestSnapshot(netService, force: true);
    }

    private static ItemInfo FromWireItem(string json)
    {
        SerializableItemInfo saved = SerializableItemInfo.FromJson(
            json,
            ArchipelagoClient.IsConnected ? ArchipelagoClient.Session : null!
        );
        var networkItem = new NetworkItem
        {
            Item = saved.ItemId,
            Location = saved.LocationId,
            Player = saved.PlayerSlot,
            Flags = saved.Flags,
        };
        return new ItemInfo(
            networkItem,
            saved.ItemGame,
            saved.LocationGame,
            CreateWireItemInfoResolver(saved),
            saved.Player
        );
    }

    private static IItemInfoResolver CreateWireItemInfoResolver(SerializableItemInfo item)
    {
        IItemInfoResolver resolver =
            DispatchProxy.Create<IItemInfoResolver, WireItemInfoResolverProxy>();
        ((WireItemInfoResolverProxy)(object)resolver).Item = item;
        return resolver;
    }

    private static bool HasSameIdentity(ItemInfo left, ItemInfo right) =>
        left.ItemId == right.ItemId
        && left.LocationId == right.LocationId
        && left.Player.Slot == right.Player.Slot
        && left.Flags == right.Flags;

    /// <summary>
    /// Implements the AP resolver dynamically after mod initialization. Keeping
    /// <see cref="IItemInfoResolver"/> out of this assembly's declared base-type list lets the
    /// beta mod loader enumerate our types before <see cref="ModEntry.Initialize"/> installs the
    /// dependency resolver.
    /// </summary>
    public class WireItemInfoResolverProxy : DispatchProxy
    {
        public object Item { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var item = (SerializableItemInfo)Item;
            return targetMethod?.Name switch
            {
                nameof(IItemInfoResolver.GetItemName) => item.ItemName,
                nameof(IItemInfoResolver.GetLocationName) => item.LocationName,
                nameof(IItemInfoResolver.GetLocationId) => item.LocationId,
                _ => throw new MissingMethodException(
                    $"Unsupported item-info resolver method: {targetMethod?.Name ?? "<null>"}"
                ),
            };
        }
    }
}
