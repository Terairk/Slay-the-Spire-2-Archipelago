using Archipelago.MultiClient.Net.DataPackage;
using Archipelago.MultiClient.Net.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using StS2AP.Models;
using STS2RitsuLib.Networking.Sidecar;

namespace StS2AP.Utils;

/// <summary>
/// Relays the fixed host's authoritative AP receipt history to AP Guests. The catalog is only a
/// receipt source: each guest's consumption and stable reward assignments remain in that guest's
/// Net-ID-keyed <see cref="ApPlayerRunState"/>.
/// </summary>
public static class ApReceiptRelay
{
    private const string MessageKey = "host_ap_receipt_catalog_v1";
    private static readonly object CatalogLock = new();
    private static readonly RitsuLibSidecarJsonSerializer<ApReceiptCatalogMessage> Serializer = new();
    private static readonly RitsuLibSidecarMessageDescriptor<ApReceiptCatalogMessage> Descriptor =
        new(
            ModEntry.ModId,
            MessageKey,
            Serializer.Serialize,
            Serializer.Deserialize,
            Required: true
        );

    private static readonly SortedDictionary<int, ItemInfo> HostItems = new();
    private static readonly SortedDictionary<int, ItemInfo> GuestItems = new();
    private static IDisposable? _subscription;
    private static string _hostRoomSeed = string.Empty;
    private static int _hostTeamId;
    private static int _hostSlotId;
    private static int _hostRevision;
    private static string _guestRoomSeed = string.Empty;
    private static int _guestTeamId;
    private static int _guestSlotId;
    private static int _guestRevision;

    public static bool GuestCatalogReady { get; private set; }

    public static void Initialize()
    {
        if (_subscription != null)
            return;
        _subscription = RitsuLibSidecarTypedMessageRegistry.Subscribe(
            Descriptor,
            OnCatalogReceived
        );
    }

    public static void ReplaceHostCatalog(
        string roomSeed,
        int apTeamId,
        int apSlotId,
        IReadOnlyList<ItemInfo> items)
    {
        lock (CatalogLock)
        {
            _hostRoomSeed = roomSeed;
            _hostTeamId = apTeamId;
            _hostSlotId = apSlotId;
            _hostRevision = 1;
            HostItems.Clear();
            for (int index = 0; index < items.Count; index++)
                HostItems[index + 1] = items[index];
        }
    }

    public static void PublishCurrentRunSnapshot()
    {
        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type != NetGameType.Host || !netService.IsConnected)
            return;
        PublishSnapshot(netService);
    }

    public static void PublishLobbySnapshot(StartRunLobby lobby)
    {
        if (lobby.NetService.Type != NetGameType.Host || !lobby.NetService.IsConnected)
            return;
        PublishSnapshot(lobby.NetService);
    }

    public static void PublishLiveReceipt(IndexedItemInfo receipt)
    {
        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type != NetGameType.Host || !netService.IsConnected)
            return;

        ApReceiptCatalogMessage message;
        lock (CatalogLock)
        {
            if (_hostRevision == 0 || string.IsNullOrEmpty(_hostRoomSeed))
                return;
            if (HostItems.TryGetValue(receipt.Index, out ItemInfo? existing)
                && HasSameIdentity(existing, receipt.Item))
            {
                return;
            }

            HostItems[receipt.Index] = receipt.Item;
            _hostRevision++;
            message = CreateMessage(
                isFullSnapshot: false,
                new[] { ToWireItem(receipt.Index, receipt.Item) }
            );
        }
        RitsuLibSidecarTypedMessageRegistry.Broadcast(netService, Descriptor, message);
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
            GuestCatalogReady = false;
        }
    }

    private static void PublishSnapshot(INetGameService netService)
    {
        ApReceiptCatalogMessage message;
        lock (CatalogLock)
        {
            if (_hostRevision == 0 || string.IsNullOrEmpty(_hostRoomSeed))
                return;
            message = CreateMessage(
                isFullSnapshot: true,
                HostItems.Select(pair => ToWireItem(pair.Key, pair.Value))
            );
        }
        RitsuLibSidecarTypedMessageRegistry.Broadcast(netService, Descriptor, message);
    }

    private static ApReceiptCatalogMessage CreateMessage(
        bool isFullSnapshot,
        IEnumerable<ApReceiptWireItem> items) => new()
    {
        RoomSeed = _hostRoomSeed,
        ApTeamId = _hostTeamId,
        ApSlotId = _hostSlotId,
        Revision = _hostRevision,
        IsFullSnapshot = isFullSnapshot,
        HostSettings = MultiplayerSupport.GetHostSettingsForReceiptRelay(),
        Items = items.ToList(),
    };

    private static ApReceiptWireItem ToWireItem(int index, ItemInfo item) => new()
    {
        Index = index,
        SerializedItem = item.ToSerializable().ToJson(false),
    };

    private static void OnCatalogReceived(
        RitsuLibSidecarTypedDispatchContext<ApReceiptCatalogMessage> context)
    {
        INetGameService netService = RunManager.Instance.NetService;
        if (!BetaMainCompatibility.TryGetHostNetId(netService, out ulong hostNetId)
            || context.SenderNetId != hostNetId)
        {
            LogUtility.Warn("Ignored AP receipt catalog from a non-host peer.");
            return;
        }

        ApReceiptCatalogMessage message = context.Message;
        if (message.SchemaVersion != 1
            || message.Revision <= 0
            || string.IsNullOrEmpty(message.RoomSeed))
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
                    throw new InvalidOperationException("Receipt wire item had no stable index or payload.");
                decoded.Add((wire.Index, FromWireItem(wire.SerializedItem)));
            }
        }
        catch (Exception ex)
        {
            LogUtility.Error($"Could not decode the host AP receipt catalog: {ex}");
            return;
        }

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
            }
            else if (!GuestCatalogReady
                || !sameIdentity
                || message.Revision != _guestRevision + 1)
            {
                LogUtility.Warn("Ignored an out-of-order AP receipt catalog delta.");
                return;
            }

            foreach ((int index, ItemInfo item) in decoded)
                GuestItems[index] = item;
            _guestRoomSeed = message.RoomSeed;
            _guestTeamId = message.ApTeamId;
            _guestSlotId = message.ApSlotId;
            _guestRevision = message.Revision;
            GuestCatalogReady = message.IsFullSnapshot || GuestCatalogReady;
        }
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
            new WireItemInfoResolver(saved),
            saved.Player
        );
    }

    private static bool HasSameIdentity(ItemInfo left, ItemInfo right) =>
        left.ItemId == right.ItemId
        && left.LocationId == right.LocationId
        && left.Player.Slot == right.Player.Slot
        && left.Flags == right.Flags;

    private sealed class WireItemInfoResolver(SerializableItemInfo item) : IItemInfoResolver
    {
        public string GetItemName(long itemId, string game) => item.ItemName;
        public string GetLocationName(long locationId, string game) => item.LocationName;
        public long GetLocationId(string locationName, string game) => item.LocationId;
    }
}
