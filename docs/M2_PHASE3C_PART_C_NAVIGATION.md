# Phase 3C Part C — Module navigation

Selection is local to a device. The backend evaluates only the requested
destination, with no from-module input and no persisted current-module field.
Every resource handler remains responsible for its own current authorization.

The internal reader derives the subject from verified authentication and uses
the controlled Accounts store to resolve the account. It returns active or
inactive plus revision, or never activated without a revision. Denied or
unavailable responses omit module and revision. A denied or unavailable
authority takes precedence over membership disclosure. The controlled store
uses a read-only repeatable-read transaction for its local database snapshot
and transaction-local scope. This provides a consistent snapshot of those
database rows. External authority and revocation still require a separate
production freshness contract.

## Flutter acceptance contract

- Keep the selected module per device and per signed-in account. Run the
  Learning, Hobbies, and Dating directions independently on two devices.
- Correlate each request with the session/account generation, requested
  canonical module, and navigation request generation. Reject late replies
  after module selection, logout, or account change. Test an earlier active
  reply arriving after a later inactive result.
- Clear sensitive screens and caches on logout/account change. Scope cached
  private data by account and module, and invalidate stale back-stack entries
  when the scoped membership or independent authorization changes.
- Deep links and restored routes must request fresh backend evidence for the
  destination and separately clear the protected operation. Inactive,
  never-activated, denied, and unavailable outcomes cannot open protected
  routes or implicitly activate, reactivate, or restore permissions.
- Activation, reactivation, or limited setup screens are proposed UI outcomes
  requiring product approval. Navigation success alone cannot open Home or
  authorize access to an additional module profile.
- Treat unavailable responses as retryable UI errors without leaking policy,
  restriction, other account, or private profile data.

These are frontend acceptance requirements. The backend tests do not execute
Flutter widgets, cache code, or route transitions.

## Part D review

Candidate caller-scoped `GET /api/v1/account/modules` remains unpublished.
Review the per-module status schema, bearer confirmation and rate limit
behavior, no-store policy, and denied or unavailable projection before
registration. Exclude private profiles, identifiers, policy evidence, and
permissions. Preserve the existing starter-only profile check until a reviewed
lifecycle-aware profile contract independently checks owned module identity,
current standing and consent, profile purpose, and route-specific permissions.
M6 must clear write-time and read-time revocation semantics, RLS and pooled
scope verification, and downstream authorization. Home remains fail-closed.
