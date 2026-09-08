using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace StS2AP.Utils;

/// <summary>
/// A native card-reward row whose assignment is resolved only when its picker opens.
/// The caller owns receipt storage and, for multiplayer, synchronized assignment resolution.
/// </summary>
internal abstract class ApDeferredCardReward : CardReward
{
    private CardReward? _assignment;

    protected ApDeferredCardReward(CardCreationOptions options, Player player)
        : base(options, 3, player)
    {
        ApCardRewardLifecycle.Freeze(this);
    }

    // A ready menu row does not imply that its hidden card choices have been generated.
    public override bool IsPopulated => true;

    public override void Populate()
    {
        // Keep native rerolls available after this picker has opened.
        if (_assignment != null)
            base.Populate();
    }

    protected abstract Task<CardReward?> ResolveAssignment();

    // Multiplayer may bind an already-reserved first choice before entering the native picker.
    protected virtual Task<bool> SelectCards() => base.OnSelect();

    protected override async Task<bool> OnSelect()
    {
        CardReward? assignment = await ResolveAssignment();
        if (assignment == null)
            return false;

        ApCardRewardLifecycle.Freeze(assignment);
        _assignment = assignment;
        ApCardRewardLifecycle.CopyOptions(assignment, this);
        bool applied = await SelectCards();
        if (!applied)
            ApCardRewardLifecycle.CopyOptions(this, assignment);
        return applied;
    }
}
