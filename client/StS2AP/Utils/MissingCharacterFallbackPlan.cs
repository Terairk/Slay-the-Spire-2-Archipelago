using StS2AP.Data;

namespace StS2AP.Utils;

/// <summary>
/// Decides how one installed vanilla character stands in for slot characters whose mods are not
/// loaded, so a broken or missing character mod never locks a player out of the slot.
///
/// The fallback character plays with its own slot configuration when the slot has one; otherwise
/// it borrows the configuration (items, ascension, seed) of the first missing character. Either
/// way, its location checks and victories also count for every missing character.
///
/// The plan is derived from the installed characters each time slot settings are prepared and is
/// never saved, so installing or removing character mods takes effect on the next connection.
/// </summary>
internal sealed class MissingCharacterFallbackPlan
{
    public readonly record struct SlotCharacter(string Id, long Number);

    public static readonly MissingCharacterFallbackPlan None =
        new(string.Empty, Array.Empty<SlotCharacter>(), null, null);

    private readonly HashSet<long> _missingNumbers;

    private MissingCharacterFallbackPlan(
        string fallbackCharacterId,
        IReadOnlyList<SlotCharacter> missingCharacters,
        SlotCharacter? borrowedCharacter,
        long? fallbackCharacterNumber)
    {
        FallbackCharacterId = fallbackCharacterId;
        MissingCharacters = missingCharacters;
        BorrowedCharacter = borrowedCharacter;
        FallbackCharacterNumber = fallbackCharacterNumber;
        _missingNumbers = missingCharacters.Select(character => character.Number).ToHashSet();
    }

    /// <summary>The game character ID that stands in for the missing characters.</summary>
    public string FallbackCharacterId { get; }

    /// <summary>Configured characters that are not installed, in AP character-number order.</summary>
    public IReadOnlyList<SlotCharacter> MissingCharacters { get; }

    /// <summary>
    /// The missing character whose slot configuration the fallback character plays with, or
    /// null when the fallback character has its own configuration in this slot.
    /// </summary>
    public SlotCharacter? BorrowedCharacter { get; }

    /// <summary>The AP character number whose locations the fallback character's runs produce.</summary>
    public long? FallbackCharacterNumber { get; }

    public bool IsActive => MissingCharacters.Count > 0;

    public static MissingCharacterFallbackPlan Create(
        IEnumerable<SlotCharacter> configuredCharacters,
        Func<string, bool> isInstalled,
        string fallbackCharacterId)
    {
        SlotCharacter[] configured = configuredCharacters.ToArray();
        SlotCharacter[] missing = configured
            .Where(character => string.IsNullOrWhiteSpace(character.Id) || !isInstalled(character.Id))
            .OrderBy(character => character.Number)
            .ToArray();
        if (missing.Length == 0)
            return None;

        SlotCharacter? ownConfiguration = configured
            .Where(character => string.Equals(
                character.Id, fallbackCharacterId, StringComparison.OrdinalIgnoreCase))
            .Select(character => (SlotCharacter?)character)
            .FirstOrDefault();
        SlotCharacter? borrowed = ownConfiguration == null ? missing[0] : null;
        long fallbackNumber = (ownConfiguration ?? missing[0]).Number;

        return new MissingCharacterFallbackPlan(fallbackCharacterId, missing, borrowed, fallbackNumber);
    }

    public bool IsMissing(long characterNumber) => _missingNumbers.Contains(characterNumber);

    /// <summary>
    /// Returns the requested locations plus, for each one the fallback character earned, the
    /// same location in every missing character's block.
    /// </summary>
    public IEnumerable<long> WithMirroredLocations(IEnumerable<long> locationIds)
    {
        foreach (long locationId in locationIds)
        {
            yield return locationId;
            if (!IsActive
                || ArchipelagoIdCodec.IsArchipelagoReservedId(locationId)
                || ArchipelagoIdCodec.GetAPCharacterNumberFromLocationId(locationId) != FallbackCharacterNumber)
            {
                continue;
            }

            long baseLocationId = ArchipelagoIdCodec.GetBaseLocationId(locationId);
            foreach (SlotCharacter missing in MissingCharacters)
            {
                if (missing.Number != FallbackCharacterNumber
                    && ArchipelagoIdCodec.TryComposeLocationId(baseLocationId, missing.Number, out long mirrored))
                {
                    yield return mirrored;
                }
            }
        }
    }

    /// <summary>
    /// Returns the slot characters credited when <paramref name="playedCharacterId"/> wins a run.
    /// A borrowed fallback is not a slot character itself, so only the missing characters count.
    /// </summary>
    public IReadOnlyList<string> GoalCharacterIds(string playedCharacterId)
    {
        if (!IsActive
            || !string.Equals(playedCharacterId, FallbackCharacterId, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { playedCharacterId };
        }

        var credited = new List<string>();
        if (BorrowedCharacter == null)
            credited.Add(playedCharacterId);
        credited.AddRange(MissingCharacters
            .Select(character => character.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id)));
        return credited;
    }
}
