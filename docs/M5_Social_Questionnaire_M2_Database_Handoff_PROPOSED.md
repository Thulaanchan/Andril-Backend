# ANDRIL M5 — SOCIAL QUESTIONNAIRE M2 DATABASE HANDOFF (PROPOSED)
## Comprehensive Database Persistence, Entity Schema, Concurrency & Governance Proposal

> **STATUS: PROPOSED SPECIFICATION / NOT IMPLEMENTED**  
> **Author:** M5 (Thulaanchan — Social Domain & Matching Lead)  
> **Target Audience / Owners:** M2 (Vizhan — Identity, Privacy & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Document Version:** 1.1.0-PROPOSED  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-20, Decision P-04  

---

## 1. Executive Summary & Ownership Boundary

This document outlines the proposed backend persistence requirements, domain entity structures, and interface specifications for integrating the **SOC-01 Personality Questionnaire** into relational database persistence.

### Strict Architectural Boundaries:
- **M5 Domain & API Scope:** M5 owns domain contracts, validators (`QuestionnaireValidator`, `PersonalityQuestionnaireV1Definition`), API controllers, contract parsers (`SocialQuestionnaireSubmissionBodyParser`), and internal domain entities. M5 produces **zero database migrations, zero DbContext definitions, and zero direct SQL executions**.
- **M2 Database Scope:** M2 owns all relational database infrastructure, PostgreSQL schemas, Supabase configurations, EF Core `DbContext` architectures (e.g., within `AccountsDbContext` or a dedicated context), database migrations, and concrete repository implementations.
- **M6 Security Sign-off:** M6 owns access boundary validation, privacy audit criteria, and verification of zero data-leakage across modules.

---

## 2. Domain Entities & Invariant Specifications

The offline domain entities reside in `src/Andril.Social/Entities/`:

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

### Proposed Persistence Behavioral Requirements:
1. **Profile-Scoped Queries:** Every query must enforce profile scoping. Any attempt to query across profiles or without an authorized profile scope must return `null` or fail closed.
2. **Fail-Closed Semantics:** Persistence dependencies that time out or fail must propagate exceptions to the caller rather than masquerading as non-existent or successful records.
3. **Transaction Atomicity:** `SaveAsync` must atomically persist the `QuestionnaireResponseEntity` and all child `QuestionnaireAnswerEntity` records within a single database transaction.

---

## 4. Proposed PostgreSQL Database Schema (Design Proposal for M2 Review)

The following schema is a **non-binding architectural design proposal** submitted for M2 evaluation. All physical tables, schemas, indexes, and migrations will be authored and owned exclusively by M2:

```sql
-- DESIGN PROPOSAL FOR M2 DATABASE REVIEW (PROPOSED)

-- 1. Proposed Questionnaire Response Parent Table
CREATE TABLE IF NOT EXISTS social_questionnaire_responses (
    id UUID PRIMARY KEY,
    profile_id UUID NOT NULL,
    version VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    is_skipped BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    -- FK Target: References M2's actual identity model in andril_identity.private_module_profiles
    CONSTRAINT fk_social_questionnaire_profile
        FOREIGN KEY (profile_id)
        REFERENCES andril_identity.private_module_profiles(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_social_questionnaire_profile_version
    ON social_questionnaire_responses (profile_id, version);

-- 2. Proposed Questionnaire Answers Child Table
CREATE TABLE IF NOT EXISTS social_questionnaire_answers (
    id UUID PRIMARY KEY,
    response_id UUID NOT NULL,
    question_id VARCHAR(128) NOT NULL,
    option_id VARCHAR(128) NOT NULL,
    answered_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_social_questionnaire_answers_response
        FOREIGN KEY (response_id)
        REFERENCES social_questionnaire_responses(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_social_questionnaire_answers_response
    ON social_questionnaire_answers (response_id);
```

> **Note on Foreign Key Target:** In the existing M2 database architecture, private profiles are persisted in `andril_identity.private_module_profiles` (entity `PrivateModuleProfile`, filtered by `module = 'social'`). There is no standalone `social_profiles` table. The proposed FK correctly references `andril_identity.private_module_profiles(id)`.

---

## 5. Architectural Decisions Awaiting Product / M2 / M6 Alignment

All questionnaire lifecycle, retention, and versioning semantics remain **PENDING** under BRD v2.4-R1 governance:

### Decision P-04: Questionnaire Retention, Lifecycle & Matching Thresholds
- **BRD Reference:** BRD v2.4-R1 §16.2 / Decision P-04 ("Social scoring weights, questionnaire retention, and matching thresholds").
- **Pending Questions:**
  1. *Response Replacement Policy:* Whether subsequent questionnaire submissions for the same version overwrite the existing response snapshot in place, or append a new versioned snapshot with an active status flag.
  2. *Data Retention & Erasure:* Whether voluntary questionnaire withdrawal soft-deletes or purges answers, and the retention period for inactive accounts.
  3. *Profile Deletion Cascades:* Whether account deactivation vs. GDPR Right to Erasure immediately purges questionnaire tables via database cascades or staged lifecycle tombstones.
- **Status:** **PENDING FORMAL APPROVAL (Decision P-04)**. M5 recommends no default policy; M2 and M6 must define the approved lifecycle in accordance with identity compliance rules.

*(Note: BRD v2.4-R1 Decision P-05 governs CF-20 use in Learning/Hobbies and does not apply to Social questionnaire retention).*

---

## 6. Concurrency Control Proposal (Subject to M2 Verification)

As a design proposal for high-concurrency environments, M2 is requested to evaluate optimistic concurrency mechanisms:

1. **Optimistic Concurrency Design Options:**
   - *Option 1 (PostgreSQL system column):* Leverage PostgreSQL's native `xmin` hidden system column.
   - *Option 2 (EF Core explicit row versioning):* Map a concurrency token property:
     ```csharp
     builder.Entity<QuestionnaireResponseEntity>()
         .Property<byte[]>("RowVersion")
         .IsRowVersion();
     ```
2. **Double-Submit Prevention:**
   - Fast double-clicks or rapid client retries should be evaluated by M2 to determine whether to surface an HTTP 409 Conflict or return the existing response idempotently.
3. **M2 Ownership:** Final selection, implementation, and performance benchmarking of concurrency tokens are owned by M2.

---

## 7. Proposed M2 Verification & Integration Test Requirements

Upon implementing the persistence layer, M2 is requested to author the following integration tests in `Andril.IntegrationTests`:

1. **Profile-Scoped Isolation Test:** Verify that `GetByIdAsync` with an unauthorized or mismatched `profileId` returns `null` and prevents cross-profile data leakage.
2. **Referential Integrity & Cascade Test:** Verify referential integrity against `andril_identity.private_module_profiles` and confirm child answer deletion behavior upon profile removal.
3. **Atomic Transaction Test:** Verify that persistence failures in child answer entities roll back the entire transaction atomically.
4. **Concurrency Conflict Test:** Verify that concurrent updates trigger expected concurrency handling without data corruption.
5. **Authorization Boundary Enforcement:** Verify that persistence operations cannot be called with unvalidated caller scopes.
