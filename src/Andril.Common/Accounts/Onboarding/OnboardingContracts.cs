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
    public bool PrivateProfileComplete => false;
}

public sealed record OnboardingStatus(string NextStep, OnboardingProgress Progress,
    string? SelectedModule = null, string? BlockingDependency = null)
{
    // There is no authoritative private profile implementation in this repository yet.
    public bool OnboardingComplete => false;
    public bool ModuleHomeAuthorized => false;
}

public sealed class OnboardingDependencyException : Exception { }
