namespace Andril.Common.Accounts.Policies;

public static class PolicyActions
{
    public const string TermsAgreed = "terms_agreed";
    public const string PrivacyAcknowledged = "privacy_acknowledged";
}

public sealed record PolicyDocument(string Version, string Url, string Language);
public sealed record PolicyCatalog(PolicyDocument Terms, PolicyDocument Privacy, PolicyDocument CommunityGuidelines);
public sealed record PolicyActionSubmission(string? Action, string? Version);
public sealed record PolicyReceipt(string Action, string Version, DateTimeOffset RecordedAtUtc);
public sealed record PolicyStatus(PolicyCatalog Documents, bool TermsAgreed, bool PrivacyAcknowledged,
    bool RequiredActionsComplete, IReadOnlyList<PolicyReceipt> History);

public interface IPolicyCatalogProvider { PolicyCatalog GetCurrent(); }
public interface IAccountPolicyRepository
{
    Task<IReadOnlyList<AccountPolicyRecord>> ReadAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task AppendAsync(Guid accountId, IReadOnlyList<PolicyActionSubmission> actions, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
public interface IAccountPolicyService
{
    Task<PolicyStatus> GetAsync(CancellationToken cancellationToken = default);
    Task<PolicyStatus> AcknowledgeAsync(IReadOnlyList<PolicyActionSubmission>? actions, CancellationToken cancellationToken = default);
}
public sealed class PolicyValidationException : Exception { }
public sealed class PolicyVersionConflictException : Exception { }
public sealed class PolicyConfigurationException : Exception { }
public sealed class PolicyAccountMissingException : Exception { }
