namespace StS2AP.Multiplayer.Messages;

/// <summary>
/// Requests that the native multiplayer host materialize one DeathLink received by an own-slot
/// client. This message is only an external-event intent; the host publishes the replicated
/// damage recipe separately after validating the sender's frozen run settings.
/// </summary>
public sealed class DeathLinkDamageRequestMessage
{
    public int SchemaVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid EventId { get; set; }
    public ulong OwnerNetId { get; set; }
    public int DamagePercent { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Cause { get; set; }
}
