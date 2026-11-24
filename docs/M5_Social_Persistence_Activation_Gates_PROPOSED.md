# ANDRIL M5 — SOCIAL PRODUCTION PERSISTENCE ACTIVATION GATES (PROPOSED)
## Comprehensive Database Architecture, Concurrency & Cross-Member Integration Plan

> **STATUS: PROPOSED ACTIVATION PLAN / NOT IMPLEMENTED**  
> **Author:** M5 (Thulaanchan — Social Domain & Matching Lead)  
> **Document Version:** 1.0.0-PROPOSED  
> **Target Release:** ANDRIL v1.0  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1, §11.1, §12.1, §18.1, §20.1; CF-01, CF-04, CF-11, CF-12, CF-18, CF-20, SOC-01, SOC-02  
> **Cross-Member Approval Owners:** M2 (Identity & Privacy), M4 (Communication), M6 (Security & Moderation)

---

## 1. Executive Summary & Activation Boundary

This document establishes the **decision-ready engineering roadmap and governance gates** required to implement relational database persistence and active production services for the Social module (`Andril.Social`). 

### Mandatory Boundaries:
1. **Zero Database Activation in this Task:** This document creates no database migrations, runs no schema scripts, registers no EF Core DbContexts in production DI, and connects to no persistent databases.
2. **Offline Domain vs. Persistent Production:** M5's offline domain components (e.g., `SocialProfilePairKey`, `SocialDirectionalInterest`, `SocialMatchEntity`, `SocialReciprocalInterestEvaluator`, `SocialMatchScopedLookupService`) provide pure, deterministic validation. Relational persistence requires atomic transactions, concurrency control, and cross-module authorization guarantees.
3. **Four-State Security Invariant (CF-20):**
   - *State 1: One-Sided Directional Interest*
   - *State 2: Mutual Interest Observed*
   - *State 3: Persisted Valid Match*
   - *State 4: Current Communication Authorization*
   These states are strictly non-equivalent. A persisted match record (State 3) does not grant chat or calling authority (State 4) without fresh, real-time M2/M4/M6 validation.

---

## 2. Core Relational Architecture & Technical Specifications

### 2.1 Match Entity Persistence & Concurrency Control
- **Entity:** `SocialMatchEntity` (`MatchId`, `PairKey`, `CreatedAtUtc`).
- **Physical Table Proposal:** `social_matches` (isolated in `SocialDbContext`).
- **Uniqueness Guarantee:** Unique constraint on `pair_key` (canonical sorted string `UUID:UUID`). Prevents duplicate match records for the same pair regardless of candidate traversal order.
- **Mutual Like Concurrency & Atomicity:**
  When evaluating reciprocal interest, an atomic transaction with `READ COMMITTED` or `SERIALIZABLE` isolation must be used:
  ```sql
  BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED;
  -- 1. Insert or update acting participant's directional interest
  INSERT INTO social_directional_interests (pair_key, acting_profile_id, is_positive, recorded_at_utc)
  VALUES (:pairKey, :callerId, :isPositive, NOW())
  ON CONFLICT (pair_key, acting_profile_id) DO UPDATE SET is_positive = EXCLUDED.is_positive, recorded_at_utc = NOW();

  -- 2. Inspect target participant's interest with row lock
  SELECT is_positive FROM social_directional_interests 
  WHERE pair_key = :pairKey AND acting_profile_id = :targetId FOR UPDATE;

  -- 3. If reciprocal positive interest confirmed, create mutual match
  IF (both_positive) THEN
      INSERT INTO social_matches (match_id, pair_key, profile_a_id, profile_b_id, status, matched_at_utc)
      VALUES (gen_random_uuid(), :pairKey, :profileA, :profileB, 'Active', NOW())
      ON CONFLICT (pair_key) DO NOTHING;
  END IF;
  COMMIT;
  ```

### 2.2 Directional-Interest Persistence & Upsert Idempotency
- **Table:** `social_directional_interests`.
- **Composite Primary Key:** `(pair_key, acting_profile_id)`.
- **Idempotency:** Re-swiping or updating an interest uses `ON CONFLICT (pair_key, acting_profile_id) DO UPDATE`.
- **Privacy Enforcement:** Directional interest decisions are strictly private. The target candidate receives zero notification or exposure of one-sided likes until mutual matching occurs.

### 2.3 Questionnaire Responses & Answers Storage Architecture
- **Normalized vs. Denormalized Proposal:**
  - *Option A (Normalized):* `social_questionnaire_responses` with 1-to-many child table `social_questionnaire_answers`. Aligns with domain entity `QuestionnaireResponseEntity` and `QuestionnaireAnswerEntity`.
  - *Option B (Encrypted JSONB):* Single `encrypted_answers JSONB` column. Requires application-layer envelope encryption and key management.
- **Decision Owner:** M2 & M6 must formally approve whether to store answers in normalized child tables or encrypted JSONB blobs under pending Decision P-04.
- **Unanswered Dimensions:** Skipped questions must never be assigned arbitrary 0 scores; matching algorithms must handle omitted dimensions gracefully.

### 2.4 Match Lifecycle, Revocation & Authorization Freshness
- **Status Lifecycle:** `Active`, `Unmatched`, `BlockedByA`, `BlockedByB`.
- **Unmatch Action:** Either participant can terminate the match. Status transitions to `Unmatched`.
- **Block Action:** A block recorded via M6/M2 updates status to `BlockedBy{X}` and revokes mutual visibility.
- **Revocation Delivery Gap:** A time-bounded communication token (Op 11) must be invalidated immediately if an unmatch or block occurs. Cross-service event delivery requires an approved message broker (e.g. transactional outbox, PostgreSQL LISTEN/NOTIFY, or Redis Pub/Sub) agreed with M4.

