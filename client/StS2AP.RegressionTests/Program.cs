using System.Text.Json;
using StS2AP.Persistence;

RunReplicaLocalConstructionRegression();
Console.WriteLine("Replica-local construction regression checks passed.");

static void RunReplicaLocalConstructionRegression()
{
    var slowReplica = new ApReplicaConstructionState();
    var checkpointActs = new HashSet<int> { 1 };

    Assert(
        slowReplica.EnsureInitialized(18, 4, 7, 6, checkpointActs),
        "The checkpoint must seed a fresh replica."
    );
    checkpointActs.Add(3);
    AssertSet(slowReplica.MultiplayerBossCompensatedActs, 1);

    // The fast owner has already constructed Act 2's compensation and published it. The slow
    // replica has not reached that construction boundary, so the live canonical update must not
    // move its cursor or import the owner's compensation marker.
    Assert(
        !slowReplica.EnsureInitialized(19, 5, 8, 7, new[] { 1, 2 }),
        "A live owner update must not reseed an active replica."
    );
    AssertEqual(18, slowReplica.CardRewardsAttempted, "card cursor after live publication");
    AssertEqual(4, slowReplica.RareCardRewardsAttempted, "rare-card cursor after live publication");
    AssertEqual(7, slowReplica.GoldRewardsAttempted, "gold cursor after live publication");
    AssertEqual(6, slowReplica.PotionRewardsAttempted, "potion cursor after live publication");
    AssertSet(slowReplica.MultiplayerBossCompensatedActs, 1);

    // When the slow replica reaches the same boundary, it performs the same construction exactly
    // once and converges with the owner's durable counters.
    Assert(slowReplica.TryMarkBossCompensation(2), "Act 2 must be compensated locally once.");
    Assert(!slowReplica.TryMarkBossCompensation(2), "Act 2 must not be compensated twice.");
    AssertEqual(19, slowReplica.IncrementCardRewards(), "local Act 2 card compensation");
    AssertEqual(5, slowReplica.IncrementRareCardRewards(), "local rare-card construction");
    AssertEqual(8, slowReplica.IncrementGoldRewards(), "local gold construction");
    AssertEqual(7, slowReplica.IncrementPotionRewards(), "local potion construction");
    AssertSet(slowReplica.MultiplayerBossCompensatedActs, 1, 2);

    // A reconstructed process is allowed to seed from the host checkpoint again.
    var rejoinedReplica = new ApReplicaConstructionState();
    Assert(
        rejoinedReplica.EnsureInitialized(19, 5, 8, 7, new[] { 1, 2 }),
        "A rejoined replica must accept the host checkpoint."
    );
    AssertEqual(19, rejoinedReplica.CardRewardsAttempted, "rejoined card cursor");
    AssertSet(rejoinedReplica.MultiplayerBossCompensatedActs, 1, 2);

    string serializedCheckpoint = JsonSerializer.Serialize(rejoinedReplica);
    ApReplicaConstructionState restoredCheckpoint =
        JsonSerializer.Deserialize<ApReplicaConstructionState>(serializedCheckpoint)
        ?? throw new InvalidOperationException("The construction checkpoint did not deserialize.");
    Assert(restoredCheckpoint.Initialized, "The restored checkpoint must remain initialized.");
    AssertEqual(19, restoredCheckpoint.CardRewardsAttempted, "restored card cursor");
    AssertSet(restoredCheckpoint.MultiplayerBossCompensatedActs, 1, 2);
    Assert(
        !restoredCheckpoint.EnsureInitialized(20, 6, 9, 8, new[] { 1, 2, 3 }),
        "A restored host checkpoint must not be overwritten by a later owner publication."
    );
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual(int expected, int actual, string description)
{
    if (actual != expected)
        throw new InvalidOperationException(
            $"Expected {description} to be {expected}, but it was {actual}."
        );
}

static void AssertSet(HashSet<int> actual, params int[] expected)
{
    if (!actual.SetEquals(expected))
        throw new InvalidOperationException(
            $"Expected acts [{string.Join(", ", expected)}], but found "
                + $"[{string.Join(", ", actual.Order())}]."
        );
}
