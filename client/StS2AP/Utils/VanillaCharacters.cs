using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace StS2AP.Utils;

/// <summary>
/// Identifies the base game's own characters by the assembly that defines them instead of by
/// name, so a character added in a newer game version (or only on the beta branch) is treated
/// like the original ones without listing it here.
/// </summary>
internal static class VanillaCharacters
{
    private static readonly Assembly GameAssembly = typeof(CharacterModel).Assembly;

    private static HashSet<CardPoolModel>? _cardPools;

    /// <summary>Whether the game itself defines this model, rather than a mod.</summary>
    public static bool IsVanilla(AbstractModel model) => model.GetType().Assembly == GameAssembly;

    /// <summary>The playable base-game characters, in the game's registration order.</summary>
    public static IEnumerable<CharacterModel> All =>
        ModelDb.AllCharacters.Where(character => character.IsPlayable && IsVanilla(character));

    public static bool IsCharacterCardPool(CardPoolModel pool) =>
        (_cardPools ??= All.Select(character => character.CardPool).ToHashSet()).Contains(pool);
}
