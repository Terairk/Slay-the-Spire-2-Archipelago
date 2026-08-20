namespace StS2AP.Models;

/// <summary>
/// Host-authored facts embedded in the canonical MegaCrit run snapshot. This is deliberately
/// limited to state every peer needs in order to interpret the same shared run.
/// </summary>
public sealed class ApRunSharedState
{
    /// <summary>Serialized payload schema only; this is not a multiplayer protocol version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Separates two AP/STSes runs even when their seeds and slot identities match.</summary>
    public Guid RunId { get; set; }

    /// <summary>
    /// The effective set chosen by the MegaCrit host. Host identity itself is derived from the
    /// live network service and is deliberately not duplicated in this record.
    /// </summary>
    public List<int> HostEffectiveAscensions { get; set; } = new();

    /// <summary>
    /// Durable proof that replicated effects are present in this checkpoint. The future effect
    /// executor must apply an effect and add its stable ID in one host-ordered operation.
    /// </summary>
    public HashSet<string> AppliedEffectIds { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One player's contribution to the canonical run snapshot. RitsuLib keys this value by the
/// MegaCrit Net ID, which is stable across disconnect/rejoin within this run; AP identity fields
/// are absent for guests. Do not remap this record by AP identity when a peer reconnects.
/// </summary>
public sealed class ApPlayerRunState
{
    /// <summary>Serialized payload schema only; this is not a multiplayer protocol version.</summary>
    public int SchemaVersion { get; set; } = 1;

    public ApParticipationKind Participation { get; set; } = ApParticipationKind.Guest;

    public string? ApRoomSeed { get; set; }

    public int? ApTeamId { get; set; }

    public int? ApSlotId { get; set; }

    /// <summary>
    /// Lobby readiness evidence contributed by this player. Complete means the initial AP
    /// receipt history was ingested far enough to rebuild unlocks, progression banks, and
    /// pending rewards; it does not mean every reward was claimed or applied. RitsuLib commits
    /// the same per-player object into the run snapshot, but this value is deliberately ignored
    /// after launch: an AP-bound rejoining process must prepare fresh server history again.
    ///
    /// TODO(AP_MP save/rejoin): receipt reconstruction is only half of rejoin readiness. Before
    /// save/rejoin is enabled, restore the owner's durable local journal (consumed receipt IDs,
    /// aggregate-gold cursor, stable reward assignments, pending/submitted/confirmed grants,
    /// buffs, and checks), reconcile it with current AP history and the host ledger, and only
    /// then contribute a complete readiness value. A missing journal uses the documented lossy
    /// salvage path instead of silently pretending an exact restore occurred.
    /// </summary>
    public bool ApHistoryComplete { get; set; }
}
