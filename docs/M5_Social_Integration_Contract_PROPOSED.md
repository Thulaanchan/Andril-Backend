# ANDRIL M5 — SOCIAL DOMAIN & PERSONALITY MATCHING INTEGRATION CONTRACT (PROPOSED)
## Multi-Module Integration Specification: Social, Discovery, Matching & Communication Boundaries

> **STATUS: PROPOSAL / NOT IMPLEMENTED**  
> **Author:** M5 (Thulaanchan — Social Domain & Matching Lead)  
> **Document Version:** 1.1.0-PROPOSED  
> **Target Release:** ANDRIL v1.0  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1, §11.1, §12.1, §18.1, §20.1; SOC-01, SOC-02, CF-04, CF-05, CF-11, CF-12, CF-18, CF-20  
> **Cross-Member Dependencies:** M2 (Identity, Privacy, Age 18+, Opt-In), M4 (Match-Gated Communication & Messaging), M6 (Security Review & Moderation)

---

## 1. Executive Summary & Proposal Boundary

This document outlines the **architectural, API endpoint, and relational database contract proposal** for the Social module (`Andril.Social`). It specifies the concrete interfaces, transaction boundaries, privacy safeguards, and approval gates required to transition M5's offline discovery and preflight domain policies into active, persistent production endpoints.

### Key Operational Rules:
1. **Status:** This document is an integration proposal. **No controller routes are active, no migrations are applied, and no DI registrations or event dispatchers are enabled in this task.**
2. **State Disambiguation (Mandatory Invariant):**
   The backend strictly distinguishes the following four distinct states:
   - **State 1: One-Sided Directional Interest:** A single participant's like or pass decision towards a target candidate (`SocialDirectionalInterest`).
   - **State 2: Mutual Interest Observed:** Offline observation that both parties have recorded positive interest (`SocialPairInterestObservationService`).
   - **State 3: Persisted Valid Match:** An authoritatively validated, transactionally committed mutual match record with active standing (proposed lifecycle; not yet persisted in production).
   - **State 4: Current Communication Authorization:** Real-time, fresh verification that both profiles remain active, opted-in, adult (18+), unblocked, and mutually matched, granting temporary M4 conversation access evidence.
   > **CRITICAL SECURITY INVARIANT:** These four states are **NOT EQUIVALENT**. An offline interest observation or persisted match record **MUST NEVER BE USED ALONE** to authorize M4 chat, calls, streaming, or communication tokens.
3. **Module Isolation:** Social is an isolated private module. No cross-module identity leakage with Learning Classrooms or Hobbies Clubs.
4. **Pending Product Decisions (BRD v2.4-R1):**
   The following pending decisions from BRD v2.4-R1 are **EXPLICITLY MARKED PENDING** and are **NOT silently approved** or resolved by this document:
   - **P-04:** Social scoring weights, questionnaire retention and matching thresholds.
   - **P-05:** CF-20 use in Learning/Hobbies.
   - **P-06:** Zone roles, joining and non-Club quotas.
   - **P-07:** Group inactivity and restoration rules.
   - **P-10:** Contextual AI consent/provider/retention/quotas.
   - **P-14:** Runtime, provider and hosting decisions.
   - **P-15:** Shared leads and backend contract ownership.
   - **P-19:** Cross-repository API contract publication/versioning.
   
   CF-20 shared-service ownership is marked **PROPOSED** under BRD §16.2.

---

## 2. Core Backend Work Areas

### Area A: Social Preferences and Eligibility (CF-04 / CF-11 / CF-12)
- **Age Gate:** Strictly 18+ (verified by authoritative M2 claims).
- **Opt-In:** Explicit user opt-in required for Social discovery.
- **Profile Standing:** Must be in active, good standing (unbanned, unsuspended).
- **Bidirectional Blocking:** Mutual invisibility and exclusion if either party blocks the other (enforced via M6 blocking engine proposal).

### Area B: Personality Questionnaire Definition & Scoped Response Storage (SOC-01)
- **Questionnaire Definition:** Versioned definitions (`QuestionnaireV1Definition`, cataloged in `QuestionnaireDefinitionCatalog`) managed immutably.
- **Confidential Answers:** Raw answers, diagnostic scores, and timestamps are confidential and concealed from diagnostics/JSON (`[JsonIgnore]`).
- **No Invented Scores:** No automatic zero scores for skipped questionnaires; matching algorithms must handle skipped or absent dimensions safely.
- **Retention & Purge:** Answer retention and deletion policies remain PENDING under Decision P-04.

