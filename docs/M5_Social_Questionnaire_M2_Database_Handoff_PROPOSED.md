# ANDRIL M5 — SOCIAL QUESTIONNAIRE M2 DATABASE HANDOFF (PROPOSED)
## Comprehensive Database Persistence, Entity Schema, Concurrency & Governance Handoff

> **STATUS: PROPOSED SPECIFICATION / NOT IMPLEMENTED**  
> **Author:** M5 (Thulaanchan — Social Domain & Matching Lead)  
> **Target Audience / Owners:** M2 (Vizhan — Identity, Privacy & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Document Version:** 1.0.0-PROPOSED  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-20  

---

## 1. Executive Summary & Ownership Boundary

This document outlines the authoritative engineering specifications and architectural requirements for integrating the **SOC-01 Personality Questionnaire** domain into relational database persistence.

### Architectural Boundary:
- **M5 Domain & API Ownership:** M5 owns the application services, strict JSON body parsers, validation rules (`QuestionnaireValidator`, `PersonalityQuestionnaireV1Definition`), API controllers, and internal domain entities. M5 produces **zero database migrations, zero DbContext definitions, and zero direct SQL executions**.
- **M2 Database Ownership:** M2 owns all relational database infrastructure, PostgreSQL schemas, Supabase configurations, EF Core `DbContext` implementations (`SocialDbContext`), migrations, and concrete repository classes (`SocialQuestionnairePersistenceRepository`).
- **M6 Security Sign-off:** M6 owns access boundary enforcement, privacy audit criteria, and verification of zero data-leakage across modules.

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

### Persistence Behavioral Requirements:
1. **Profile-Scoped Queries:** Every query must enforce `WHERE profile_id = :profileId`. Any attempt to query across profiles or without profile scoping must return `null` or fail closed.
2. **Fail-Closed Semantics:** Persistence dependencies that time out or fail must propagate exceptions to the caller rather than masquerading as non-existent or successful records.
3. **Transaction Atomicity:** `SaveAsync` must atomically persist the `QuestionnaireResponseEntity` and all child `QuestionnaireAnswerEntity` records within a single database transaction.

---

## 4. Proposed PostgreSQL Database Schema

M2 is requested to author the EF Core mapping and migration implementing the following physical table structure:

```sql
-- 1. Questionnaire Response Parent Table
CREATE TABLE IF NOT EXISTS social_questionnaire_responses (
    id UUID PRIMARY KEY,
    profile_id UUID NOT NULL,
    version VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    is_skipped BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    xmin XID, -- PostgreSQL system column for optimistic concurrency
    CONSTRAINT fk_social_questionnaire_profile
        FOREIGN KEY (profile_id)
        REFERENCES social_profiles(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_social_questionnaire_profile_version
    ON social_questionnaire_responses (profile_id, version);

-- 2. Questionnaire Answers Child Table
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

---

## 5. Architectural Decisions Awaiting M2 / M6 Alignment

### Decision P-04: Response Overwrite vs. Versioned Append
- **Context:** When a user retakes or updates their questionnaire answers for the same version (e.g., `v1`).
- **Option A (In-Place Overwrite / Upsert):** Previous response is overwritten. Simplest schema, but loses history.
- **Option B (Append-Only with Active Flag — M5 Recommended):** Each submission generates a new `QuestionnaireResponseEntity` snapshot with a new `Id`. A boolean column `is_active` marks the current active response. Retains audit history and allows longitudinal research/match evaluation.
- **Action Required:** M2 and M6 confirmation during schema design.

### Decision P-05: Deletion Lifecycle — Soft Delete vs. Hard Cascade
- **Context:** GDPR Right to Erasure, account deactivation, and module opt-out.
- **Option A (Soft Delete):** Maintain `deleted_at_utc` timestamp for 30-day recovery staging.
- **Option B (Hard Cascade — M5 Recommended):** On complete Social profile deletion, foreign key cascade (`ON DELETE CASCADE`) immediately purges all questionnaire data. On voluntary questionnaire reset, deactivate or soft-delete.
- **Action Required:** M2 privacy lead sign-off.

---

## 6. Concurrency Control Specifications

1. **Optimistic Concurrency:** Multiple client tabs or devices may attempt to submit answers concurrently. M2 should map the PostgreSQL `xmin` hidden column or an explicit `row_version BYTEA` token using EF Core:
   ```csharp
   builder.Entity<QuestionnaireResponseEntity>()
       .Property<byte[]>("RowVersion")
       .IsRowVersion();
   ```
2. **Double-Submit Prevention:** Fast double-clicks or mobile retry bursts must be handled gracefully:
   - Handle `DbUpdateConcurrencyException` by returning HTTP 409 Conflict or idempotently returning the first persisted response snapshot.

---

## 7. Required M2 Integration & Verification Tests

Before activating production persistence, M2 must provide automated integration tests in `Andril.IntegrationTests`:

1. **Cross-Profile Isolation Test:** Verify that `GetByIdAsync(profileA, responseB_Id)` returns `null` and prevents unauthorized cross-tenant read.
2. **Cascade Deletion Test:** Verify that deleting a `SocialProfile` automatically deletes associated `social_questionnaire_responses` and `social_questionnaire_answers`.
3. **Transaction Rollback Test:** Verify that if an answer record fails constraint validation, the parent response record is rolled back atomically.
4. **Optimistic Concurrency Conflict Test:** Verify that concurrent updates trigger concurrency exceptions without corrupted state.
5. **Authorization Boundary Integrity:** Verify that repository operations cannot be invoked with unverified or forged caller identities.
