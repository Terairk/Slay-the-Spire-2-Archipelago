using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace StS2AP.Utils;

/// <summary>
/// Every relic the mod keeps out of a run or out of the Ancient choices it builds, with the
/// reason. Add new relic exclusions here rather than as another IsAllowed patch.
///
/// Run rules are enforced through <see cref="RelicModel.IsAllowed"/> by
/// <c>Patches_RelicRules</c>. Every relic source asks it: the grab bags behind elites, chests,
/// shops and events, AP's own relic rewards, Custom Shop Stock, AP's Ancient choices, and from
/// 0.113 the natural Ancients too. Grab bags drop a disallowed relic for good, and a shared bag
/// that refills a rarity asks again, so the check has to hold for the whole run.
///
/// Ancient choice rules apply only to the Ancient choices AP builds itself
/// (<see cref="AncientRelicPool"/>), not to natural Ancient events.
/// </summary>
public static class RelicRules
{
    /// <summary>Keeps <see cref="Relics"/> out of the whole run while <see cref="Applies"/> holds.</summary>
    private sealed record RunRule(string Reason, Type[] Relics, Func<IRunState, bool> Applies);

    /// <summary>
    /// Keeps <see cref="Relics"/> out of AP-built Ancient choices. The int is the zero-based act
    /// of an immediate start-of-act Ancient, or null for Anytime rewards and shop Ancient slots.
    /// </summary>
    private sealed record AncientChoiceRule(
        string Reason,
        Type[] Relics,
        Func<RelicModel, Player, ArchipelagoSettings?, int?, bool> Applies);

    private static readonly RunRule[] RunRules =
    [
        // Their extra card rewards arrive after AP turns combat card rewards into checks, so they
        // hand back the card supply the randomizer withholds. Relic pools and RNG belong to the
        // whole run, so in multiplayer one player choosing the category removes them for all.
        new("Remove Overpowered Relics: Rare",
            [typeof(PrayerWheel), typeof(WhiteStar)],
            runState => PlayersWith(runState, settings =>
                settings.RemovedOpRelics.HasFlag(OpRelicCategories.Rare)).Any()),

        // Wing Charm advances the host-authoritative shared Niche RNG while it modifies card
        // rewards, but only the owner of an AP card reward builds its cards.
        new("Wing Charm is not safe in AP multiplayer",
            [typeof(WingCharm)],
            _ => MultiplayerSupport.IsMultiplayerScope),

#if STS2_AT_LEAST_0_113_0
        // From 0.113 every machine replays a player's Lord's Parasol against its own copy of that
        // player's shop. Shop Sanity locks slots only on the owner's machine, so the others would
        // buy the locked slots as well and then wait for picks the owner never makes. Until shop
        // locks are shared between players, keep it out while any player has Shop Sanity.
        new("Lord's Parasol buys locked Shop Sanity slots in multiplayer",
            [typeof(LordsParasol)],
            runState => MultiplayerSupport.IsRealMultiplayerRun
                && MultiplayerSupport.ShouldRunReplicatedConstruction(MultiplayerFeature.Shops)
                && PlayersWith(runState, settings => settings.ShopSanity).Any()),
#endif

        // Juzu Bracelet turns ? room fights back into events, which the modifier exists to undo.
        new("Dangerous Unknowns run modifier",
            [typeof(JuzuBracelet)],
            runState => runState.Modifiers.Any(modifier => modifier is ApDangerousUnknowns)),
    ];

    private static readonly AncientChoiceRule[] AncientChoiceRules =
    [
        new("Driftwood is never offered in AP Ancient choices",
            [typeof(Driftwood)],
            (_, _, _, _) => true),

        // Golden Compass regenerates the current map on pickup, so it only fits the Act 2 Ancient.
        new("Golden Compass is offered only at the start of Act 2",
            [typeof(GoldenCompass)],
            (_, _, _, startOfActIndex) => startOfActIndex != 1),

        // An unseeded Anytime run can be restarted for free until floor 1 offers one of these.
        // Pael's Growth then doubles a strong Clone target at every rest site, and Glitter gives
        // Glam to every AP card reward. Start-of-act Ancients keep them: rerolling there costs a
        // whole act. Ancient choices belong to one player, so only that player's settings count.
        new("Remove Overpowered Relics: Ancient",
            [typeof(PaelsGrowth), typeof(Glitter)],
            (_, player, settings, startOfActIndex) => startOfActIndex == null
                && ResolveSettings(player, settings)?.RemovedOpRelics.HasFlag(OpRelicCategories.Ancient) == true),

        new("Progressive starter replaces it",
            [typeof(ArchaicTooth), typeof(TouchOfOrobas)],
            (relic, player, settings, _) => ProgressiveStarterUtility.ShouldExcludeAncientRelic(relic, player, settings)),
    ];

