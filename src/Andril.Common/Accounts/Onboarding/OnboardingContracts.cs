namespace Andril.Common.Accounts.Onboarding;

/// <summary>Request-scoped evidence from the successful live identity check, not JWT metadata.</summary>
public interface IOnboardingIdentity
{
    bool IsConfirmedFor(Guid subject);
}

public interface IOnboardingStatusService
{
    Task<OnboardingStatus> GetAsync(CancellationToken cancellationToken = default);
}

public sealed record OnboardingProgress(bool IdentityVerified, bool AccountInitialized,
    bool DemographicsComplete, bool CurrentPoliciesAccepted, bool StarterModuleSelected)
{
    public bool PrivateProfileComplete { get; init; }
}

public sealed record OnboardingStatus(string NextStep, OnboardingProgress Progress,
    string? SelectedModule = null, string? BlockingDependency = null)
{
    // Social private-profile completion does not approve overall onboarding or module entry.
    public bool OnboardingComplete => false;
    public bool ModuleHomeAuthorized => false;
}

public sealed class OnboardingDependencyException : Exception { }
