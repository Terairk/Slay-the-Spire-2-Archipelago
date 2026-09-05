namespace StS2AP.Domain.Tests

open System
open FsCheck
open FsCheck.Xunit
open StS2AP.Domain
open global.Xunit

module RewardMaterializationTests =
    let private decode strategy replay =
        match RewardMaterialization.Decode(strategy, replay) with
        | Ok policy -> policy
        | Error error -> failwithf "Expected a valid materialization policy, got %A" error

    let private validInputs =
        [| "ap_rng_owner_final_v1", false
           "replica_native_v1", false
           "replica_native_v1", true |]

    [<Fact>]
    let ``owner-final models do not require replica generation`` () =
        let policy = decode "ap_rng_owner_final_v1" false
        Assert.False(policy.RequiresNativeMaterialization)
        Assert.Equal("ap_rng_owner_final_v1", policy.StrategyId)

    [<Fact>]
    let ``restored native assignment keeps its strategy without replaying generation`` () =
        let policy = decode "replica_native_v1" false
        Assert.False(policy.RequiresNativeMaterialization)
        Assert.Equal("replica_native_v1", policy.StrategyId)

    [<Fact>]
    let ``new native assignment requires replica generation`` () =
        let policy = decode "replica_native_v1" true
        Assert.True(policy.RequiresNativeMaterialization)
        Assert.Equal("replica_native_v1", policy.StrategyId)

    [<Fact>]
    let ``owner-final with native replay is a contract error`` () =
        match RewardMaterialization.Decode("ap_rng_owner_final_v1", true) with
        | Error MaterializationError.InconsistentContract -> ()
        | actual -> failwithf "Expected InconsistentContract, got %A" actual

    [<Fact>]
    let ``restored native and owner-final remain distinct despite both disabling replay`` () =
        let ownerFinal = decode "ap_rng_owner_final_v1" false
        let restoredNative = decode "replica_native_v1" false
        Assert.NotEqual<RewardMaterialization>(ownerFinal, restoredNative)

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" ")>]
    [<InlineData("REPLICA_NATIVE_V1")>]
    [<InlineData("replica_native_v1 ")>]
    [<InlineData("replica_native_v2")>]
    let ``malformed strategy is rejected without normalization or fallback`` (strategy: string) =
        for replay in [ false; true ] do
            match RewardMaterialization.Decode(strategy, replay) with
            | Error (MaterializationError.UnknownStrategy original) ->
                Assert.Equal<string>(strategy, original)
            | actual -> failwithf "Expected UnknownStrategy, got %A" actual

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    let ``policy eliminator invokes only its selected delegate`` (selected: int) =
        let strategy, replay = validInputs[selected]
        let policy = decode strategy replay
        let calls = ResizeArray<int>()
        let handler index = Func<int>(fun () ->
            calls.Add(index)
            if index <> selected then failwith "An unselected branch was evaluated"
            index)
        let actual = policy.Match(handler 0, handler 1, handler 2)
        Assert.Equal(selected, actual)
        Assert.Equal<int list>([ selected ], List.ofSeq calls)

    [<Property(MaxTest = 500)>]
    let ``valid policy survives a wire round trip`` (NonNegativeInt selection) =
        let strategy, replay = validInputs[selection % validInputs.Length]
        let policy = decode strategy replay
        RewardMaterialization.Decode(policy.StrategyId, policy.RequiresNativeMaterialization) = Ok policy

    [<Property(MaxTest = 500)>]
    let ``repeated decoding has no hidden history`` (strategy: string) (replay: bool) =
        let first = RewardMaterialization.Decode(strategy, replay)
        // Interleave a different valid request to detect accidental cached/global decisions.
        let unrelated = decode "replica_native_v1" (not replay)
        unrelated.RequiresNativeMaterialization = not replay
        && first = RewardMaterialization.Decode(strategy, replay)

    [<Property(MaxTest = 500)>]
    let ``unknown wire data is retained exactly for either replay flag`` (suffix: string) (replay: bool) =
        let strategy = "unsupported:" + suffix
        match RewardMaterialization.Decode(strategy, replay) with
        | Error (MaterializationError.UnknownStrategy original) -> original = strategy
        | _ -> false

    [<Property(MaxTest = 500)>]
    let ``all valid policies remain pairwise distinct after round trips`` (NonNegativeInt start) =
        let policies =
            [ for offset in 0 .. validInputs.Length - 1 do
                  let strategy, replay = validInputs[(start % validInputs.Length + offset) % validInputs.Length]
                  let policy = decode strategy replay
                  yield decode policy.StrategyId policy.RequiresNativeMaterialization ]
        Set.ofList policies |> Set.count = validInputs.Length
