using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace StS2AP.DomainAdapters;

/// <summary>
/// Verifies independently generated offers. This carries no card payload or instructions to
/// mutate relics: each replica must already have the same cards and native player state.
/// </summary>
internal static class ApCardRevealCodec
{
    private const int Version = 2;
    private const int DigestWords = 8;

    internal static List<int> Encode(ApMirroredRewardSpec spec, string before, string after, bool firstReveal = true)
    {
        if (spec.Kind != ApMirroredRewardKind.Card || !spec.CardHasBeenRevealed
            || spec.SerializedModels.Count == 0 || spec.AppliedEffects.Count != 0
            || spec.MaterializationStrategyId != "ap_rng_replicated_card_v1")
            throw new InvalidOperationException("Cannot verify an unfinished or non-replicated AP card offer.");
        _ = MirroredRewardAdapter.Decode(spec, 3);
        // Parse model/state JSON so object property order is irrelevant; array order remains
        // significant for card indexes, relic hook order, deck order, and RNG state.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            spec.ApSlotId, spec.ReceivedItemIndex, spec.OwnerNetId,
            spec.IsRareCardReward, spec.CardRewardActIndex, spec.CardCanReroll, FirstReveal = firstReveal,
            Cards = spec.SerializedModels.Select(model => JsonSerializer.Deserialize<JsonElement>(model)),
            Before = JsonSerializer.Deserialize<JsonElement>(before),
            After = JsonSerializer.Deserialize<JsonElement>(after),
        }));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, document.RootElement);
        byte[] digest = SHA256.HashData(stream.ToArray());
        var result = new List<int> { Version };
        for (int offset = 0; offset < digest.Length; offset += sizeof(int))
            result.Add(BinaryPrimitives.ReadInt32LittleEndian(digest.AsSpan(offset, sizeof(int))));
        return result;
    }

    internal static void Verify(IReadOnlyList<int> local, IReadOnlyList<int> owner, string receipt)
    {
        if (local.Count != DigestWords + 1 || owner.Count != DigestWords + 1
            || local[0] != Version || owner[0] != Version || !local.SequenceEqual(owner))
            throw new InvalidOperationException($"Replicated AP card offer {receipt} disagreed with the owner "
                + "(receipt, cards, or native player state). No picker choice was applied.");
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement element in value.EnumerateArray())
                    WriteCanonical(writer, element);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
