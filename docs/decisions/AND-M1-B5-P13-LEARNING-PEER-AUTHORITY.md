# Learning peer purpose boundary

Implements the accepted AND-M1-B5-P11-20261128 rules for explicit Exchanges,
Pacts and Connections. Discovery disclosure and mentorship publication are separate.

- Current actor: existing M2 serializable Learning owner callback, current account,
  demographics, policy records, starter membership, lifecycle and profile.
- Targets: real `private_module_profiles` → account bindings, active Learning
  lifecycles, membership, M2 Learning age eligibility and current Terms/Privacy.
  A private backend/transaction/caller/target/purpose scope permits only a definer's
  exact lifecycle SELECT/SHARE lock. Its UPDATE policy has WITH CHECK false.
- Privacy/blocking: existing M5 `module_restrictions` and symmetric `module_blocks`;
  all Pact participant pairs must be unblocked. No Classroom consent is borrowed.
- The existing M5 authority fence is acquired before the serializable snapshot.
  Target row locks use NOWAIT. A 250 ms row-lock timeout avoids reverse waits behind
  another M2/M5 account writer; contention fails closed without automatic replay.
  Session fence/configuration restoration is bounded; damaged connections are cleared.
- Participant-only FORCE RLS checks real current eligibility/blocking on every row.
  Runtime grants are exact SELECT/INSERT and UPDATE(content,status,version), narrow
  helper execution, no identity/scope-table reads, ownership rewrite, DELETE or BYPASSRLS.
- Existing domain transitions, revision checks and unordered-pair Exchange/Connection
  uniqueness remain intact. Pacts retain multiple participants without a new limit.
- `Learning:PeerActivities:Enabled` defaults off, with a separate restricted RuntimeRole.
  Owner and peer compositions share one authenticated API route set and separate stores.
  No migration or flag is activated at application startup.

Ordinary People Discovery still needs explicit disclosure/visibility publication;
mentorship offers need authoritative publication consent and offer bindings. Circle
resource/capability mappings remain unresolved. These paths stay unavailable; none
of their logical approvals is reopened. AI, retention and XP policy holds are unchanged.
