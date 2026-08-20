using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StS2AP.Models
{
    public sealed class APCardAssignmentUnified
    {
        public List<string> SerializedCards { get; set; } = new();
        // CONFIRM: do we need this as we don't support driftwood atm with AP card rewards
        public bool CanReroll { get; set; }
    }

    /// <summary>
    /// Run-scoped AP progress shared by the singleplayer envelope and the host-owned
    /// per-player multiplayer snapshot. Received-item history is reconstructed separately.
    /// </summary>
    public class APProgressUnified
    {
        [JsonPropertyName("initialized")]
        public bool Initialized { get; set; }
        [JsonPropertyName("card_rewards_attempted")]
        public int CardRewardsAttempted { get; set; }
        [JsonPropertyName("rare_card_rewards_attempted")]
        public int RareCardRewardsAttempted { get; set; }
        [JsonPropertyName("relic_rewards_attempted")]
        public int RelicRewardsAttempted { get; set; }
        /// <summary>Earned relic rewards not yet paired with an AP Relic receipt.</summary>
        [JsonPropertyName("banked_relic_rewards")]
        public int BankedRelicRewards { get; set; }
        /// <summary>The anytime value captured when this run started.</summary>
        [JsonPropertyName("relic_rewards_available_anytime_for_run")]
        public int RelicRewardsAvailableAnytimeForRun { get; set; }
        [JsonPropertyName("gold_rewards_attempted")]
        public int GoldRewardsAttempted { get; set; }
        [JsonPropertyName("potion_rewards_attempted")]
        public int PotionRewardsAttempted { get; set; }
        [JsonPropertyName("boss_rewards_distributed")]
        public int BossRewardsDistributed { get; set; }
        [JsonPropertyName("unified_relic_choice_assignments")]
        public Dictionary<int, List<string>> RelicChoiceAssignments { get; set; } = new();
        [JsonPropertyName("unified_ancient_relic_choice_assignments")]
        public Dictionary<int, List<string>> AncientRelicChoiceAssignments { get; set; } = new();
        [JsonPropertyName("unified_card_assignments")]
        public Dictionary<int, APCardAssignmentUnified> CardAssignments { get; set; } = new();
        [JsonPropertyName("unified_potion_assignments")]
        public Dictionary<int, string> PotionAssignments { get; set; } = new();
        [JsonPropertyName("used_items")]
        public List<int> UsedItems { get; set; } = new List<int>();
        [JsonPropertyName("gold_redeemed")]
        public int GoldRedeemed { get; set; }
        [JsonPropertyName("progressive_starter_card_base_id")]
        public string? ProgressiveStarterCardBaseId { get; set; }
        [JsonPropertyName("progressive_starter_card_upgraded_id")]
        public string? ProgressiveStarterCardUpgradedId { get; set; }
        [JsonPropertyName("progressive_starter_card_tier")]
        public ProgressiveStarterTier ProgressiveStarterCardTier { get; set; } = ProgressiveStarterTier.Unsupported;
        [JsonPropertyName("progressive_starter_relic_base_id")]
        public string? ProgressiveStarterRelicBaseId { get; set; }
        [JsonPropertyName("progressive_starter_relic_upgraded_id")]
        public string? ProgressiveStarterRelicUpgradedId { get; set; }
        [JsonPropertyName("progressive_starter_relic_tier")]
        public ProgressiveStarterTier ProgressiveStarterRelicTier { get; set; } = ProgressiveStarterTier.Unsupported;
        [JsonPropertyName("pending_location_checks")]
        public HashSet<long> PendingLocationChecks { get; set; } = new HashSet<long>();
        public List<int> Ascensions { get; set; } = new List<int>();
    }

    /// <summary>
    /// Singleplayer persistence envelope. Multiplayer stores <see cref="APProgressUnified"/>
    /// in RitsuLib host run data and lets MegaCrit persist the native run itself.
    /// </summary>
    public class SerializableAP : APProgressUnified
    {
        // CONFIRM: why are these legacy, i can see they're different datatypes
        // but still, can we not actually unify them. don't care about old saves
        [JsonPropertyName("relic_choice_assignments")]
        public Dictionary<int, List<SerializableRelic>> LegacyRelicChoiceAssignments { get; set; } = new();
        [JsonPropertyName("ancient_relic_choice_assignments")]
        public Dictionary<int, List<SerializableRelic>> LegacyAncientRelicChoiceAssignments { get; set; } = new();
        [JsonPropertyName("card_assignments")]
        public Dictionary<int, SerializableReward> LegacyCardAssignments { get; set; } = new();
        [JsonPropertyName("card_models")]
        public Dictionary<int, List<SerializableCard>> LegacyCardAssignmentModels { get; set; } = new();
        [JsonPropertyName("potion_assignments")]
        public Dictionary<int, SerializablePotion> LegacyPotionAssignments { get; set; } = new();

        [JsonPropertyName("save_data")]
        // Keep the base-game save opaque to AP's source-generated serializer. The
        // running game must serialize and deserialize this payload with its own
        // MegaCritSerializerContext so public and beta save schemas can differ.
        public JsonElement? SaveData { get; set; }
    }

    [JsonSerializable(typeof(SerializableAP))]
    [JsonSerializable(typeof(APProgressUnified))]
    public partial class APSerializationContext : JsonSerializerContext
    {
        // Code gets generated I guess
    }

    public class SerializationUtility
    {

        public static JsonSerializerOptions CombinedOptions{ get; }

        static SerializationUtility() 
        {
            LogUtility.Info("Getting assembly");
            var megaAssembly = typeof(RunSaveManager).Assembly;
            LogUtility.Info("Getting megaContext");
            var contextType = megaAssembly.GetType("MegaCrit.Sts2.Core.Saves.MegaCritSerializerContext");
            LogUtility.Info("Getting Default");
            var fieldInfo = contextType.GetField("Default", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            LogUtility.Info("Getting Dereferencing Default");
            var megaResolver = (IJsonTypeInfoResolver?)fieldInfo?.GetValue(null);

            LogUtility.Info("Getting Options");
            var optionsInfo = contextType.GetField("s_defaultOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            LogUtility.Info("Dereferencing Options");
            var megaOptions = (JsonSerializerOptions?)optionsInfo?.GetValue(null);

            CombinedOptions = new JsonSerializerOptions(megaOptions)
            {
                TypeInfoResolver = JsonTypeInfoResolver.Combine(megaResolver, APSerializationContext.Default)
            };
        }

    }
}
