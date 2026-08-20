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

    // maybe the card state can be its own data structure, maybe thats too much though
    public bool IsRareCardReward { get; set; }

    public int? CardRewardActIndex { get; set; }

    // CONFIRM: future reroll support as I don't think driftwood works for AP card rewards
    public bool CardCanReroll { get; set; }

    // CONFIRM: the datatype of serialized models, why string?
    public List<string> SerializedModels { get; set; } = new();

    public ApGrantId GrantId => new(ApSlotId, ReceivedItemIndex);
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
