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

    // AP_MP: Enable these next as their native synchronized grant paths are implemented.
    CardRewards,
    RelicRewards,
    PotionRewards,

    // AP_MP: Keep these disabled until their owner-only checks and replicated results agree.
    CombatRewardLocations,
    FloorChecks,
    Shops,
    RestSites,
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
    Singleplayer,
    Multiplayer,
}
