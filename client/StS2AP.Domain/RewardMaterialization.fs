namespace StS2AP.Domain

open System

/// Failure to interpret the existing card/potion wire contract.
[<RequireQualifiedAccess>]
type MaterializationError =
    | UnknownStrategy of wireId: string
    | InconsistentContract

    /// Keeps C# error handling exhaustive without branching on diagnostic text.
    member this.Match(unknownStrategy: Func<string, 'T>, inconsistentContract: Func<'T>) : 'T =
        match this with
        | MaterializationError.UnknownStrategy wireId -> unknownStrategy.Invoke(wireId)
        | MaterializationError.InconsistentContract -> inconsistentContract.Invoke()

/// Validated card/potion generation policy. Construction is restricted to Decode.
/// Restoring a replica-native assignment must never replay the original native rolls.
type RewardMaterialization =
    private
    | OwnerFinal
    | RestoredReplicaNative
    | NewReplicaNative

    member this.RequiresNativeMaterialization =
        match this with
        | OwnerFinal | RestoredReplicaNative -> false
        | NewReplicaNative -> true

    member this.StrategyId =
        match this with
        | OwnerFinal -> "ap_rng_owner_final_v1"
        | RestoredReplicaNative | NewReplicaNative -> "replica_native_v1"

    /// C# consumes named decisions without constructing F# union cases or functions.
    member this.Match(ownerFinal: Func<'T>, restoredReplicaNative: Func<'T>, newReplicaNative: Func<'T>) : 'T =
        match this with
        | OwnerFinal -> ownerFinal.Invoke()
        | RestoredReplicaNative -> restoredReplicaNative.Invoke()
        | NewReplicaNative -> newReplicaNative.Invoke()

    /// Accepts exactly the strategy/flag combinations previously accepted by the host.
    /// Unknown (including null) strategy IDs remain errors, never a fallback strategy.
    static member Decode(strategyId: string, requiresNativeMaterialization: bool) =
        match strategyId, requiresNativeMaterialization with
        | "ap_rng_owner_final_v1", false -> Ok OwnerFinal
        | "ap_rng_owner_final_v1", true -> Error MaterializationError.InconsistentContract
        | "replica_native_v1", false -> Ok RestoredReplicaNative
        | "replica_native_v1", true -> Ok NewReplicaNative
        | unknown, _ -> Error (MaterializationError.UnknownStrategy unknown)
