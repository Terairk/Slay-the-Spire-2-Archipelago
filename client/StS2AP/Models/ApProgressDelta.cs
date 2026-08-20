namespace StS2AP.Models;

/// <summary>
/// Ordered field-level mutation for <see cref="ApRunProgressState"/>. Assignment dictionaries
/// carry only changed keys and removals, so claiming one reward never resends every saved model.
/// </summary>
public sealed class ApProgressDelta
{
    public int? CardRewardsAttempted { get; set; }
    public int? RareCardRewardsAttempted { get; set; }
    public int? RelicRewardsAttempted { get; set; }
    public int? BankedRelicRewards { get; set; }
    public int? RelicRewardsAvailableAnytimeForRun { get; set; }
    public int? GoldRewardsAttempted { get; set; }
    public int? PotionRewardsAttempted { get; set; }
    public int? BossRewardsDistributed { get; set; }
    public int? GoldRedeemed { get; set; }

    public bool ProgressiveStarterCardBaseIdChanged { get; set; }
    public string? ProgressiveStarterCardBaseId { get; set; }
    public bool ProgressiveStarterCardUpgradedIdChanged { get; set; }
    public string? ProgressiveStarterCardUpgradedId { get; set; }
    public ProgressiveStarterTier? ProgressiveStarterCardTier { get; set; }
    public bool ProgressiveStarterRelicBaseIdChanged { get; set; }
    public string? ProgressiveStarterRelicBaseId { get; set; }
    public bool ProgressiveStarterRelicUpgradedIdChanged { get; set; }
    public string? ProgressiveStarterRelicUpgradedId { get; set; }
    public ProgressiveStarterTier? ProgressiveStarterRelicTier { get; set; }

    public Dictionary<int, List<string>> RelicChoiceAssignmentUpserts { get; set; } = new();
    public List<int> RelicChoiceAssignmentRemovals { get; set; } = new();
    public Dictionary<int, List<string>> AncientRelicChoiceAssignmentUpserts { get; set; } = new();
    public List<int> AncientRelicChoiceAssignmentRemovals { get; set; } = new();
    public Dictionary<int, ApCardAssignmentState> CardAssignmentUpserts { get; set; } = new();
    public List<int> CardAssignmentRemovals { get; set; } = new();
    public Dictionary<int, string> PotionAssignmentUpserts { get; set; } = new();
    public List<int> PotionAssignmentRemovals { get; set; } = new();
    public List<int> UsedItemsAdded { get; set; } = new();
    public List<int> UsedItemsRemoved { get; set; } = new();
    public HashSet<long> PendingLocationChecksAdded { get; set; } = new();
    public HashSet<long> PendingLocationChecksRemoved { get; set; } = new();
    public List<int>? Ascensions { get; set; }

    public bool HasChanges =>
        CardRewardsAttempted.HasValue
        || RareCardRewardsAttempted.HasValue
        || RelicRewardsAttempted.HasValue
        || BankedRelicRewards.HasValue
        || RelicRewardsAvailableAnytimeForRun.HasValue
        || GoldRewardsAttempted.HasValue
        || PotionRewardsAttempted.HasValue
        || BossRewardsDistributed.HasValue
        || GoldRedeemed.HasValue
        || ProgressiveStarterCardBaseIdChanged
        || ProgressiveStarterCardUpgradedIdChanged
        || ProgressiveStarterCardTier.HasValue
        || ProgressiveStarterRelicBaseIdChanged
        || ProgressiveStarterRelicUpgradedIdChanged
        || ProgressiveStarterRelicTier.HasValue
        || RelicChoiceAssignmentUpserts.Count > 0
        || RelicChoiceAssignmentRemovals.Count > 0
        || AncientRelicChoiceAssignmentUpserts.Count > 0
        || AncientRelicChoiceAssignmentRemovals.Count > 0
        || CardAssignmentUpserts.Count > 0
        || CardAssignmentRemovals.Count > 0
        || PotionAssignmentUpserts.Count > 0
        || PotionAssignmentRemovals.Count > 0
        || UsedItemsAdded.Count > 0
        || UsedItemsRemoved.Count > 0
        || PendingLocationChecksAdded.Count > 0
        || PendingLocationChecksRemoved.Count > 0
        || Ascensions != null;

