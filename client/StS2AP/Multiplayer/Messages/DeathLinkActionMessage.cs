namespace StS2AP.Multiplayer.Messages;

/// <summary>
/// External DeathLink event carried by the native multiplayer action queue. Gameplay values such
/// as the action owner, damage percentage, and targets are derived from synchronized run state.
/// </summary>
public sealed class DeathLinkActionMessage
{
    public int SchemaVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid EventId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Cause { get; set; }
}
