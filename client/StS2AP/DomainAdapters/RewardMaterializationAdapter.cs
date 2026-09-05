using StS2AP.Domain;

namespace StS2AP.DomainAdapters;

/// <summary>Maps typed decode failures to the existing reward-menu diagnostic contract.</summary>
internal static class RewardMaterializationAdapter
{
    public static RewardMaterialization Decode(string strategyId, bool requiresNativeMaterialization,
        string receiptIdentity)
    {
        var decoded = RewardMaterialization.Decode(strategyId, requiresNativeMaterialization);
        if (decoded.IsOk)
            return decoded.ResultValue;

        throw decoded.ErrorValue.Match(
            _ => new InvalidOperationException(
                $"AP reward {receiptIdentity} used an unknown materialization strategy."),
            () => new InvalidOperationException(
                $"AP reward {receiptIdentity} had an inconsistent materialization contract."));
    }
}