    public static ApProgressDelta Between(ApRunProgressState before, ApRunProgressState after)
    {
        var delta = new ApProgressDelta
        {
            CardRewardsAttempted = Changed(before.CardRewardsAttempted, after.CardRewardsAttempted),
            RareCardRewardsAttempted = Changed(before.RareCardRewardsAttempted, after.RareCardRewardsAttempted),
            RelicRewardsAttempted = Changed(before.RelicRewardsAttempted, after.RelicRewardsAttempted),
            BankedRelicRewards = Changed(before.BankedRelicRewards, after.BankedRelicRewards),
            RelicRewardsAvailableAnytimeForRun = Changed(
                before.RelicRewardsAvailableAnytimeForRun,
                after.RelicRewardsAvailableAnytimeForRun
            ),
            GoldRewardsAttempted = Changed(before.GoldRewardsAttempted, after.GoldRewardsAttempted),
            PotionRewardsAttempted = Changed(before.PotionRewardsAttempted, after.PotionRewardsAttempted),
            BossRewardsDistributed = Changed(before.BossRewardsDistributed, after.BossRewardsDistributed),
            GoldRedeemed = Changed(before.GoldRedeemed, after.GoldRedeemed),
            ProgressiveStarterCardBaseIdChanged =
                before.ProgressiveStarterCardBaseId != after.ProgressiveStarterCardBaseId,
            ProgressiveStarterCardBaseId = after.ProgressiveStarterCardBaseId,
            ProgressiveStarterCardUpgradedIdChanged =
                before.ProgressiveStarterCardUpgradedId != after.ProgressiveStarterCardUpgradedId,
            ProgressiveStarterCardUpgradedId = after.ProgressiveStarterCardUpgradedId,
            ProgressiveStarterCardTier = Changed(
                before.ProgressiveStarterCardTier,
                after.ProgressiveStarterCardTier
            ),
            ProgressiveStarterRelicBaseIdChanged =
                before.ProgressiveStarterRelicBaseId != after.ProgressiveStarterRelicBaseId,
            ProgressiveStarterRelicBaseId = after.ProgressiveStarterRelicBaseId,
            ProgressiveStarterRelicUpgradedIdChanged =
                before.ProgressiveStarterRelicUpgradedId != after.ProgressiveStarterRelicUpgradedId,
            ProgressiveStarterRelicUpgradedId = after.ProgressiveStarterRelicUpgradedId,
            ProgressiveStarterRelicTier = Changed(
                before.ProgressiveStarterRelicTier,
                after.ProgressiveStarterRelicTier
            ),
        };

        DiffDictionary(
            before.RelicChoiceAssignments,
            after.RelicChoiceAssignments,
            static (left, right) => left.SequenceEqual(right),
            static value => value.ToList(),
            delta.RelicChoiceAssignmentUpserts,
            delta.RelicChoiceAssignmentRemovals
        );
        DiffDictionary(
            before.AncientRelicChoiceAssignments,
            after.AncientRelicChoiceAssignments,
            static (left, right) => left.SequenceEqual(right),
            static value => value.ToList(),
            delta.AncientRelicChoiceAssignmentUpserts,
            delta.AncientRelicChoiceAssignmentRemovals
        );
        DiffDictionary(
            before.CardAssignments,
            after.CardAssignments,
            CardAssignmentEquals,
            CloneCardAssignment,
            delta.CardAssignmentUpserts,
            delta.CardAssignmentRemovals
        );
        DiffDictionary(
            before.PotionAssignments,
            after.PotionAssignments,
            static (left, right) => left == right,
            static value => value,
            delta.PotionAssignmentUpserts,
            delta.PotionAssignmentRemovals
        );

        delta.UsedItemsAdded = after.UsedItems.Except(before.UsedItems).ToList();
        delta.UsedItemsRemoved = before.UsedItems.Except(after.UsedItems).ToList();
        delta.PendingLocationChecksAdded = after.PendingLocationChecks
            .Except(before.PendingLocationChecks).ToHashSet();
        delta.PendingLocationChecksRemoved = before.PendingLocationChecks
            .Except(after.PendingLocationChecks).ToHashSet();
        if (!before.Ascensions.SequenceEqual(after.Ascensions))
            delta.Ascensions = after.Ascensions.ToList();
        return delta;
    }

    public ApRunProgressState ApplyToCopy(ApRunProgressState source)
    {
        ApRunProgressState result = Clone(source);
        Apply(CardRewardsAttempted, value => result.CardRewardsAttempted = value);
        Apply(RareCardRewardsAttempted, value => result.RareCardRewardsAttempted = value);
        Apply(RelicRewardsAttempted, value => result.RelicRewardsAttempted = value);
        Apply(BankedRelicRewards, value => result.BankedRelicRewards = value);
        Apply(RelicRewardsAvailableAnytimeForRun, value => result.RelicRewardsAvailableAnytimeForRun = value);
        Apply(GoldRewardsAttempted, value => result.GoldRewardsAttempted = value);
        Apply(PotionRewardsAttempted, value => result.PotionRewardsAttempted = value);
        Apply(BossRewardsDistributed, value => result.BossRewardsDistributed = value);
        Apply(GoldRedeemed, value => result.GoldRedeemed = value);
        if (ProgressiveStarterCardBaseIdChanged)
            result.ProgressiveStarterCardBaseId = ProgressiveStarterCardBaseId;
        if (ProgressiveStarterCardUpgradedIdChanged)
            result.ProgressiveStarterCardUpgradedId = ProgressiveStarterCardUpgradedId;
        Apply(ProgressiveStarterCardTier, value => result.ProgressiveStarterCardTier = value);
        if (ProgressiveStarterRelicBaseIdChanged)
            result.ProgressiveStarterRelicBaseId = ProgressiveStarterRelicBaseId;
        if (ProgressiveStarterRelicUpgradedIdChanged)
            result.ProgressiveStarterRelicUpgradedId = ProgressiveStarterRelicUpgradedId;
        Apply(ProgressiveStarterRelicTier, value => result.ProgressiveStarterRelicTier = value);

        ApplyDictionary(result.RelicChoiceAssignments, RelicChoiceAssignmentUpserts, RelicChoiceAssignmentRemovals);
        ApplyDictionary(result.AncientRelicChoiceAssignments, AncientRelicChoiceAssignmentUpserts, AncientRelicChoiceAssignmentRemovals);
        ApplyDictionary(result.CardAssignments, CardAssignmentUpserts, CardAssignmentRemovals);
        ApplyDictionary(result.PotionAssignments, PotionAssignmentUpserts, PotionAssignmentRemovals);
        result.UsedItems.RemoveAll(UsedItemsRemoved.Contains);
        foreach (int item in UsedItemsAdded)
            if (!result.UsedItems.Contains(item))
                result.UsedItems.Add(item);
        result.PendingLocationChecks.ExceptWith(PendingLocationChecksRemoved);
        result.PendingLocationChecks.UnionWith(PendingLocationChecksAdded);
        if (Ascensions != null)
            result.Ascensions = Ascensions.ToList();
        return result;
    }

