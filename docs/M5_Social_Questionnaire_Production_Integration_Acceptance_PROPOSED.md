# ANDRIL M5 — SOCIAL QUESTIONNAIRE PRODUCTION INTEGRATION ACCEPTANCE
## Multi-Module Acceptance, Endpoint Verification, Governance Gates & Delegated Database Implementation

> **STATUS: DELEGATED IMPLEMENTATION SPECIFICATION / PRODUCTION STAGING**  
> **Module Ownership:** M5 (Thulaanchan — Social & Personality Matching Domain Lead)  
> **Collaborating Leads:** M2 (Vizhan — Identity, Onboarding & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Integration Branch:** `Develop`  
> **Document Version:** 1.2.0  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-18, CF-20, Decision P-04  

---

## 1. Executive Summary & Verification State

With the completion of M5 Tasks 71 through 90, the **SOC-01 Personality Questionnaire V1** domain logic, contract validation, and definition endpoint have been staged and verified against test doubles. Following formal delegation from M2 (Identity & Database Owner), M5 is implementing the isolated relational database persistence layer (`SocialDbContext`, migrations, repositories, and integration tests) under Tasks 91–110.

### Critical Verification Distinctions:
1. **HTTP Endpoint Mapped in Source:** `GET /api/v1/social/questionnaires/active` is registered in `SocialQuestionnaireDefinitionsController`.
2. **Tested With Test Doubles Only:** Definition endpoint security and authorization flows have been verified in-memory using ASP.NET Core `WebApplicationFactory` with stubbed identity and profile services.
3. **Production Deployment Unverified:** No live cloud, staging, or production deployments have been executed or verified.
4. **Questionnaire Persistence Delegated to M5:** Relational database storage for questionnaire responses and answers is being implemented by M5 via a dedicated `SocialDbContext` under M2 delegation.
5. **PostgreSQL Tenancy Verification Required:** Physical database tenancy and row-level profile isolation must be tested using real isolated PostgreSQL test fixtures.
6. **Submission and Read Routes Not Active:** There are **no active HTTP routes** for questionnaire submission (`POST`) or individual response retrieval (`GET /response/{id}`); these capabilities exist solely as internal application/contract services.
7. **Preserved Multi-Module Governance:** M2 preserves exclusive authority over the shared `andril_identity` schema, accounts, and cross-module database integration. M6 preserves security review, penetration testing, and release authorization.
8. **No Live Production Migration:** Source implementation does not authorize or execute live database migrations against production Supabase instances without separate verified release authorization.

---

## 2. Component Status Ledger

| Component | Target / Route | Current Codebase Status | Verification Evidence |
| :--- | :--- | :---: | :--- |
| **`SocialQuestionnaireDefinitionsController`** | `GET /api/v1/social/questionnaires/active` | **Mapped in API** | In-memory integration tests (`SocialQuestionnaireEndpointTests`) verify HTTP 200 (eligible adult), HTTP 401 (unauthenticated), and HTTP 403 (minor or missing Social profile) using test doubles. Production deployment unverified. |
| **OpenAPI V1 Contract** | `contracts/openapi/andril-api.v1.yaml` | **Documented in Contract** | Syntactically verified via `SocialQuestionnaireOpenApiContractTests`. Documents `QuestionnaireDefinitionDto` and `/social/questionnaires/active`. |
| **DI Extension Registration** | `SocialQuestionnaireServiceCollectionExtensions` | **Registered in Program.cs** | Verified via solution build and startup DI resolution tests. |
| **`SocialQuestionnaireSubmissionValidationService`** | Internal Service | **Dormant / Unmapped** | Unit tested via `SocialQuestionnaireSubmissionValidationServiceTests`. Enforces domain validation rules. Awaits database submission route activation. |
| **`SocialQuestionnaireResponseReadAccessService`** | Internal Service | **Dormant / Unmapped** | Unit tested via `SocialQuestionnaireResponseReadAccessServiceTests`. Enforces profile scope correlation. Awaits database read route activation. |
| **`SocialQuestionnaireSubmissionBodyParser`** | Internal Contract Parser | **Dormant / Unmapped** | Unit tested via `SocialQuestionnaireSubmissionBodyParserTests`. Validates bounds (32 KB limit, depth 6), rejects duplicate keys, and disallows prototype pollution properties. |
| **`SocialDbContext` & Migrations** | Relational Persistence | **In Development (M5 Tasks 94–106)** | Dedicated schema `andril_social` providing atomic questionnaire response persistence under M2 delegation. |

---

## 3. Delegated Persistence Prerequisites & Security Invariants

In implementing Tasks 94 through 106, M5 must enforce the following architectural and security invariants:

1. **Schema Isolation:**
   - Physical tables reside in a dedicated schema (`andril_social`), preventing namespace collisions with `andril_identity` or `andril_hobbies`.
2. **Social Profile Module Verification:**
   - A foreign key to `andril_identity.private_module_profiles(id)` does not independently guarantee that the referenced profile is a Social profile.
   - The persistence adapter and repository must verify at the application/query boundary that the referenced profile has `module = 'social'`.
3. **Fail-Closed Repository Boundary:**
   - Concrete `SocialQuestionnairePersistenceRepository` must enforce profile scoping on all reads and writes. Missing profiles, unauthorized scopes, or timed-out persistence calls must fail closed without disclosing private state.
4. **Resolution of BRD v2.4-R1 Decision P-04:**
   - Questionnaire replacement policy (in-place overwrite vs. versioned append-only) and retention/erasure rules remain **PENDING** formal approval by Product Lead, M2, and M6.
   - Foundational schema must not pre-empt or hardcode unapproved retention policies.

---

## 4. M6 Security & Privacy Acceptance Gates (Pending Production Verification)

The following governance gates must be satisfied prior to general production release:

| Gate | Requirement | Verification Method | Status |
| :--- | :--- | :--- | :---: |
| **Route Protection** | Only authenticated, adult accounts with active Social profiles may retrieve the active definition. | In-memory test doubles | **Tested in Test Suite** |
| **Physical Tenant Isolation** | Cross-profile retrieval must be prevented at the database query level. | Integration tests against PostgreSQL | **IN PROGRESS (Task 98/104/106)** |
| **Answer Privacy Boundary** | Raw questionnaire answers must never appear in public candidate cards, Explore feeds, or application logs. | Code review & contract audit | **VERIFIED IN DOMAIN CODE** |
| **Safe Error Handling** | Malformed payloads, oversized requests, and syntax errors return generic client diagnostics without leaking internal state. | Security test suite | **TESTED IN TEST SUITE** |
| **Production Penetration Audit** | End-to-end verification of authentication, rate limiting, and network boundaries in staging environment. | M6 staging penetration test | **PENDING STAGING DEPLOYMENT** |

---

## 5. Deployment Readiness Checklist

- [x] **M5 API & Domain Code:** Builds clean with zero errors under .NET 10 Release configuration.
- [x] **Regression Suite:** Passing unit tests, security tests, and architecture tests.
- [x] **Branch Parity:** `Develop` and `Member-5` synchronized with identical commit SHAs.
- [ ] **M5 Delegated Relational Persistence:** (In progress Tasks 94–106).
- [ ] **PostgreSQL Tenancy Verification:** (In progress Task 98/104/106).
- [ ] **Decision P-04 Formal Ratification:** (Pending Product Lead / M2 / M6 approval).
- [ ] **Activation of Submission Routes:** (Blocked on complete database persistence verification).
- [ ] **M6 Production Security Sign-Off:** (Pending staging environment audit).
