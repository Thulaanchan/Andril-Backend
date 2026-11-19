namespace Andril.AI.Policies;

/// <summary>Controlled text prototype only; no subscription entitlements or deployment configuration.</summary>
public static class AiPrototypeLimits
{
    public const int AllowlistedAccounts = AiOperationPolicy.TesterCount;
    public const int DailyQuota = AiOperationPolicy.DailySlots;
    public const int AccountPerMinute = AiOperationPolicy.AccountPerMinute;
    public const int GlobalPerMinute = AiOperationPolicy.GlobalPerMinute;
    public const int MaxUserTextElements = AiOperationPolicy.MaxTextElements;
    public const int MaxInputTokens = AiConversationContextPolicy.MaxInputTokens;
    public const int MaxOutputTokens = 500;
    public const int DurationDays = 14;
    public static TimeSpan Duration => TimeSpan.FromDays(DurationDays);
    public const decimal BudgetUsd = 5.00m;
    public const decimal FirstAlertUsd = 2.50m;
    public const decimal SecondAlertUsd = 4.00m;
}
