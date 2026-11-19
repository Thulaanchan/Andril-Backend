namespace Andril.Common.Authentication;

// Only construct from the authenticated Supabase /user response, never JWT user_metadata or client input.
public sealed record ConfirmedUserEvidence(Guid Subject, bool IsAnonymous, bool HasEmail, DateTimeOffset? EmailConfirmedAt)
{
    public bool IsConfirmedFor(Guid expectedSubject, DateTimeOffset now) =>
        expectedSubject != Guid.Empty && Subject == expectedSubject && !IsAnonymous && HasEmail
        && EmailConfirmedAt is { } confirmed && confirmed > DateTimeOffset.MinValue && confirmed <= now;
}
