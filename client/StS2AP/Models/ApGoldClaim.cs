namespace StS2AP.Models;

/// <summary>
/// Immutable aggregate gold claim materialized by one AP reward-menu button.
/// The raw source and owner-private cursor never leave the owning AP process;
/// only <see cref="GrantedAmount"/> is replicated through MegaCrit's synchronizer.
/// </summary>
public sealed record ApGoldClaim(
    int SourceAmount,
    int GrantedAmount,
    int RedeemedRawAfter);
