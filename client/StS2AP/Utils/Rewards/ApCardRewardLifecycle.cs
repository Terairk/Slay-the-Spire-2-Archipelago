using MegaCrit.Sts2.Core.Rewards;

namespace StS2AP.Utils;

/// <summary>Shared AP card lifecycle operations, using the publicized game API.</summary>
internal static class ApCardRewardLifecycle
{
    // Disable retroactive relic-pickup updates; normal generation hooks still run on first reveal.
    // Detaching also lets discarded menu rows be collected instead of retained by the player event.
    internal static void Freeze(CardReward reward) =>
        reward.Player.RelicObtained -= reward.OnRelicObtained;

    internal static void CopyOptions(CardReward source, CardReward destination)
    {
        if (ReferenceEquals(source, destination))
            return;
        destination._cards.Clear();
        destination._cards.AddRange(source._cards);
        destination.CanReroll = source.CanReroll;
    }
}
