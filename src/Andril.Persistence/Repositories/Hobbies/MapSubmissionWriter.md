# Internal Map submission writer

The writer remains unregistered and has no public endpoint. Prepared submissions come from the existing preflight boundary, using the authorized Hobbies profile, canonical attraction coordinates, completed capture timing, and verified private photo reference. Device coordinates and accuracy remain reported observations. Preparation does not establish GPS authenticity.

Each call creates a dedicated context and READ COMMITTED transaction. A scoped FOR UPDATE lock serializes contenders for one session. Evidence is validated again against the current session and attraction. The existing capture-session unique index and composite scope foreign key protect insertion. Existing contributions are queried by session, owner and attraction. Only the exact named capture-session unique violation receives duplicate resolution after rollback through a fresh context. Other failures produce an infrastructure outcome without record or exception details.

Cancellation before commit propagates. Once commit begins, a database error or cancellation returns OutcomeUnknown because acknowledgement may have been lost. A subsequent authorized attempt queries persisted state. The writer never retries a write after an ambiguous commit.

M2 dependencies include an approved freshness policy for authorization between preparation and writing, and authoritative restriction/revocation checks. The existing access reader resolves the authenticated account, active owned Hobbies profile and attraction eligibility, but exposes no authorization version or transaction fence. Production photo evidence remains deny-all.

Product and M2 must decide capture lifecycle and retention, rejected-contribution replay semantics, and public Already Submitted behavior. Current moderation supports only PendingReview. Existing rows retain session uniqueness regardless of future moderation decisions. This batch introduces no deletion, restoration, rewards or publication.
