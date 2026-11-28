# P15 — exact-offer mentorship request integration

Baseline: `e9813c79ce4444682fb2db8d4614f1971a5be63d`.
Authority: approved `AND-M1-B5-P11-20261128`, existing mentorship domain transitions.

The opt-in peer repository/API now supports mentorship requests. The learner selects
an exact voluntarily published offer belonging to the mentor. Both participants
are bound to real M2 Learning accounts; current lifecycle, policy acceptance,
M5 restrictions/blocking and offer publication are checked before commit/release.
Only the mentor accepts/declines; only the learner cancels, under existing domain rules.
One pending request per unordered pair is enforced by a partial unique index,
including different offers and reversed roles. Terminal records permit fresh intent.
No inferred communication/discovery/AI permission is granted; Growth stays private.

The publication-aware RLS predicate validates exact offer/creator/member bindings.
A purpose-5 eligibility helper uses an inaccessible transaction/backend-bound scope,
with a lifecycle lock-only policy (`WITH CHECK(false)`), never a runtime lifecycle grant.
Mentor-side authorization uses the actual mentor context, not learner impersonation.
Existing M5 authority fencing precedes the serializable snapshot. Publication/lifecycle
locks last through transaction completion; cancellation and denial roll back.
Old migration target models remain immutable; the new Peer V2 migration depends on
the separately owned private activities and publications migrations. Apply those first.
Downgrade refuses existing mentorship rows instead of deleting records.
Runtime grant provisioning must run after schema migrations, using the existing narrow helper.
Production opt-ins remain disabled by default; no hosted migration or live activation.

## Remaining external authorities (unchanged)

- M5: authoritative Learning Circle-to-group resource binding and exact goal
  read/create/edit/progress capability codes. Classroom membership/domain IDs are not substitutes.
- M2/M6: actual AI policy producer and approved current policy version, plus concrete
  transport dispatch/revocation guarantees. Existing mechanisms stay fail-closed; live dispatch disabled.
- M2/M6/Product: authenticated cleanup-worker grant and exact retention/deletion
  eligibility including external stores. Scheduled retention stays disabled.
- Product/M1/M3/M6: XP earning events, amounts, caps and thresholds remain unconfigured.

No additional policy is inferred from this delivery. Batch 6 API/readiness audit follows
the manual merge gate; this scoped delivery does not close the outstanding authorities.
