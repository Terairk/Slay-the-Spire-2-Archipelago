using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;

namespace StS2AP.Utils;

/// <summary>
/// Keeps an AP receipt's revealed choices stable independently of the menu displaying them.
/// Register the assignment when it is first revealed or restored, before handing it to the UI.
/// </summary>
internal static class ApCardRewardLifecycle
{
    private static readonly FieldInfo CardsField = AccessTools.Field(typeof(CardReward), "_cards")
        ?? throw new MissingFieldException(typeof(CardReward).FullName, "_cards");
    private static readonly MethodInfo RelicObtainedMethod = AccessTools.Method(typeof(CardReward), "OnRelicObtained")
        ?? throw new MissingMethodException(typeof(CardReward).FullName, "OnRelicObtained");

    internal static void Freeze(CardReward reward)
    {
        // Native OnRelicObtained calls AfterModifyingRewards, not the card-option callback which
        // consumes Silken Tress / Silver Crucible. AP applies those effects on first reveal instead.
        // Unsubscribe rather than suppressing callbacks so abandoned menu rows are not kept alive
        // by the player. Removing an already absent subscription is harmless.
        reward.Player.RelicObtained -= RelicObtainedMethod.CreateDelegate<Action<RelicModel>>(reward);
    }

    /// <summary>
    /// Copy the option records, retaining native modifier provenance as well as upgraded/enchanted
    /// cards. Also retain a spent reroll when a player skips and returns to the receipt later.
    /// </summary>
    internal static void CopyOptions(CardReward source, CardReward destination)
    {
        if (ReferenceEquals(source, destination))
            return;
        var sourceCards = (List<CardCreationResult>)CardsField.GetValue(source)!;
        var destinationCards = (List<CardCreationResult>)CardsField.GetValue(destination)!;
        destinationCards.Clear();
        destinationCards.AddRange(sourceCards);
        destination.CanReroll = source.CanReroll;
    }

}
