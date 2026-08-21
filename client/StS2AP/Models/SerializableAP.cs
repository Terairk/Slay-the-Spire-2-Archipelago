using MegaCrit.Sts2.Core.Saves.Managers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StS2AP.Models
{
    public sealed class ApCardAssignmentState
    {
        [JsonPropertyName("serialized_cards")]
        public List<string> SerializedCards { get; set; } = new();
        // Preserve the native reroll contract with the concrete card choices so restoring or
        // mirroring the assignment does not silently remove Driftwood's reroll behavior.
        [JsonPropertyName("can_reroll")]
        public bool CanReroll { get; set; }
    }

    /// <summary>
    /// Run-scoped AP progress shared by the singleplayer envelope and the host-owned
    /// per-player multiplayer snapshot. Received-item history is reconstructed separately.
    /// </summary>
    public sealed class ApRunProgressState
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
        /// <summary>
        /// Compact receipt evidence used by every multiplayer replica to make the same decision
        /// about retaining or banking a natural relic reward.
        /// </summary>
        [JsonPropertyName("relic_receipt_indexes_by_character")]
        public Dictionary<long, List<int>> RelicReceiptIndexesByCharacter { get; set; } = new();
        [JsonPropertyName("gold_rewards_attempted")]
        public int GoldRewardsAttempted { get; set; }
        [JsonPropertyName("potion_rewards_attempted")]
        public int PotionRewardsAttempted { get; set; }
        [JsonPropertyName("boss_rewards_distributed")]
        public int BossRewardsDistributed { get; set; }
        [JsonPropertyName("relic_choice_assignments")]
        public Dictionary<int, List<string>> RelicChoiceAssignments { get; set; } = new();
        [JsonPropertyName("ancient_relic_choice_assignments")]
        public Dictionary<int, List<string>> AncientRelicChoiceAssignments { get; set; } = new();
        [JsonPropertyName("card_assignments")]
        public Dictionary<int, ApCardAssignmentState> CardAssignments { get; set; } = new();
        [JsonPropertyName("potion_assignments")]
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
        [JsonPropertyName("ascensions")]
        public List<int> Ascensions { get; set; } = new List<int>();
    }

    /// <summary>
    /// Singleplayer persistence envelope. Its progress property is the same canonical state that
    /// multiplayer stores in RitsuLib host run data; only the native save payload is specific to
    /// singleplayer persistence.
    /// </summary>
    public sealed class SerializableAP
    {
        [JsonPropertyName("progress")]
        public ApRunProgressState Progress { get; set; } = new();

        [JsonPropertyName("save_data")]
        // Keep the base-game save opaque to AP's source-generated serializer. The
        // running game must serialize and deserialize this payload with its own
        // MegaCritSerializerContext so public and beta save schemas can differ.
        public JsonElement? SaveData { get; set; }
    }

    [JsonSerializable(typeof(SerializableAP))]
    [JsonSerializable(typeof(ApRunProgressState))]
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
            var defaultProperty = contextType?.GetProperty(
                "Default",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
            );
            var defaultField = contextType?.GetField(
                "Default",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
            );
            LogUtility.Info("Getting Dereferencing Default");
            var megaResolver = (IJsonTypeInfoResolver?)(
                defaultProperty?.GetValue(null) ?? defaultField?.GetValue(null)
            ) ?? throw new InvalidOperationException(
                "Could not resolve MegaCritSerializerContext.Default."
            );

            LogUtility.Info("Getting Options");
            var megaOptions = (megaResolver as JsonSerializerContext)?.Options
                ?? throw new InvalidOperationException(
                    "MegaCritSerializerContext.Default did not expose serializer options."
                );

            CombinedOptions = new JsonSerializerOptions(megaOptions)
            {
                TypeInfoResolver = JsonTypeInfoResolver.Combine(megaResolver, APSerializationContext.Default)
            };
        }

    }
}
