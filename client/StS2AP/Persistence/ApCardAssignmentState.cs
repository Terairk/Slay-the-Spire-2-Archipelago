using System.Text.Json.Serialization;

namespace StS2AP.Persistence;

public sealed class ApCardAssignmentState
{
    [JsonPropertyName("serialized_cards")]
    public List<string> SerializedCards { get; set; } = new();

    [JsonPropertyName("can_reroll")]
    public bool CanReroll { get; set; }
}