### Area C: Authorized Explore Candidate Discovery (CF-04 / SOC-02)
- Evaluates candidates against caller evidence via `SocialDiscoveryEligibilityPreFilter`.
- Fails closed if authoritative verification is missing or indeterminate.
- Conceals private candidate identities and sensitive exclusion reasons.
- Candidate scoring weights and compatibility percentages remain PENDING under Decision P-04.

### Area D: Likes and Directional-Interest Writes (CF-20)
- Preflight validation via `SocialInterestWritePreflightPolicy`.
- Deterministic validation of canonical pair key (`SocialProfilePairKey`), legal age, opt-in, active standing, discoverability, and blocks.
- One-sided decisions never grant communication permissions.
- Daily swipe/like quotas remain PENDING product approval.

### Area E: Reciprocal Match Lifecycle (CF-20)
- Offline evaluation of mutual positive decisions via `SocialReciprocalInterestEvaluator`.
- Proposed future transaction: atomic transition to persisted match state upon verified mutual like.
- Note: Persisted match records do NOT currently exist in the database; status lifecycle enums and tables are proposed.

### Area F: Match Visibility and Unmatch Behavior (CF-20 / CF-11)
- Either participant can unmatch at any time.
- Unmatching invalidates reciprocal match standing.
- Unmatch tombstone retention and re-matching eligibility policies remain PENDING.

### Area G: Block and Revocation Propagation (CF-12)
- A block recorded by either participant terminates mutual matching.
- Real-time cross-service revocation delivery and freshness guarantees remain an unapproved integration dependency pending M4/M6 review.

### Area H: M4 Match-Gated Communication Access Evidence (M4 Integration)
- Real-time authorization check requiring valid match standing, mutual unblocked state, and active profiles.
- Time-bounded, revocable communication tokens.

### Area I: Consent-Controlled Contextual AI Integration (CF-18)
- Contextual AI consent, provider selection, data retention, and quotas remain strictly PENDING under Decision P-10.
- Mandatory mutual consent mechanisms or specific differential-privacy algorithms are proposed concepts, not approved implementations.

---

## 3. Proposed Method/Path Inventory & Operation Contracts

The table below summarizes the **11 proposed Social operations** across HTTP method/path endpoints:

| # | Operation | Method | Proposed Path | Scope | Preconditions | Approval Status |
|---|---|---|---|---|---|---|
| 1 | Social Profile Settings Read | `GET` | `/api/v1/social/preferences` | Social | Adult 18+, Authenticated | PROPOSED |
| 2 | Social Profile Settings Update | `PUT` | `/api/v1/social/preferences` | Social | Adult 18+, Authenticated | PROPOSED |
| 3 | Questionnaire Definition | `GET` | `/api/v1/social/questionnaires/active` | Social | Authenticated | PROPOSED |
| 4 | Questionnaire Submit | `POST` | `/api/v1/social/questionnaires/responses` | Social | Adult 18+, Opted-in | PROPOSED |
| 5 | Questionnaire Read | `GET` | `/api/v1/social/questionnaires/responses/me` | Social | Owner Caller Binding | PROPOSED |
| 6 | Explore Candidate Discovery | `GET` | `/api/v1/social/explore` | Social | Opted-in, Discoverable, Active | PROPOSED |
| 7 | Directional Interest Write | `POST` | `/api/v1/social/interests` | Social | Validated Adult Pair, Unblocked | PROPOSED |
| 8 | Matches List | `GET` | `/api/v1/social/matches` | Social | Valid Persisted Matches | PROPOSED |
| 9 | Match Details | `GET` | `/api/v1/social/matches/{matchId}` | Social | Participant in Match | PROPOSED |
| 10 | Unmatch Participant | `POST` | `/api/v1/social/matches/{matchId}/unmatch` | Social | Participant in Match | PROPOSED |
| 11 | Communication Evidence Grant | `POST` | `/api/v1/social/matches/{matchId}/communication-evidence` | Social / M4 | Real-time Active Valid Match | PROPOSED |

---

### Detailed Operation Specifications

#### Op 1: Social Profile Settings Read
- **HTTP Method & Path:** `GET /api/v1/social/preferences`
- **Actor:** Authenticated caller.
- **Preconditions:** M2 verified adult ($\ge 18$).
- **Response DTO (`200 OK`):** Current social preferences projection (`isOptedIn`, `isDiscoverable`).
- **Errors:** `403 Forbidden` if caller age claim $< 18$.

