using System.Text.Json.Serialization;

namespace StS2AP.Models;

/// <summary>
/// The complete run-scoped state for one progressive starter. Sending the related fields as one
/// unit prevents a peer from observing an ID/tier combination that never existed on the owner.
/// </summary>
public sealed class ApProgressiveStarterState
{
    public string? BaseId { get; set; }
    public string? UpgradedId { get; set; }
    public ProgressiveStarterTier Tier { get; set; }
}

/// <summary>
/// Ordered field-level mutation for <see cref="ApRunProgressState"/>. Assignment dictionaries
/// carry only changed keys and removals, so claiming one reward never resends every saved model.
/// </summary>
public sealed class ApProgressDelta
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CardRewardsAttempted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RareCardRewardsAttempted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RelicRewardsAttempted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BankedRelicRewards { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RelicRewardsAvailableAnytimeForRun { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GoldRewardsAttempted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PotionRewardsAttempted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BossRewardsDistributed { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GoldRedeemed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApProgressiveStarterState? StarterCard { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApProgressiveStarterState? StarterRelic { get; set; }

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Ascensions { get; set; }

    [JsonIgnore]
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
        || StarterCard != null
        || StarterRelic != null
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
            StarterCard = StarterChanged(
                before.ProgressiveStarterCardBaseId,
                before.ProgressiveStarterCardUpgradedId,
                before.ProgressiveStarterCardTier,
                after.ProgressiveStarterCardBaseId,
                after.ProgressiveStarterCardUpgradedId,
                after.ProgressiveStarterCardTier
            ),
            StarterRelic = StarterChanged(
                before.ProgressiveStarterRelicBaseId,
                before.ProgressiveStarterRelicUpgradedId,
                before.ProgressiveStarterRelicTier,
                after.ProgressiveStarterRelicBaseId,
                after.ProgressiveStarterRelicUpgradedId,
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
        if (!before.Ascensions.ToHashSet().SetEquals(after.Ascensions))
            delta.Ascensions = after.Ascensions.Distinct().OrderBy(level => level).ToList();
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
        if (StarterCard != null)
        {
            result.ProgressiveStarterCardBaseId = StarterCard.BaseId;
            result.ProgressiveStarterCardUpgradedId = StarterCard.UpgradedId;
            result.ProgressiveStarterCardTier = StarterCard.Tier;
        }
        if (StarterRelic != null)
        {
            result.ProgressiveStarterRelicBaseId = StarterRelic.BaseId;
            result.ProgressiveStarterRelicUpgradedId = StarterRelic.UpgradedId;
            result.ProgressiveStarterRelicTier = StarterRelic.Tier;
        }

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
            result.Ascensions = Ascensions.Distinct().OrderBy(level => level).ToList();
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

    private static ApProgressiveStarterState? StarterChanged(
        string? beforeBaseId,
        string? beforeUpgradedId,
        ProgressiveStarterTier beforeTier,
        string? afterBaseId,
        string? afterUpgradedId,
        ProgressiveStarterTier afterTier)
    {
        if (string.Equals(beforeBaseId, afterBaseId, StringComparison.Ordinal)
            && string.Equals(beforeUpgradedId, afterUpgradedId, StringComparison.Ordinal)
            && beforeTier == afterTier)
        {
            return null;
        }

        return new ApProgressiveStarterState
        {
            BaseId = afterBaseId,
            UpgradedId = afterUpgradedId,
            Tier = afterTier,
        };
    }

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
