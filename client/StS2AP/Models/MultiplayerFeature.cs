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

    // Planned standard reward conversions.
    CardRewards,
    RelicRewards,
    PotionRewards,

    // Planned AP location and option conversions.
    CombatRewardLocations,
    FloorChecks,
    Shops,
    RestSites,
    Ancients,
    VictoryChecks,

    // Planned derived-state and combat conversions.
    ProgressiveStarters,
    AscensionEffects,
    CombatEffects,
    DeathLink,

    // Planned durability work.
    SaveAndReconnect,

    // Unknown received items fail closed until explicitly classified.
    UnknownReceivedItems,
}

/// <summary>The play flow that requested an Archipelago connection.</summary>
public enum ApPlayDestination
{
    Singleplayer,
    Multiplayer,
}
