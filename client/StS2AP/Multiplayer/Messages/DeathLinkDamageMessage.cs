namespace StS2AP.Multiplayer.Messages;

/// <summary>
/// Host-authored recipe for applying one incoming DeathLink at a deterministic safe boundary on
/// every replica. Own-slot events target their owner; the host slot also targets its AP Guests.
/// </summary>
public sealed class DeathLinkDamageMessage
{
    public int SchemaVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid EventId { get; set; }
    public ulong SlotOwnerNetId { get; set; }
    public int DamagePercent { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Cause { get; set; }
    public List<ulong> TargetNetIds { get; set; } = new();
}
