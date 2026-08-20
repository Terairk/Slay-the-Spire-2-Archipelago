namespace StS2AP.Models;

/// <summary>
/// Stable identity for one discrete Archipelago receipt. Received item indexes are only
/// unique inside an AP slot, so the slot must remain part of every durable assignment key.
/// </summary>
public readonly record struct ApGrantId(int ApSlotId, int ReceivedItemIndex)
{
    public override string ToString() => $"{ApSlotId}:{ReceivedItemIndex}";
}

public enum ApGrantState
{
    Claimable,
    Applied,
    Blocked,
}

/// <summary>Native mirrored reward shapes currently supported by the AP reward menu.
/// CONFIRM: Gold is handled separately I think
/// </summary>
public enum ApMirroredRewardKind
{
    Card,
    Relic,
    Potion,
    Ancient,
    Unavailable,
}

/// <summary>
/// Concrete owner-authored specification sent through RitsuLib Sidecar before a native
/// reward flow starts. Model payloads are MegaCrit save-schema JSON strings so mutable
/// card/relic/potion state can be reconstructed rather than reduced to a model ID.
/// </summary>
public sealed class ApMirroredRewardSpec
{
    public int SchemaVersion { get; set; } = 1;

    public int ApSlotId { get; set; }
    public int ReceivedItemIndex { get; set; }

    /// <summary>
    /// Run-scoped owner binding. MegaCrit preserves this Net ID across active-run rejoin; the
    /// durable receipt identity remains <see cref="GrantId"/> rather than this transport identity.
    /// </summary>
    public ulong OwnerNetId { get; set; }

    public ApMirroredRewardKind Kind { get; set; }

    /// <summary>Display text used only when no concrete native reward type exists.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>AP sender and location retained as presentation metadata on native rewards.</summary>
    public string SenderName { get; set; } = string.Empty;
    public string FoundLocation { get; set; } = string.Empty;

    /// <summary>Owner-facing reason that this receipt cannot currently be claimed.</summary>
    public string UnavailableReason { get; set; } = string.Empty;

    // maybe the card state can be its own data structure, maybe thats too much though
    public bool IsRareCardReward { get; set; }

    public int? CardRewardActIndex { get; set; }

    // CONFIRM: future reroll support as I don't think driftwood works for AP card rewards
    public bool CardCanReroll { get; set; }

    // CONFIRM: the datatype of serialized models, why string?
    public List<string> SerializedModels { get; set; } = new();

    public ApGrantId GrantId => new(ApSlotId, ReceivedItemIndex);
}

/// <summary>Serializable form of one condensed AP gold row in a native reward menu.</summary>
public sealed class ApMenuGoldSpec
{
    public int SourceAmount { get; set; }
    public int GrantedAmount { get; set; }
    public int RedeemedRawAfter { get; set; }

    public ApGoldClaim ToClaim() => new(SourceAmount, GrantedAmount, RedeemedRawAfter);
}

/// <summary>
/// One immutable AP reward-menu snapshot. Every multiplayer replica builds the same ordered
/// native RewardsSet before MegaCrit begins synchronizing selections for its owner.
/// </summary>
public sealed class ApRewardMenuSpec
{
    public int SchemaVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid MenuId { get; set; } = Guid.NewGuid();
    public int ApSlotId { get; set; }
    public ulong OwnerNetId { get; set; }
    public ApMenuGoldSpec? Gold { get; set; }
    public List<ApMirroredRewardSpec> Rewards { get; set; } = new();
}

/// <summary>Human-readable snapshot used by the AP developer-console providers.</summary>
public sealed record ApGrantSnapshot(
    ApGrantId GrantId,
    string ItemName,
    ulong OwnerNetId,
    ApMirroredRewardKind Kind,
    ApGrantState State,
    string Assignment,
    string? BlockedReason,
    string? LastAttempt
);
