# Batch 5 P05 — purpose-bound Learning eligibility

Authority: explicit M1-B5-P05 implementation instruction and standing M2/M5/M6
cross-member clearance. Existing business, privacy, TC/TD and lifecycle decisions
remain unchanged. No new XP values, retention policy or AI permission is inferred.

## Source-backed integration

The retained PostgreSQL diagnostic proved the defect: an active group creator's
M2 lifecycle row was invisible under a different caller's tenant scope. M5's
`profile_is_active` therefore returned false even for an eligible public Classroom.
Changing the caller to the creator, disabling FORCE RLS or adding runtime table
grants would not be an acceptable correction.

The new Groups migration supplies the private boolean reader
`andril_groups.learning_group_target_eligible(candidate, group_id)`. The actual
caller still comes from the verified M2 account/subject/module transaction. For a
caller search it considers only that caller and the actual Classroom creator.
For a host's admission decision, a different candidate additionally requires the
caller's active host capability and the candidate's real active membership or
pending request in that same Classroom. It never accepts an arbitrary target as
an authenticated actor. Existing public operation, visibility, membership and
admission checks remain in the enclosing M5 functions; the reader is a necessary
eligibility check, not an independent operation permission.

The reader reuses current M5 restrictions, both-direction blocking and host facts.
It reads actual M2 Learning profile, account, starter membership, active lifecycle
and the existing Learning age rule (16), returning only a boolean internally.
The shared `can_interact_with_hosts` Learning branch reuses it for discovery,
participation and authorized admission. Other modules' existing branch is retained.
No private profile fields or eligibility rows become an HTTP response.

## Narrow RLS and transaction scope

The private `andril_groups.learning_eligibility_read_scopes` table has no PUBLIC
or runtime grants and retains ENABLE/FORCE RLS. A definer creates an exact,
short-lived caller/group/target scope only after checking the source bindings.
It binds backend PID, full transaction ID, account, subject, profile, purpose and
Learning module. Target IDs alone or caller-written GUCs cannot create that proof.
The row is consumed/deleted before returning; cancellation/error rollback removes
it as part of the transaction. There is no TTL or retained user content.

M2's original lifecycle policy remains. The additional definer-owner SELECT policy
permits only the exact proven target row in that backend/transaction/purpose. A
matching UPDATE USING policy is necessary for PostgreSQL `SELECT FOR SHARE`;
its `WITH CHECK (false)` rejects actual cross-owner lifecycle modifications.
No INSERT/DELETE policy or runtime UPDATE/SELECT grant is introduced. The private
function is revoked from PUBLIC and is not granted to runtime roles.

Conditional shared row locks cover the actual target account, membership,
demographics, lifecycle and profile until the enclosing transaction/read-release
finishes. Targets are ordered by account/profile. Conditional target locks use
NOWAIT: if an existing operation already owns a conflicting parent lock, the
reader fails unavailable rather than waiting in a reversed participant order.
The existing M5 resource fence still coordinates blocking and group changes;
no independent lock namespace or automatic retry is introduced. An operation
already ahead at an existing fence may queue the read; fresh eligibility is
required after that operation commits. No database lock crosses a provider call.

Reference: PostgreSQL's [policy command matrix](https://www.postgresql.org/docs/17/sql-createpolicy.html)
and [row-security locking guidance](https://www.postgresql.org/docs/17/ddl-rowsecurity.html).

## Production composition and acceptance

`Learning:Classrooms:Enabled` is explicitly default-off. Enabling it requires
`SharedGroups:EnablePersistence` and `SharedGroups:EnableLearning`, the existing
M5 verified repository/runtime-role composition and applied migrations. The
read-only readiness check additionally rejects superuser/BYPASSRLS definer owners,
missing scoped policies, public private-reader access and missing FORCE RLS.
It neither migrates nor schedules cleanup or activates a provider.

The protected existing-contract GET `/api/learning/classrooms` is bound to the
server's Learning identity, confirmed-account middleware and no-store responses.
Only ordinary group cards (ID, module, kind, name) leave the source. No caller
profile/module query parameter changes the actor or scope, no joins occur on read,
and exceptions become confidential unavailable responses.

Tests use a cryptographically credentialed, temporary non-superuser/NOBYPASSRLS
migration/provisioning owner even when CI starts with a PostgreSQL administrator.
That owner is separate from the restricted runtime role. The override remains
loopback/postgres validated and does not mutate process-wide connection settings.
All temporary databases, runtime/provisioner roles and the test owner are disposed.
The eleven required cases are added to the existing M5 PostgreSQL CI manifest path;
no prior required cases are removed. New acceptance includes actual HTTP discovery,
blocking/lifecycle revocation, independent-connection read/revocation ordering,
private function/table denial, cross-owner UPDATE denial, host admission reuse,
existing-data preservation and migration rollback/reapplication.

## Remaining holds

This authorizes ordinary M5-backed Learning Classroom interactions only. It is not
AI-specific name disclosure, a general-purpose profile directory, or authorization
for Skill Exchanges, Pacts, mentorship, Circles or Connections without their exact
privacy/resource/relationship contracts. Learning document runtime wiring retains
its existing guards. AI processing-policy ingestion and concrete transport/revocation
guarantees, authenticated cleanup/retention eligibility and approved XP earning
policies remain fail-closed. No hosted migration, deployment or live provider is
performed, and this scope does not complete B3-10 or Batch 5.
