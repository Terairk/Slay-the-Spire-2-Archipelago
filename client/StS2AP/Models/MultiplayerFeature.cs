namespace StS2AP.Models;

/// <summary>
/// AP capabilities that must be reviewed independently before they are enabled in a real
/// multiplayer run. Unsupported gameplay systems remain disabled independently of the
/// persistence and participant-mode implementation.
/// </summary>
public enum MultiplayerFeature
{
    // Enabled in the first experimental profile.
    CharacterUnlocks,
    PressStartCheck,
    GoldRewards,

    // Mirrored through RitsuLib Sidecar plus MegaCrit's native reward synchronizers.
    CardRewards,
    RelicRewards,
    PotionRewards,
    AncientRewardChoices,

    // Native reward construction/check ownership is deterministic across replicas.
    CombatRewardLocations,
    FloorChecks,

    // Local inventory capabilities plus owner-only AP checks and native purchase effects.
    Shops,

    // Host-confirmed AP inputs plus native dense-list manifest validation.
    RestSites,
    // Host-confirmed per-owner thresholds plus MegaCrit's native event option synchronizer.
    Ancients,
    // AP_MP: Keep this disabled until its owner-only checks and replicated results agree.
    VictoryChecks,

    // AP_MP: Keep these disabled until their managed/native synchronization is implemented.
    ProgressiveStarters,
    AscensionEffects,
    CombatEffects,
    DeathLink,

    // Native host save/rejoin with AP progress embedded through RitsuLib run data.
    SaveAndReconnect,

    // AP_MP: Unknown received items fail closed until explicitly classified.
    UnknownReceivedItems,
}

/// <summary>The play flow that requested an Archipelago connection.</summary>
public enum ApPlayDestination
{
    None,
    Singleplayer,
    Multiplayer,
}

/// <summary>
/// The local player's identity source for an AP multiplayer run. An AP Guest borrows the fixed
/// STS host's receipt/check source while retaining independent Net-ID-keyed consumption state;
/// a Vanilla Guest has no AP rewards or checks.
/// </summary>
public enum ApParticipationKind
{
    VanillaGuest,
    ApGuest,
    OwnApSlot,
}

public enum SharedSlotCheckScope
{
    HostCharacterOnly,
    AllApParticipants,
}
