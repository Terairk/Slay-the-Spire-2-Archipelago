namespace StS2AP.Multiplayer.Messages;

/// <summary>A complete host receipt catalog or one ordered append/update delta.</summary>
public sealed class ApReceiptCatalogMessage
{
    public int SchemaVersion { get; set; } = 2;
    public string RoomSeed { get; set; } = string.Empty;
    public int ApTeamId { get; set; }
    public int ApSlotId { get; set; }
    public int BaseRevision { get; set; }
    public int Revision { get; set; }
    public bool IsFullSnapshot { get; set; }
    public ArchipelagoSettings? HostSettings { get; set; }
    public List<ApReceiptWireItem> Items { get; set; } = new();
}
