# Coordinated dispatch application boundary

Authority: confirmed TC-04 and cross-member implementation approval. This is a
new application integration contract, not a claim that an external provider or
M2 revocation authority already implements the required transport protocol.

`AiCoordinatedConversationResultService` validates the existing 500-character
submission contract, resolves the current Learning actor/privacy authority,
checks owned conversation access, reads authoritative acknowledgement evidence,
prepares and claims the existing journal, invokes a required transport boundary,
and calls the delivered atomic conversation-result command. No client account,
profile, consent, provider choice or tier entitlement is accepted. This service
is unregistered and returns only safe status, not private reply text.

The new `IAiProviderDispatchBoundary` is responsible for actual applicable
session/lifecycle/privacy/admission and dispatch/revocation linearization. It
must cover the claim-to-send race, cancellation and unknown-send evidence, invoke
the callback at most once and hold no database locks across that callback. A
journal claim or final authorization recheck is not an implementation of this
contract. The explicit default `UnavailableAiProviderDispatchBoundary` never
invokes a provider. No denial-only default is claimed as completed dispatch.

The application checks that a Sent receipt follows exactly one completed
callback; inconsistent receipts cannot persist results. A second callback is
rejected before a second provider execution. Provider failures or cancellation
after invocation mark the existing journal Indeterminate without automatic
replay; cancellation propagates. Transport denial/unavailability before any
invocation cancels the operation. A fresh atomic persistence command revalidates
authority and acknowledgement before saving reply+Completed state. No database
transaction is held while calling the provider; deterministic tests revoke
acknowledgement from a separate database operation during that call.

Successful isolated tests use a clearly synthetic transport authority and
provider with real PostgreSQL journal/acknowledgement/conversation operations.
They exercise dispatch decisions, safe failure, callback nonreplay, revocation,
cancellation and controlled HTTP integration. They do NOT prove a production
transport fence, external send/revoke race closure, remote unknown-send
reconciliation or legally sufficient processing consent. No current privacy
policy wording, quota, provider, retention or deletion policy is invented.

Concrete production transport authority, schema/cleanup, source policy ingestion,
composition-root activation and full B3-10 functional acceptance remain pending.
Existing journal, privacy guards and isolated database protections remain in
force. No hosted migrations or live provider calls are authorized by these tests.