    private static readonly Dictionary<Type, RunRule[]> RunRulesByRelic = IndexByRelic(RunRules, rule => rule.Relics);

    private static readonly Dictionary<Type, AncientChoiceRule[]> AncientChoiceRulesByRelic =
        IndexByRelic(AncientChoiceRules, rule => rule.Relics);

    // A removal is logged once per run and relic, so a report's log shows which rule removed what.
    private static readonly ConditionalWeakTable<object, HashSet<Type>> LoggedRunRemovals = new();

    /// <summary>Relic types that run rules can remove, for patching their own IsAllowed overrides.</summary>
    public static IEnumerable<Type> RunRuleRelicTypes => RunRulesByRelic.Keys;

    /// <summary>Whether a run rule keeps this relic out of the run.</summary>
    public static bool IsKeptOutOfRun(RelicModel relic, IRunState runState)
    {
        if (!RunRulesByRelic.TryGetValue(relic.GetType(), out RunRule[]? rules))
            return false;

        RunRule? rule = rules.FirstOrDefault(rule => rule.Applies(runState));
        if (rule == null)
            return false;

        if (LoggedRunRemovals.GetOrCreateValue(runState).Add(relic.GetType()))
            LogUtility.Info($"Relic rules: {relic.GetType().Name} is kept out of this run ({rule.Reason}).");
        return true;
    }

    /// <summary>
    /// Whether an AP-built Ancient choice must not offer this relic. Null settings mean the
    /// player's own slot settings.
    /// </summary>
    public static bool IsExcludedFromAncientChoices(
        RelicModel relic,
        Player player,
        ArchipelagoSettings? settings,
        int? startOfActIndex) =>
        AncientChoiceRulesByRelic.TryGetValue(relic.GetType(), out AncientChoiceRule[]? rules)
        && rules.Any(rule => rule.Applies(relic, player, settings, startOfActIndex));

    /// <summary>
    /// Describes every rule for the current run, with each player's settings as this machine
    /// reads them. Every machine in a multiplayer run should print the same answers.
    /// </summary>
    public static string Describe(IRunState runState)
    {
        var lines = new List<string> { $"Relic rules on this machine ({runState.Players.Count} player(s)):" };
        foreach (Player player in runState.Players)
        {
            string settingsText = ApPlayerContextResolver.TryGetRewardSettings(player, out ArchipelagoSettings settings)
                ? $"Remove Overpowered Relics = {settings.RemovedOpRelics}, Shop Sanity = {settings.ShopSanity}"
                : "no AP slot settings found (vanilla guest, or run data missing)";
            lines.Add($"  Player {player.NetId} ({player.Character.Id.Entry}): {settingsText}");
        }

        foreach (RunRule rule in RunRules)
        {
            bool applies = rule.Applies(runState);
            IEnumerable<string> relics = rule.Relics.Select(type =>
            {
                RelicModel? relic = ModelDb.AllRelics.FirstOrDefault(model => model.GetType() == type);
                return relic == null ? type.Name : $"{relic.Id.Entry} (IsAllowed={relic.IsAllowed(runState)})";
            });
            lines.Add($"  [{(applies ? "removed" : "allowed")}] {rule.Reason}: {string.Join(", ", relics)}");
        }

        lines.Add("  Ancient choice rules: " + string.Join("; ", AncientChoiceRules.Select(rule => rule.Reason)));
        return string.Join("\n", lines);
    }

    private static IEnumerable<Player> PlayersWith(IRunState runState, Func<ArchipelagoSettings, bool> predicate) =>
        runState.Players.Where(player =>
            ApPlayerContextResolver.TryGetRewardSettings(player, out ArchipelagoSettings settings)
            && predicate(settings));

    private static ArchipelagoSettings? ResolveSettings(Player player, ArchipelagoSettings? settings) =>
        settings ?? (ApPlayerContextResolver.TryGetRewardSettings(player, out ArchipelagoSettings resolved)
            ? resolved
            : null);

    private static Dictionary<Type, TRule[]> IndexByRelic<TRule>(IEnumerable<TRule> rules, Func<TRule, Type[]> relics) =>
        rules
            .SelectMany(rule => relics(rule).Select(type => (type, rule)))
            .GroupBy(pair => pair.type)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.rule).ToArray());
}
