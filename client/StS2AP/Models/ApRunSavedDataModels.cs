namespace StS2AP.Models;

/// <summary>
/// Host-authored facts embedded in the canonical MegaCrit run snapshot. This is deliberately
/// limited to state every peer needs in order to interpret the same shared run.
/// </summary>
public sealed class ApRunSharedState
{
    /// <summary>Serialized payload schema only; this is not a multiplayer protocol version.</summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>Separates two AP/STSes runs even when their seeds and slot identities match.</summary>
    public Guid RunId { get; set; }

    /// <summary>The host's resolved AP slot/client gameplay settings, frozen at run start.</summary>
    public ArchipelagoSettings? HostSettings { get; set; }

    public SharedSlotCheckScope SharedSlotCheckScope { get; set; } =
        SharedSlotCheckScope.HostCharacterOnly;
}

/// <summary>
/// One player's contribution to the canonical run snapshot. RitsuLib keys this value by the
/// MegaCrit Net ID, which is stable across disconnect/rejoin within this run; AP identity fields
/// are absent for guests. Do not remap this record by AP identity when a peer reconnects.
/// </summary>
public sealed class ApPlayerRunState
{
    /// <summary>Serialized payload schema only; this is not a multiplayer protocol version.</summary>
    public int SchemaVersion { get; set; } = 2;

    // i wonder if we want to default this to ApGuest
    public ApParticipationKind Participation { get; set; } = ApParticipationKind.VanillaGuest;

    // I wonder if all 3 are needed or not. 
    public string? ApRoomSeed { get; set; }
    public int? ApTeamId { get; set; }
    public int? ApSlotId { get; set; }

    /// <summary>
    /// Lobby readiness evidence contributed by this player. This means either the independent
    /// slot's SDK history or the fixed host's AP Guest receipt catalog has been reconstructed.
    /// Durable consumption and assignments live in <see cref="Progress"/> instead.
    /// </summary>
    public bool ReceiptSourceReady { get; set; }

    /// <summary>
    /// Canonical run-scoped AP progress for this Net ID. Only the fixed host persists it;
    /// clients receive an in-memory copy in the native run/rejoin snapshot.
    /// </summary>
    public ApRunProgressState Progress { get; set; } = new();

    /// <summary>Monotonic live-update revision used to reject stale client snapshots.</summary>
    public long ProgressRevision { get; set; }
}

/// <summary>
/// Establishes the owner's complete progress view when no prior local publication exists. Normal
/// mutations use <see cref="ApProgressDeltaMessage"/> so large saved assignments are not resent.
/// </summary>
public sealed class ApProgressSnapshotMessage
{
    public Guid RunId { get; set; }
    public ulong OwnerNetId { get; set; }
    public long Revision { get; set; }
    public ApRunProgressState Progress { get; set; } = new();
}

/// <summary>
/// Carries one ordered change to the host-owned per-player progress. The base revision makes a
/// missing/out-of-order mutation detectable instead of applying a patch to the wrong snapshot.
/// </summary>
public sealed class ApProgressDeltaMessage
{
    public Guid RunId { get; set; }
    public ulong OwnerNetId { get; set; }
    public long BaseRevision { get; set; }
    public long Revision { get; set; }
    public ApProgressDelta Delta { get; set; } = new();
}

// CONFIRM: what's this used for? Do we not need a slot asw or something
public sealed class ApReceiptWireItem
{
    public int Index { get; set; }
    public string SerializedItem { get; set; } = string.Empty;
}

// CONFIRM: purpose of this 
public sealed class ApReceiptCatalogMessage
{
    public int SchemaVersion { get; set; } = 1;
    public string RoomSeed { get; set; } = string.Empty;
    public int ApTeamId { get; set; }
    public int ApSlotId { get; set; }
    public int Revision { get; set; }
    public bool IsFullSnapshot { get; set; }
    public ArchipelagoSettings? HostSettings { get; set; }
    public List<ApReceiptWireItem> Items { get; set; } = new();
}
