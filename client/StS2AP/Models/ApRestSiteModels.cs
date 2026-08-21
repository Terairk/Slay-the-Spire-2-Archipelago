namespace StS2AP.Models;

/// <summary>Provides a stable manifest key for AP-owned rest-site option implementations.</summary>
public interface IApRestSiteSemanticOption
{
    string SemanticKey { get; }
}

/// <summary>
/// The host-confirmed AP inputs needed to apply one player's campfire transform on every
/// replica. Native, relic, card, and third-party options are deliberately not serialized.
/// </summary>
public sealed class ApRestSiteState
{
    public int SchemaVersion { get; set; } = 1;
    public long CharacterOffset { get; set; }
    public int ProgressiveRestLevel { get; set; }
    public int ProgressiveSmithLevel { get; set; }
    public List<ApCampfireCheckState> CampfireChecks { get; set; } = new();
}

/// <summary>Portable presentation and consumption state for one Campfiresanity location.</summary>
public sealed class ApCampfireCheckState
{
    public int Act { get; set; }
    public int Campfire { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public long LocationId { get; set; }
    public bool IsChecked { get; set; }
    public string Description { get; set; } = string.Empty;
    public string OptionId { get; set; } = "FILLER";
}

/// <summary>
/// One owner's latest AP campfire state. Own-slot clients send theirs to the host; the host
/// persists and relays it. The host creates the same message directly for AP Guests.
/// </summary>
public sealed class ApRestSiteStateMessage
{
    public int SchemaVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public ulong OwnerNetId { get; set; }
    public ApRestSiteState State { get; set; } = new();
}

/// <summary>
/// Location-targeted proof that a peer constructed the same flattened dense option lists.
/// </summary>
public sealed class ApRestSiteManifestMessage
{
    public int SchemaVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public string VisitId { get; set; } = string.Empty;
    public ulong ReporterNetId { get; set; }
    public string? ConstructionFailure { get; set; }
    public List<string> OptionKeys { get; set; } = new();
}
