namespace StS2AP.Multiplayer.Messages;

/// <summary>Requests a targeted full catalog after initial join, rejoin, or a revision gap.</summary>
public sealed class ApReceiptCatalogRequestMessage
{
    public int SchemaVersion { get; set; } = 1;
    public string KnownRoomSeed { get; set; } = string.Empty;
    public int KnownRevision { get; set; }
}
