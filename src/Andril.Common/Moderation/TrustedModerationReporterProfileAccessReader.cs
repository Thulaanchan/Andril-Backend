using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;

namespace Andril.Common.Moderation;

/// <summary>
/// Offline-only reporter identity authorization adapter.
///
/// Uses M2's authoritative account lookup and validated
/// read-only private-profile service.
///
/// NOT registered in production DI.
///
/// Account restriction and deactivation policy remains
/// pending M2/M6 approval. This adapter alone is not
/// sufficient for production authorization.
/// </summary>
public sealed class TrustedModerationReporterProfileAccessReader
    : IModerationReportReporterProfileAccessReader
{
    private readonly ICurrentUser _user;
    private readonly IOnboardingIdentity _identity;
    private readonly IAccountRepository _accounts;
    private readonly IPrivateProfileService _profiles;

    public TrustedModerationReporterProfileAccessReader(
        ICurrentUser user,
        IOnboardingIdentity identity,
        IAccountRepository accounts,
        IPrivateProfileService profiles)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public async Task<bool> IsAuthorizedReporterAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (verifiedAccountId == Guid.Empty ||
            report is null ||
            report.ReporterModuleProfileId == Guid.Empty)
        {
            return false;
        }

        // Never interpret a client-provided profile ID
        // as evidence of account ownership.
        if (!_user.IsAuthenticated ||
            _user.UserId is not Guid subject ||
            subject == Guid.Empty ||
            !_identity.IsConfirmedFor(subject))
        {
            return false;
        }

        // Read an existing authoritative account only.
        // Never create accounts during authorization.
        var account = await _accounts.FindBySubjectAsync(
            subject,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (account is null ||
            account.Id != verifiedAccountId ||
            !IsCurrentIdentity(subject))
        {
            return false;
        }

        try
        {
            // M2 performs membership, age/opt-in
            // and applicable validated-profile checks.
            //
            // This is GetAsync, not CreateAsync.
            var profile = await _profiles.GetAsync(
                report.ModuleScope,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (profile is null || !IsCurrentIdentity(subject))
                return false;

            // Exact scope and identity comparison.
            // Profile completeness is deliberately
            // NOT treated as reporter eligibility.
            return profile.ProfileId ==
                       report.ReporterModuleProfileId
                && string.Equals(
                    profile.Module,
                    report.ModuleScope,
                    StringComparison.Ordinal);
        }
        catch (AccountAuthenticationRequiredException)
        {
            return false;
        }
        catch (OnboardingDependencyException)
        {
            return false;
        }
        catch (ProfileMissingException)
        {
            return false;
        }
        catch (ProfileAccessDeniedException)
        {
            return false;
        }
        catch (ProfileValidationException)
        {
            return false;
        }
        catch (StarterModuleAccountMissingException)
        {
            return false;
        }
        catch (StarterModuleIneligibleException)
        {
            return false;
        }

        // Cancellation, timeouts, database failures
        // and unexpected errors propagate.
        // They must never produce authorization success.
    }

    private bool IsCurrentIdentity(Guid subject) =>
        _user.IsAuthenticated &&
        _user.UserId == subject &&
        _identity.IsConfirmedFor(subject);
}