    private static ApRunProgressState Clone(ApRunProgressState source) => new()
    {
        Initialized = source.Initialized,
        CardRewardsAttempted = source.CardRewardsAttempted,
        RareCardRewardsAttempted = source.RareCardRewardsAttempted,
        RelicRewardsAttempted = source.RelicRewardsAttempted,
        BankedRelicRewards = source.BankedRelicRewards,
        RelicRewardsAvailableAnytimeForRun = source.RelicRewardsAvailableAnytimeForRun,
        GoldRewardsAttempted = source.GoldRewardsAttempted,
        PotionRewardsAttempted = source.PotionRewardsAttempted,
        BossRewardsDistributed = source.BossRewardsDistributed,
        RelicChoiceAssignments = source.RelicChoiceAssignments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList()
        ),
        AncientRelicChoiceAssignments = source.AncientRelicChoiceAssignments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList()
        ),
        CardAssignments = source.CardAssignments.ToDictionary(
            pair => pair.Key,
            pair => CloneCardAssignment(pair.Value)
        ),
        PotionAssignments = new Dictionary<int, string>(source.PotionAssignments),
        UsedItems = source.UsedItems.ToList(),
        GoldRedeemed = source.GoldRedeemed,
        ProgressiveStarterCardBaseId = source.ProgressiveStarterCardBaseId,
        ProgressiveStarterCardUpgradedId = source.ProgressiveStarterCardUpgradedId,
        ProgressiveStarterCardTier = source.ProgressiveStarterCardTier,
        ProgressiveStarterRelicBaseId = source.ProgressiveStarterRelicBaseId,
        ProgressiveStarterRelicUpgradedId = source.ProgressiveStarterRelicUpgradedId,
        ProgressiveStarterRelicTier = source.ProgressiveStarterRelicTier,
        PendingLocationChecks = new HashSet<long>(source.PendingLocationChecks),
        Ascensions = source.Ascensions.ToList(),
    };

    private static int? Changed(int before, int after) => before == after ? null : after;
    private static T? Changed<T>(T before, T after) where T : struct =>
        EqualityComparer<T>.Default.Equals(before, after) ? null : after;
    private static bool CardAssignmentEquals(
        ApCardAssignmentState left,
        ApCardAssignmentState right) =>
        left.CanReroll == right.CanReroll
        && left.SerializedCards.SequenceEqual(right.SerializedCards);

    private static ApCardAssignmentState CloneCardAssignment(ApCardAssignmentState value) => new()
    {
        CanReroll = value.CanReroll,
        SerializedCards = value.SerializedCards.ToList(),
    };

    private static void DiffDictionary<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> before,
        IReadOnlyDictionary<TKey, TValue> after,
        Func<TValue, TValue, bool> equal,
        Func<TValue, TValue> clone,
        IDictionary<TKey, TValue> upserts,
        ICollection<TKey> removals)
        where TKey : notnull
    {
        foreach ((TKey key, TValue value) in after)
        {
            if (!before.TryGetValue(key, out TValue? oldValue) || !equal(oldValue, value))
                upserts[key] = clone(value);
        }
        foreach (TKey key in before.Keys)
            if (!after.ContainsKey(key))
                removals.Add(key);
    }

    private static void ApplyDictionary<TKey, TValue>(
        IDictionary<TKey, TValue> target,
        IReadOnlyDictionary<TKey, TValue> upserts,
        IEnumerable<TKey> removals)
        where TKey : notnull
    {
        foreach (TKey key in removals)
            target.Remove(key);
        foreach ((TKey key, TValue value) in upserts)
            target[key] = value;
    }

    private static void Apply<T>(T? value, Action<T> setter) where T : struct
    {
        if (value.HasValue)
            setter(value.Value);
    }
}
