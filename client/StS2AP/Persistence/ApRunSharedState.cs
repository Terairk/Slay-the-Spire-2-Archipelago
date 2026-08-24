namespace StS2AP.Persistence;

/// <summary>Host-authored facts embedded in the canonical MegaCrit run snapshot.</summary>
public sealed class ApRunSharedState
{
    public int SchemaVersion { get; set; } = 6;
    public Guid RunId { get; set; }
    public ArchipelagoSettings? HostSettings { get; set; }
    public SharedSlotCheckScope SharedSlotCheckScope { get; set; } =
        SharedSlotCheckScope.HostCharacterOnly;
}