#### Op 2: Social Profile Settings Update
- **HTTP Method & Path:** `PUT /api/v1/social/preferences`
- **Actor:** Authenticated caller.
- **Preconditions:** M2 verified adult ($\ge 18$).
- **Request DTO:**
  ```json
  {
    "hasExplicitSocialOptIn": true,
    "isDiscoverable": true,
    "allowContextualAiSuggestions": false
  }
  ```
- **Response DTO (`200 OK`):**
  ```json
  {
    "isOptedIn": true,
    "isDiscoverable": true,
    "updatedAtUtc": "2026-11-23T16:30:00Z"
  }
  ```
- **Errors:** `403 Forbidden` if caller age claim $< 18$.

#### Op 3 & 4: Questionnaire Definition & Response Submission
- **HTTP Method & Path:** `GET /api/v1/social/questionnaires/active` & `POST /api/v1/social/questionnaires/responses`
- **Preconditions:** Opted-in, active social profile.
- **Request DTO:**
  ```json
  {
    "definitionId": "018d9f4e-0000-7000-8000-000000000001",
    "answers": {
      "q1_social_energy": 4,
      "q2_spontaneity": 2
    },
    "skipRemaining": false
  }
  ```
- **Privacy Enforcement:** Responses stored privately; diagnostic JSON serializations conceal raw answers (`[JsonIgnore]`).
- **No Zero Scoring:** Missing dimensions are marked `Unanswered`, not assigned score 0.

#### Op 5: Questionnaire Scoped Read
- **HTTP Method & Path:** `GET /api/v1/social/questionnaires/responses/me`
- **Authorization:** Only the authoritative owner can read their own response (`SocialQuestionnaireResponseScopedLookupService`).
- **Response DTO (`200 OK`):** Returns completion status and answered question count; raw answers redacted from client logs.

#### Op 6: Explore Candidate Discovery
- **HTTP Method & Path:** `GET /api/v1/social/explore?limit=20`
- **Preconditions:** Caller is 18+, opted-in, active, and discoverable.
- **Filtering Engine:** Evaluates candidate pool via `SocialDiscoveryEligibilityPreFilter`:
  - Enforces bidirectional block clearance (`BlockedByCaller`, `BlockedByCandidate`).
  - Filters underage, inactive, hidden, or non-opted-in candidates.
  - Fails closed on missing authoritative evidence.
- **Response DTO (`200 OK`):** Array of candidate public cards strictly excluding private IDs and raw questionnaire answers.

#### Op 7: Directional Interest Write (Like / Pass)
- **HTTP Method & Path:** `POST /api/v1/social/interests`
- **Preconditions:** Preflight evaluated via `SocialInterestWritePreflightPolicy`.
- **Request DTO:**
  ```json
  {
    "targetProfileId": "018d9f4e-2222-7000-8000-000000000002",
    "isPositive": true
  }
  ```
- **Proposed Transaction (Unapproved):**
  ```sql
  BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED;
  -- 1. Insert or update directional interest
  INSERT INTO social_directional_interests (pair_key, acting_profile_id, is_positive, recorded_at_utc)
  VALUES (:pairKey, :callerId, :isPositive, NOW())
  ON CONFLICT (pair_key, acting_profile_id) DO UPDATE SET is_positive = EXCLUDED.is_positive, recorded_at_utc = NOW();

  -- 2. Check if opposing positive interest exists
  SELECT is_positive FROM social_directional_interests 
  WHERE pair_key = :pairKey AND acting_profile_id = :targetId;

  -- 3. If mutual like, transition to reciprocal match (proposed)
  IF (mutual_positive) THEN
      INSERT INTO social_matches (match_id, pair_key, profile_a_id, profile_b_id, status, matched_at_utc)
      VALUES (gen_random_uuid(), :pairKey, :callerId, :targetId, 'Active', NOW())
      ON CONFLICT (pair_key) DO NOTHING;
  END IF;
  COMMIT;
  ```
- **Response DTO (`200 OK`):**
  ```json
  {
    "status": "Recorded",
    "isMatchObserved": true
  }
  ```
  *(Note: Does NOT issue communication tokens!)*

#### Op 8 & 9: Matches List & Details
- **HTTP Method & Path:** `GET /api/v1/social/matches` & `GET /api/v1/social/matches/{matchId}`
- **Preconditions:** Returns only `Active` matches where neither participant has blocked or unmatched the other.
- **Errors:** Conceals revoked or nonexistent matches.

