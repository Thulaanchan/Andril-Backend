# ANDRIL M5 — SOCIAL PHYSICAL SCHEMA & DECISION MATRIX
## Relational Persistence Architecture, Invariant Boundaries & BRD Decision Governance Matrix

> **STATUS: APPROVED IMPLEMENTATION BASELINE & PENDING GOVERNANCE GATES**  
> **Module Ownership:** M5 (Thulaanchan — Social & Personality Matching Domain Lead)  
> **Collaborating Leads:** M2 (Vizhan — Identity, Onboarding & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Integration Branch:** `Develop`  
> **Document Version:** 1.0.0  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-18, CF-20, Decision P-04  

---

## 1. Executive Summary & Gate Evaluation

In accordance with **Tasks 91–110 Master Plan** and explicit pre-implementation requirements, this document establishes the **Physical Schema Specification** and **Decision Governance Matrix** prior to implementing `SocialDbContext` (Task 95).

### Core Pre-Implementation Gate Finding:
- **Foundational Schema is Policy-Neutral:** The physical relational tables (`social_questionnaire_responses` and `social_questionnaire_answers`) map existing domain entities (`QuestionnaireResponseEntity` and `QuestionnaireAnswerEntity`) without pre-empting or hardcoding unapproved retention, erasure, or scoring policies.
- **Decision P-04 Remains Pending:** Business policies regarding response overwrite vs. append-only history, data retention windows, and matching thresholds remain formally **PENDING**.
- **Safe Implementation Path:** Task 95 will implement purely the foundational EF Core model without policy-dependent triggers, unapproved retention defaults, or destructive lifecycle routines.

---

## 2. Decision Governance Matrix (BRD v2.4-R1 & Multi-Module Contracts)

| Architectural / Policy Dimension | Governance Status | Decision Authority | Implemented in M5 Tasks 95–106? | Exact Architectural Rule / Invariant |
| :--- | :---: | :---: | :---: | :--- |
| **Dedicated Schema Namespace** | **APPROVED** | M2 / M5 | **YES** | Schema name: `andril_social`. Completely isolated from `andril_identity` and `andril_hobbies`. |
| **Response Snapshot Model** | **APPROVED** | M5 Lead | **YES** | Maps `QuestionnaireResponseEntity` (`Id`, `ProfileId`, `Version`, `Status`, `CreatedAtUtc`, `Answers`). |
| **Answer Record Model** | **APPROVED** | M5 Lead | **YES** | Maps `QuestionnaireAnswerEntity` (`Id`, `ResponseId`, `QuestionId`, `OptionId`, `AnsweredAtUtc`). |
| **Foreign Key to Identity Model** | **APPROVED** | M2 / M5 | **YES** | Foreign key references `andril_identity.private_module_profiles(id)`. |
| **Social Module Discriminator Gate** | **APPROVED** | M5 / M6 | **YES** | Persistence adapter verifies at the query/app boundary that the referenced profile has `module = 'social'`. |
| **Profile-Scoped Data Isolation** | **APPROVED** | M5 / M6 | **YES** | All reads and writes enforce `profile_id` scoping; unscoped or multi-tenant queries fail closed. |
| **Optimistic Concurrency Control** | **APPROVED** | M2 / M5 | **YES** | Evaluated via EF Core row versioning / PostgreSQL concurrency tokens. |
| **Response Replacement Policy** | **PENDING** | Decision P-04 (Product/M2/M6) | **NO** (Policy-Neutral) | Storage layer supports persisting responses; whether API overwrites or appends is decoupled until P-04 resolution. |
| **Questionnaire Retention Lifetimes** | **PENDING** | Decision P-04 (Product/M2/M6) | **NO** (Policy-Neutral) | No automatic purge timers, TTLs, or unapproved retention defaults are introduced. |
| **GDPR Soft-Delete vs. Hard Cascade** | **PENDING** | Decision P-04 (Product/M2/M6) | **NO** (Policy-Neutral) | Foreign key cascade reflects parent profile deletion; voluntary soft-delete lifecycle awaits P-04 resolution. |
| **Compatibility Scoring Weights** | **PENDING** | Decision P-04 (Product Lead) | **NO** (Offline / Staged) | Algorithm weights and matching scoring are deferred to future offline candidate evaluation. |

---

## 3. Physical Database Schema Specification (`andril_social`)

### 3.1 DDL Structure (PostgreSQL)

```sql
CREATE SCHEMA IF NOT EXISTS andril_social;

-- 1. Questionnaire Response Snapshots
CREATE TABLE IF NOT EXISTS andril_social.social_questionnaire_responses (
    id UUID PRIMARY KEY,
    profile_id UUID NOT NULL,
    version VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL,
    is_skipped BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    row_version BYTEA,
    CONSTRAINT fk_social_questionnaire_profile
        FOREIGN KEY (profile_id)
        REFERENCES andril_identity.private_module_profiles(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_social_questionnaire_profile_version
    ON andril_social.social_questionnaire_responses (profile_id, version);

-- 2. Questionnaire Answers (Child Records)
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

### 3.2 Security & Data Privacy Invariants
1. **Zero Secret Leakage:** Answers are strictly confidential and mapped only to authorized Social profile scopes.
2. **Module Verification:** Cross-module identity spoofing (e.g. attempting to link a questionnaire response to a Learning profile ID) is prohibited and blocked by the persistence adapter.
3. **Transactional Integrity:** Saving a response and its answers is strictly atomic within `SaveAsync`.
