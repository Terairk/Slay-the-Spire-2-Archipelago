namespace StS2AP.Persistence;

/// <summary>
/// One player's canonical contribution, keyed by the MegaCrit Net ID retained across rejoin.
/// </summary>
public sealed class ApPlayerRunState
{
    public int SchemaVersion { get; set; } = 6;
    public ApParticipationKind Participation { get; set; } = ApParticipationKind.VanillaGuest;
    public string? ApRoomSeed { get; set; }
    public int? ApTeamId { get; set; }
    public int? ApSlotId { get; set; }
    public ArchipelagoSettings? SlotSettings { get; set; }
    public Dictionary<long, List<int>> InitialRelicReceiptIndexesByCharacter { get; set; } = new();
    public Dictionary<long, int> InitialProgressiveAncientsByCharacter { get; set; } = new();
    public bool ReceiptSourceReady { get; set; }
    public ApRunProgressState Progress { get; set; } = new();
    public long ProgressRevision { get; set; }
    public ApProgressiveStarterPlayerState ProgressiveStarters { get; set; } = new();
}
