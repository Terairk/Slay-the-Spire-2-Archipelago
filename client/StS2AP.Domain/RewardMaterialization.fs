namespace StS2AP.Domain

open System

/// Invalid card or potion generation settings.
[<RequireQualifiedAccess>]
type MaterializationError =
    | UnknownStrategy of wireId: string
    | InconsistentContract

    /// Calls the C# handler for this error.
    member this.Match(unknownStrategy: Func<string, 'T>, inconsistentContract: Func<'T>) : 'T =
        match this with
        | MaterializationError.UnknownStrategy wireId -> unknownStrategy.Invoke(wireId)
        | MaterializationError.InconsistentContract -> inconsistentContract.Invoke()

/// How machines agree on a card or potion reward.
/// The owner is the reward's player, not necessarily the host.
type RewardMaterialization =
    private
    /// Normal path: the owner sends the chosen reward; other machines use it without rolling again.
    | OwnerFinal
    /// Reuses a reward already rolled with the game's RNG, without rolling again.
    | RestoredReplicaNative
    /// Each machine rolls with the game's RNG and checks that results match. Diagnostics only; currently disabled.
    | NewReplicaNative

    member this.RequiresNativeMaterialization =
        match this with
        | OwnerFinal | RestoredReplicaNative -> false
        | NewReplicaNative -> true

    member this.StrategyId =
        match this with
        | OwnerFinal -> "ap_rng_owner_final_v1"
        | RestoredReplicaNative | NewReplicaNative -> "replica_native_v1"

    /// Calls the C# handler for this choice.
    member this.Match(ownerFinal: Func<'T>, restoredReplicaNative: Func<'T>, newReplicaNative: Func<'T>) : 'T =
        match this with
        | OwnerFinal -> ownerFinal.Invoke()
        | RestoredReplicaNative -> restoredReplicaNative.Invoke()
        | NewReplicaNative -> newReplicaNative.Invoke()

    /// Checks the received settings. Unknown IDs (including null) are errors.
    static member Decode(strategyId: string, requiresNativeMaterialization: bool) =
        match strategyId, requiresNativeMaterialization with
        | "ap_rng_owner_final_v1", false -> Ok OwnerFinal
        | "ap_rng_owner_final_v1", true -> Error MaterializationError.InconsistentContract
        | "replica_native_v1", false -> Ok RestoredReplicaNative
        | "replica_native_v1", true -> Ok NewReplicaNative
        | unknown, _ -> Error (MaterializationError.UnknownStrategy unknown)
