namespace StS2AP.Models;

/// <summary>
/// AP capabilities that must be reviewed independently before they are enabled in a real
/// multiplayer run. The initial experimental profile deliberately enables only the small
/// local-ownership and gold-reward slice.
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

    // AP_MP: Keep these disabled until their owner-only checks and replicated results agree.
    CombatRewardLocations,
    FloorChecks,
    Shops,
    RestSites,
    // AP_MP: This broader switch still owns natural Ancient events/start-of-act patches.
    Ancients,
    VictoryChecks,

    // AP_MP: Keep these disabled until their managed/native synchronization is implemented.
    ProgressiveStarters,
    AscensionEffects,
    CombatEffects,
    DeathLink,

    // AP_MP: Keep this disabled while multiplayer runs are intentionally disposable.
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
/// The local player's identity source for an AP multiplayer run. A guest participates in
/// MegaCrit multiplayer but owns no AP connection, receipts, checks, or private AP journal.
/// </summary>
public enum ApParticipationKind
{
    Guest,
    Archipelago,
}
