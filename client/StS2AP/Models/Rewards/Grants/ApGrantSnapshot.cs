namespace StS2AP.Models;

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
