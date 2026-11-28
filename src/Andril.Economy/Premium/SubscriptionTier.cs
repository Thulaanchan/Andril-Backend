namespace Andril.Economy.Premium;

public enum SubscriptionTier
{
    Free = 0,
    PremiumMonthly = 1,
    PremiumAnnual = 2,
    Lifetime = 3
}

public static class SubscriptionTierExtensions
{
    public static bool IsPremium(this SubscriptionTier tier) =>
        tier is SubscriptionTier.PremiumMonthly
             or SubscriptionTier.PremiumAnnual
             or SubscriptionTier.Lifetime;
}