#### Op 10: Unmatch Participant
- **HTTP Method & Path:** `POST /api/v1/social/matches/{matchId}/unmatch`
- **Lifecycle Action (Proposed):**
  - Updates match status to terminal unmatch state.
  - Requires approved event delivery mechanism to notify M4.

#### Op 11: Communication Evidence Grant (M4 Integration Gateway)
- **HTTP Method & Path:** `POST /api/v1/social/matches/{matchId}/communication-evidence`
- **Actor:** M4 Communication Service or authorized client initiating conversation.
- **Validation Pipeline:**
  1. Authoritative verification that `matchId` exists and is active.
  2. Live check: Neither participant has blocked the other.
  3. Live check: Both profiles remain opted-in and active.
- **Response DTO (`200 OK`):**
  ```json
  {
    "matchId": "018d9f4e-3333-7000-8000-000000000003",
    "isAuthorized": true,
    "validUntilUtc": "2026-11-23T17:00:00Z",
    "conversationScope": "DirectMessagingOnly"
  }
  ```
- **Denial / Errors:** `403 Forbidden` (`MatchNotActive`, `Blocked`, `ProfileInactive`).
- **Freshness & Revocation Caveat:** Real-time cross-service revocation delivery remains an unapproved integration dependency pending M4/M6 review.

---

## 4. Proposed Database Schema (PROPOSED / UNAPPROVED)

> [!WARNING]
> All physical table definitions, status values, uniqueness constraints, and retention policies below are **PROPOSED AND UNAPPROVED**. No database migration or table creation is authorized by this document.

```sql
-- PROPOSED / UNAPPROVED: Private Questionnaire Responses
CREATE TABLE IF NOT EXISTS social_questionnaire_responses (
    response_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id UUID NOT NULL,
    definition_id UUID NOT NULL,
    encrypted_answers JSONB NOT NULL,
    is_skipped BOOLEAN NOT NULL DEFAULT FALSE,
    submitted_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_profile_questionnaire UNIQUE (profile_id, definition_id)
);

-- PROPOSED / UNAPPROVED: Directional Interests (Likes / Passes)
CREATE TABLE IF NOT EXISTS social_directional_interests (
    pair_key VARCHAR(73) NOT NULL, -- Canonical sorted pair string (UUID:UUID)
    acting_profile_id UUID NOT NULL,
    is_positive BOOLEAN NOT NULL,
    recorded_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (pair_key, acting_profile_id)
);

-- PROPOSED / UNAPPROVED: Reciprocal Matches Lifecycle
CREATE TABLE IF NOT EXISTS social_matches (
    match_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    pair_key VARCHAR(73) NOT NULL UNIQUE,
    profile_a_id UUID NOT NULL,
    profile_b_id UUID NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'Active', -- 'Active', 'Unmatched', 'BlockedByA', 'BlockedByB'
    matched_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at_utc TIMESTAMPTZ NULL,
    revocation_reason VARCHAR(64) NULL
);

-- PROPOSED / UNAPPROVED: Match-Gated Communication Authorizations
CREATE TABLE IF NOT EXISTS social_communication_authorizations (
    auth_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    match_id UUID NOT NULL REFERENCES social_matches(match_id) ON DELETE CASCADE,
    granted_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at_utc TIMESTAMPTZ NOT NULL,
    is_revoked BOOLEAN NOT NULL DEFAULT FALSE
);

-- PROPOSED / UNAPPROVED: Query Indexes
CREATE INDEX IF NOT EXISTS idx_social_matches_profiles 
    ON social_matches(profile_a_id, profile_b_id) WHERE status = 'Active';
CREATE INDEX IF NOT EXISTS idx_social_directional_interests_actor 
    ON social_directional_interests(acting_profile_id);
```

---

## 5. Required Approval Gates for Production Activation

Before any route or schema in this contract is enabled in `Program.cs` or registered in DI:

1. **M2 (Identity, Privacy & Entitlements):**
   - Authoritative caller age verification ($\ge 18$).
   - Explicit Social opt-in management.
   - Private module access resolution (`IPrivateModuleAccess.RequireAsync`).
2. **M4 (Communication Lead):**
   - Match-gated conversation token issuance and revocation protocol.
   - Real-time unmatch and block revocation event subscription.
3. **M6 (Security & Moderation Lead):**
   - Bidirectional blocking engine (CF-12).
   - Privacy audit of questionnaire storage and Explore candidate leakage.
   - Abuse detection, rate limits on like actions, and administrative account suspension integration.

---

*End of Proposed Social Integration Contract.*
