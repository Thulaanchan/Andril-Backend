# TD-04 isolated operation journal

Authority: AND-M1-B310-CROSS-MEMBER-ACCESS-20261126; approved TC-04 behavior.
This records a new M1 technical implementation for cross-owner review, not a
claim that an existing M2/provider dispatch protocol supplies these guarantees.

## Implemented boundary

The unregistered `IsolatedLearningAiDispatchOperationRepository` uses the existing
isolated serializable Account → Membership → Profile boundary. It shares that
boundary with acknowledgement writes/revocation; no independent lock namespace
is introduced. It binds each operation to the exact authoritative account,
Learning profile, acknowledgement generation/version and current policy version.
Full privacy-gate authorization is independently required for preparation,
claiming and release validation; acknowledgement alone is insufficient.

The journal stores only operation state and authority bindings, never prompts,
responses, private tool results, credentials or a provider selection. Its new
interface is `IAiDispatchOperationRepository`.

State transitions are deliberately conservative:

- Missing → Prepared: only with a current acknowledged revision, independently
  satisfied privacy gate and authorized owner. Same preparation may be read back
  without creating a second operation.
- Prepared → Claimed: one successful serialized claim; a repeat cannot claim again.
- Prepared/Claimed → Cancelled: terminal cancellation, available to the authorized
  owner even if privacy acknowledgement has since been revoked.
- Claimed → Indeterminate: records an uncertain external outcome conservatively.
  No automatic replay, cancellation-to-hide-uncertainty or transition back to
  Prepared is exposed.
- Reconciliation reads the persisted owner-scoped state; it does not infer a
  provider outcome or retry an operation.

Claims and release validation require the same acknowledgement generation/version
as preparation. Revoke/re-acknowledge cannot revive an old operation. Current
privacy and policy are checked again at transaction commit/read release. Restricted
runtime updates are limited to state/time; ownership and captured authority
bindings are immutable through the runtime update grants. FORCE RLS remains on.

Independent-connection tests order revocation before a waiting claim and a claim
before a waiting revocation. They verify the resulting committed state and deny
later release after revocation, without assuming thread timing closes a race.

## Guarantees explicitly not supplied

**A Claimed marker is not transport permission.** This component never invokes a
provider. It does not solve revocation between claim and actual external send,
retract previously sent data, or guarantee atomicity between database commit and
an external provider. Provider dispatch remains disabled.

`ValidateForReleaseAsync` validates journal/authority state inside a coordinated
read; it is not an atomic HTTP response or conversation-persistence operation.
An independently committed conversation write after it would be a TOCTOU defect,
not an approved integration pattern. `CompleteConversationResultAsync` now provides
the joined database command: one assistant message for an existing same-operation
user message and Claimed → Completed journal state are saved in the same transaction.
Current privacy, policy, acknowledgement revision, lifecycle, conversation ownership
and expiry are revalidated before commit. Both writes roll back together on denial,
cancellation or failure; assistant saves do not renew expiry. Completed is terminal,
and duplicate completion conflicts rather than replaying. The command returns only
sanitized operation status, never reply text or transport permission.

An ambiguous commit still returns CommitUnknown through the existing transaction
boundary; the caller must reconcile the persisted state without automatic replay.
Reply delivery remains a separately coordinated release operation. The future
transport fence must specify actual send/revoke linearization before provider
dispatch can be activated. No provider selection or live execution is introduced.

Indeterminate state is a conservative record, not a complete reconciliation of an
ambiguous provider send. The existing boundary reports unknown commits without
automatic replay. No synthetic provider test is represented as proof of a real
transport guarantee.

The table is added only to the existing disposable `andril_ai_isolated` model.
No production migration/history, retention or deletion policy, operational worker,
HTTP route or DI registration is introduced. The existing isolated connection
guard is preserved. Full TD-04, production privacy integration and B3-10 remain
incomplete until those remaining acceptance gates pass.
