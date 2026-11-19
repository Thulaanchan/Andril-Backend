namespace Andril.Common.Accounts.Policies;

public sealed class AccountPolicyRecord
{
    private AccountPolicyRecord() { }
    public Guid AccountId { get; private set; }
    public string Action { get; private set; } = "";
    public string Version { get; private set; } = "";
    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static bool ValidVersion(string? version) => !string.IsNullOrWhiteSpace(version)
        && version.Length <= 200 && version == version.Trim() && !version.Any(char.IsControl);

    public static AccountPolicyRecord Create(Guid accountId, string? action, string? version, DateTimeOffset now)
    {
        if (accountId == Guid.Empty || action is not (PolicyActions.TermsAgreed or PolicyActions.PrivacyAcknowledged)
            || !ValidVersion(version) || now == DateTimeOffset.MinValue || now == DateTimeOffset.MaxValue)
            throw new PolicyValidationException();
        return new() { AccountId = accountId, Action = action, Version = version!, RecordedAtUtc = now.ToUniversalTime() };
    }
}
