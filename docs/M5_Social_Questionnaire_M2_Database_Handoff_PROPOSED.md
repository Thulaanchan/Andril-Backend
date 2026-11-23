# ANDRIL M5 — SOCIAL QUESTIONNAIRE M2 DATABASE HANDOFF & IMPLEMENTATION DELEGATION
## Database Persistence Architecture, Entity Schema, Concurrency & Ownership Delegation

> **STATUS: DELEGATED IMPLEMENTATION SPECIFICATION / PRODUCTION STAGING**  
> **Author:** M5 (Thulaanchan — Social Domain & Matching Lead)  
> **Collaborating Owners:** M2 (Vizhan — Identity, Privacy & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Document Version:** 1.2.0  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-20, Decision P-04  

---

## 1. Executive Summary & Ownership Delegation Boundary

Following cross-member coordination for the ANDRIL M5 production workstream (Tasks 91–110), M2 has formally **delegated database implementation responsibility** for the isolated Social module persistence layer to M5.

### Explicit Architectural & Governance Boundaries:
- **Delegated M5 Database Scope:** M5 is authorized to author the dedicated `SocialDbContext`, EF Core entity configurations, design-time factory, isolated PostgreSQL migrations (`src/Andril.Persistence/Migrations/Social/`), concrete repository implementations (`IQuestionnairePersistenceRepository`), and automated integration tests in `Andril.IntegrationTests`.
- **Preserved M2 Authority:** M2 retains exclusive governance and ownership over the shared identity schema (`andril_identity`), account models (`AccountsDbContext`, `Account`, `PrivateModuleProfile`), cross-module referential integrity, and master database integration.
- **Preserved M6 Security & Release Controls:** M6 retains authoritative review and sign-off over identity access boundaries, multi-tenant isolation, error handling, and production release gates.
- **No Unilateral Policy Approval:** M5 does not invent or assume approval of pending business decisions (specifically BRD v2.4-R1 Decision P-04).
- **No Live Production Deployment:** Source implementation and passing CI tests do not authorize or execute live migrations against production Supabase/PostgreSQL instances. Live application requires separate verified release authorization.

---

## 2. Domain Entities & Invariant Specifications

The domain entities reside in `src/Andril.Social/Entities/`:

### 2.1 `QuestionnaireResponseEntity`
- **Class:** `Andril.Social.Entities.QuestionnaireResponseEntity`
- **Properties:**
  - `Id` (`Guid`): Immutable primary identifier of the response snapshot.
  - `ProfileId` (`Guid`): Identifier of the owning Social profile scope.
  - `Version` (`string`): Questionnaire definition version (e.g., `"v1"`).
  - `Status` (`QuestionnaireParticipationStatus`): Enumeration (`NotStarted`, `InProgress`, `Completed`, `Skipped`).
  - `CreatedAtUtc` (`DateTimeOffset`): Explicit UTC timestamp when the response was submitted.
  - `Answers` (`IReadOnlyList<QuestionnaireAnswerEntity>`): Associated collection of question answers.
- **Factory:** Instantiation is governed by `QuestionnaireResponseEntity.Create(...)`.

### 2.2 `QuestionnaireAnswerEntity`
- **Class:** `Andril.Social.Entities.QuestionnaireAnswerEntity`
- **Properties:**
  - `Id` (`Guid`): Primary identifier of the individual answer record.
  - `ResponseId` (`Guid`): Foreign key referencing the parent `QuestionnaireResponseEntity.Id`.
  - `QuestionId` (`string`): Identifier of the question (e.g., `"q01"`, max 128 characters).
  - `OptionId` (`string`): Identifier of the selected option (e.g., `"1"`, max 128 characters).
  - `AnsweredAtUtc` (`DateTimeOffset`): Explicit UTC timestamp of response capture.

### 2.3 Critical Privacy Invariants
1. **Strict Social Isolation:** Questionnaire responses belong exclusively to the private Social profile. They must never be linked or leaked to other modules (Learning, Hobbies, Economy).
2. **Hidden From Public Exploration:** Raw question answers and Likert-scale choices are strictly confidential. Under no circumstances may raw answers be serialized into public candidate discovery cards, search feeds, or unauthenticated responses.
3. **Redacted Diagnostics:** Raw answers and option selections must never be logged in plain text. `ToString()` implementations omit answer values.

---

## 3. Persistence Repository Contract

The contract is defined in `src/Andril.Social/Interfaces/IQuestionnairePersistenceRepository.cs`:

```csharp
namespace Andril.Social.Interfaces;

public interface IQuestionnairePersistenceRepository
{
    Task<QuestionnaireResponseEntity?> GetByIdAsync(
        Guid profileId,
        Guid responseId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        Guid profileId,
        QuestionnaireResponseEntity response,
        CancellationToken cancellationToken = default);
}
```

### Persistence Behavioral Requirements:
1. **Profile-Scoped Queries:** Every query must enforce profile scoping. Any attempt to query across profiles or without an authorized profile scope must return `null` or fail closed.
2. **Fail-Closed Semantics:** Persistence dependencies that time out or fail must propagate exceptions to the caller rather than masquerading as non-existent or successful records.
3. **Transaction Atomicity:** `SaveAsync` must atomically persist the `QuestionnaireResponseEntity` and all child `QuestionnaireAnswerEntity` records within a single database transaction.

---

## 4. Physical PostgreSQL Database Schema (M5 Implementation under M2 Delegation)

The physical schema is managed under a dedicated database schema `andril_social` with tables mapped via `SocialDbContext`:

```sql
-- DEDICATED SOCIAL MODULE SCHEMA (andril_social)

CREATE SCHEMA IF NOT EXISTS andril_social;

-- 1. Questionnaire Response Parent Table
CREATE TABLE IF NOT EXISTS andril_social.social_questionnaire_responses (
    id UUID PRIMARY KEY,
    profile_id UUID NOT NULL,
    version VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    is_skipped BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    -- FK Target: References M2's identity model in andril_identity.private_module_profiles
    CONSTRAINT fk_social_questionnaire_profile
        FOREIGN KEY (profile_id)
        REFERENCES andril_identity.private_module_profiles(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_social_questionnaire_profile_version
    ON andril_social.social_questionnaire_responses (profile_id, version);

-- 2. Questionnaire Answers Child Table
CREATE TABLE IF NOT EXISTS andril_social.social_questionnaire_answers (
    id UUID PRIMARY KEY,
    response_id UUID NOT NULL,
    question_id VARCHAR(128) NOT NULL,
    option_id VARCHAR(128) NOT NULL,
    answered_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_social_questionnaire_answers_response
        FOREIGN KEY (response_id)
        REFERENCES andril_social.social_questionnaire_responses(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_social_questionnaire_answers_response
    ON andril_social.social_questionnaire_answers (response_id);
```

> **Note on Social Module Boundary Verification:** While `profile_id` references `andril_identity.private_module_profiles(id)`, the persistence adapter and repository must verify at the query/application boundary that the profile possesses `module = 'social'`, ensuring cross-module foreign key correlation cannot be spoofed.

---

## 5. Architectural Decisions Awaiting Product / M2 / M6 Alignment

All questionnaire lifecycle, retention, and versioning semantics remain **PENDING** under BRD v2.4-R1 governance:

### Decision P-04: Questionnaire Retention, Lifecycle & Matching Thresholds
- **BRD Reference:** BRD v2.4-R1 §16.2 / Decision P-04 ("Social scoring weights, questionnaire retention, and matching thresholds").
- **Pending Questions:**
  1. *Response Replacement Policy:* Whether subsequent questionnaire submissions for the same version overwrite the existing response snapshot in place, or append a new versioned snapshot with an active status flag.
  2. *Data Retention & Erasure:* Whether voluntary questionnaire withdrawal soft-deletes or purges answers, and the retention period for inactive accounts.
  3. *Profile Deletion Cascades:* Whether account deactivation vs. GDPR Right to Erasure immediately purges questionnaire tables via database cascades or staged lifecycle tombstones.
- **Implementation Status:** Staged schema provides foundational storage without pre-empting retention policies. Concrete lifecycle workflows await formal Decision P-04 resolution.

*(Note: BRD v2.4-R1 Decision P-05 governs CF-20 use in Learning/Hobbies and does not apply to Social questionnaire retention).*

---

## 6. Concurrency Control Implementation

1. **Optimistic Concurrency:** Multiple client tabs or devices may attempt to submit answers concurrently. M5 maps an optimistic concurrency token on `QuestionnaireResponseEntity` evaluated via EF Core.
2. **Double-Submit Prevention:** Fast double-clicks or rapid client retries are handled with transactional atomicity, raising concurrency exceptions or returning idempotent snapshots.

---

## 7. M5 Automated Integration Test Verification Plan

Under M2 delegation, M5 implements automated integration tests in `Andril.IntegrationTests`:

1. **Profile-Scoped Isolation Test:** Verify that `GetByIdAsync` with an unauthorized or mismatched `profileId` returns `null` and prevents cross-profile data leakage.
2. **Referential Integrity & Cascade Test:** Verify referential integrity against `andril_identity.private_module_profiles` and confirm child answer deletion behavior upon profile removal.
3. **Atomic Transaction Test:** Verify that persistence failures in child answer entities roll back the entire transaction atomically.
4. **Concurrency Conflict Test:** Verify that concurrent updates trigger expected concurrency handling without data corruption.
5. **Authorization Boundary Enforcement:** Verify that persistence operations cannot be called with unvalidated caller scopes.