### 2.5 PostgreSQL Row-Level Security (RLS) & Error Concealment
- **RLS Policy Proposal:** Enforce tenant/profile scoping directly in PostgreSQL using `current_setting('app.current_profile_id')` to ensure queries cannot read foreign profiles even in the event of an application-layer defect.
- **Error Concealment:** Missing records, foreign scopes, and unauthorized reads return non-disclosing 404 responses or empty sets without revealing resource existence.

### 2.6 Data Retention, Deletion & Personal Data Erasure (GDPR)
- Questionnaire answers, directional decisions, and match tombstones must have defined retention lifetimes approved under P-04 and P-10.
- Personal data erasure procedures must purge private profile questionnaire answers, likes, and matches upon account deletion.

---

## 3. Evidence Matrix

| Requirement | Existing Source | Missing Implementation | Proposed Contract | Decision Owner | Approval Status | Acceptance Test |
|---|---|---|---|---|---|---|
| **CF-20: Match Identity** | `SocialMatchEntity.cs`, `SocialProfilePairKey.cs` | Database mapping and persistence | `ISocialMatchRepository.cs` | M5 | **CONFIRMED** (Domain Model) | `SocialMatchEntityTests.cs` |
| **CF-20: Match Read Repository** | `ISocialMatchRepository.cs` | `SocialMatchRepository` EF Core implementation in `Andril.Persistence` | `ISocialMatchRepository.cs` | M5 | **CONFIRMED** (Interface) | `SocialMatchRepositoryContractTests.cs` |
| **CF-20: Match Scoped Lookup** | `SocialMatchScopedLookupService.cs` | Production DI registration and controller | `SocialMatchScopedLookupService` | M5 | **CONFIRMED** (Service) | `SocialMatchScopedLookupServiceTests.cs` |
| **CF-20: Match Persistence DB** | None | `SocialDbContext.cs`, `social_matches` table, migration | Proposed Schema §4 | M2 / M6 | **PENDING APPROVAL** | Ephemeral PostgreSQL integration tests |
| **CF-20: Directional Interests DB** | `SocialDirectionalInterest.cs`, `ISocialInterestRepository.cs` | `SocialInterestRepository` EF Core implementation, `social_directional_interests` table | Proposed Schema §4 | M2 / M6 | **PENDING APPROVAL** | Relational upsert idempotency tests |
| **SOC-01: Questionnaire DB** | `QuestionnaireResponseEntity.cs`, `IQuestionnairePersistenceRepository.cs` | `QuestionnairePersistenceRepository` EF Core adapter, response tables | Proposed Schema §4 | M2 / M6 | **PENDING APPROVAL** | Relational response persistence tests |
| **SOC-02: Smart Matching** | `SocialCandidateMatchingService.cs`, `SocialDiscoveryEligibilityPreFilter.cs` | Scoring weights and algorithm tuning | P-04 BRD decision | Product Lead | **PENDING APPROVAL (P-04)** | Offline candidate filter tests |
| **CF-12: Blocking Engine** | Preflight block check interfaces | Central M6 bidirectional block store & hook | Block verification contract | M6 / M2 | **PENDING APPROVAL** | Bidirectional block exclusion tests |
| **M4 Integration: Chat Evidence** | `IChatAccessAuthorizer.cs` | Real-time match authorization provider & token service | Communication evidence API | M4 / M5 | **PENDING APPROVAL** | Token generation and revocation tests |
| **M2 Gate: Module Access** | `IPrivateModuleAccess.RequireAsync` | Operational handler (currently throws pending exception) | M2 module access contract | M2 | **IMPLEMENTED BUT NOT ACTIVATED** | Module access authorization tests |

---

## 4. Phased Implementation Roadmap

### Phase 1: Tasks Safe to Proceed Immediately (Offline Domain Foundations)
*These tasks require no external database approvals and introduce zero breaking changes:*
1. Complete offline domain models and scoped lookup services (Completed in Tasks 63–68).
2. Author database persistence activation plans (Tasks 69 & 70).
3. Implement in-memory test doubles and architectural contract tests for future persistence interfaces.

### Phase 2: Tasks Requiring M2/M6 Persistence Approval
*These tasks require explicit sign-off on database schema, encryption, and RLS:*
1. Create `SocialDbContext.cs` and entity type configurations in `src/Andril.Persistence`.
2. Implement `SocialMatchRepository.cs`, `SocialInterestRepository.cs`, and `QuestionnairePersistenceRepository.cs`.
3. Add Testcontainers/PostgreSQL integration tests verifying concurrency, upserts, and foreign key isolation.
4. Generate initial migration `InitialSocial` (only after schema approval).

### Phase 3: Tasks Requiring M2/M4/M6 Cross-Module Activation
*These tasks require operational integration gates:*
1. Activate M2 `IPrivateModuleAccess` for `social` module.
2. Implement M4 match-gated communication evidence token generator.
3. Integrate M6 bidirectional blocking filter in candidate discovery and interest writes.
4. Scaffold and register Social controllers in `src/Andril.API`.

---

*End of Proposed Social Persistence Activation Gates Plan.*
