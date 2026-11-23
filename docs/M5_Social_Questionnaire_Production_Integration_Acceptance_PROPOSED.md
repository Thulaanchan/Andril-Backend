# ANDRIL M5 — SOCIAL QUESTIONNAIRE PRODUCTION INTEGRATION ACCEPTANCE (PROPOSED)
## Multi-Module Acceptance, Endpoint Verification, Governance Gates & Production Readiness Status

> **STATUS: PROPOSED ACCEPTANCE HANDOFF / READINESS GATES PENDING M2 PERSISTENCE**  
> **Module Ownership:** M5 (Thulaanchan — Social & Personality Matching Domain Lead)  
> **Collaborating Leads:** M2 (Vizhan — Identity, Onboarding & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Integration Branch:** `Develop`  
> **Document Version:** 1.1.0-PROPOSED  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-18, CF-20, Decision P-04  

---

## 1. Executive Summary & Current Verification State

With the completion of M5 Tasks 71 through 90, the **SOC-01 Personality Questionnaire V1** domain logic, contract validation, and definition endpoint have been staged and verified against test doubles.

### Critical Verification Distinctions:
1. **HTTP Endpoint Mapped in Source:** `GET /api/v1/social/questionnaires/active` is registered in `SocialQuestionnaireDefinitionsController`.
2. **Tested With Test Doubles Only:** Endpoint security and authorization flows have been verified in-memory using ASP.NET Core `WebApplicationFactory` with stubbed identity and profile services.
3. **Production Deployment Unverified:** No live cloud, staging, or production deployments have been executed or verified.
4. **Questionnaire Persistence Not Implemented:** Relational database storage for questionnaire responses and answers does not exist.
5. **PostgreSQL Cross-Profile Isolation Not Verified:** Physical database tenancy and row-level profile isolation cannot be tested until M2 provisions the database schema.
6. **Submission and Read Routes Not Active:** There are **no active HTTP routes** for questionnaire submission (`POST`) or individual response retrieval (`GET /response/{id}`); these capabilities exist solely as internal application/contract services.
7. **M2 Database Work Pending:** Database schema authoring, EF Core mappings, and repository implementation remain pending under M2 ownership.
8. **Final M6 Security Acceptance Pending:** Formal staging penetration testing and end-to-end security sign-off remain pending under M6 governance.

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

---

## 3. M2 Database Activation Prerequisites

Before any future submission (`POST`) or response read (`GET`) routes can be mapped in API controllers, M2 must deliver the following persistence prerequisites (detailed in `docs/M5_Social_Questionnaire_M2_Database_Handoff_PROPOSED.md`):

1. **Schema & Migration Provisioning:**
   - Author EF Core migration creating proposed tables `social_questionnaire_responses` and `social_questionnaire_answers`.
   - Establish referential integrity against M2's actual profile table (`andril_identity.private_module_profiles`).
2. **Repository Implementation:**
   - Implement `IQuestionnairePersistenceRepository` ensuring profile-scoped data access (`WHERE profile_id = :profileId`).
   - Register concrete repository in dependency injection.
3. **Resolution of BRD v2.4-R1 Decision P-04:**
   - Formal resolution by Product Lead, M2, and M6 regarding questionnaire response replacement (overwrite vs. versioned append-only) and retention/erasure rules.
   - *(Note: BRD v2.4-R1 Decision P-05 governs CF-20 use in Learning/Hobbies and does not apply to Social questionnaire retention).*

---

## 4. M6 Security & Privacy Acceptance Gates (Pending Production Verification)

The following governance gates must be satisfied prior to general production release:

| Gate | Requirement | Verification Method | Status |
| :--- | :--- | :--- | :---: |
| **Route Protection** | Only authenticated, adult accounts with active Social profiles may retrieve the active definition. | In-memory test doubles | **Tested in Test Suite** |
| **Physical Tenant Isolation** | Cross-profile retrieval must be prevented at the database query level. | Integration tests against PostgreSQL | **PENDING M2 DATABASE** |
| **Answer Privacy Boundary** | Raw questionnaire answers must never appear in public candidate cards, Explore feeds, or application logs. | Code review & contract audit | **VERIFIED IN DOMAIN CODE** |
| **Safe Error Handling** | Malformed payloads, oversized requests, and syntax errors return generic client diagnostics without leaking internal state. | Security test suite | **TESTED IN TEST SUITE** |
| **Production Penetration Audit** | End-to-end verification of authentication, rate limiting, and network boundaries in staging environment. | M6 staging penetration test | **PENDING STAGING DEPLOYMENT** |

---

## 5. Deployment Readiness Checklist

- [x] **M5 API & Domain Code:** Builds clean with zero errors under .NET 10 Release configuration.
- [x] **Regression Suite:** Passing unit tests, security tests, and architecture tests.
- [x] **Branch Parity:** `Develop` and `Member-5` synchronized with identical commit SHAs.
- [ ] **M2 Relational Persistence:** (Pending M2 sprint).
- [ ] **PostgreSQL Tenancy Verification:** (Pending M2 integration tests with live database).
- [ ] **Decision P-04 Formal Ratification:** (Pending Product Lead / M2 / M6 approval).
- [ ] **Activation of Submission Routes:** (Blocked on M2 database readiness).
- [ ] **M6 Production Security Sign-Off:** (Pending staging environment audit).
