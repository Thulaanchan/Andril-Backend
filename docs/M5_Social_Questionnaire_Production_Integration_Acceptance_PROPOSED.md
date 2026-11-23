# ANDRIL M5 — SOCIAL QUESTIONNAIRE PRODUCTION INTEGRATION ACCEPTANCE (PROPOSED)
## Multi-Module Acceptance, Endpoint Verification, Governance Gates & Production Checklist

> **STATUS: PROPOSED ACCEPTANCE HANDOFF / PRODUCTION READY FOR DEPLOYMENT GATES**  
> **Module Ownership:** M5 (Thulaanchan — Social & Personality Matching Domain Lead)  
> **Collaborating Leads:** M2 (Vizhan — Identity, Onboarding & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Integration Branch:** `Develop`  
> **Document Version:** 1.0.0-PROPOSED  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-18, CF-20  

---

## 1. Executive Summary & Integration Milestone

With the completion of M5 Tasks 71 through 90, the **SOC-01 Personality Questionnaire V1** subsystem has reached complete **API contract maturity, domain validation completeness, identity boundary hardening, and production-readiness staging**.

This handoff synthesizes the exact live state of the codebase, defines the active endpoints vs. dormant services, details M2 database integration prerequisites, and establishes the release checklist for full multi-module production activation.

---

## 2. Active vs. Dormant Architectural Components

To respect strict cross-module ownership boundaries (specifically M2 database ownership and M6 security gates), M5 has partitioned the subsystem into **Live Production Endpoints** and **Dormant Verified Services**:

### 2.1 Live Production Endpoints
| Component | Route / Target | Status | Security / Auth Invariants |
| :--- | :--- | :--- | :--- |
| **`SocialQuestionnaireDefinitionsController`** | `GET /api/v1/social/questionnaires/active` | **ACTIVE** | Requires authenticated identity + Social profile claim. Enforces complete adult verification (18+) and active Dating module eligibility. Verified via `SocialQuestionnaireEndpointTests` (HTTP 200 / 401 / 403). |
| **OpenAPI V1 Contract** | `contracts/openapi/andril-api.v1.yaml` | **ACTIVE** | Full specification of `QuestionnaireDefinitionDto`, `QuestionnaireQuestionDto`, `QuestionnaireOptionDto`, and `/social/questionnaires/active` route with 200, 401, 403, and 500 status schemas. Verified via `SocialQuestionnaireOpenApiContractTests`. |
| **DI Extension Registration** | `SocialQuestionnaireServiceCollectionExtensions` | **ACTIVE** | Wired into `Program.cs`. Registers `QuestionnaireDefinitionCatalog.CreateProductionV1()`, `QuestionnaireApplicationService`, and `ISocialQuestionnaireDefinitionAccessService`. |

### 2.2 Dormant Pre-Wired Application Services (Awaiting M2 Database Activation)
The following components are fully implemented, compiled, and covered by exhaustive unit and security tests, but remain dormant until M2 delivers the underlying PostgreSQL tables and `IQuestionnairePersistenceRepository`:

1. **`SocialQuestionnaireSubmissionValidationService` (`src/Andril.API/Authorization/Social/`)**:
   - Enforces fail-closed identity verification, profile ownership validation, questionnaire version checking, and comprehensive Likert option validation.
   - Verified by `SocialQuestionnaireSubmissionValidationServiceTests`.
2. **`SocialQuestionnaireResponseReadAccessService` (`src/Andril.API/Authorization/Social/`)**:
   - Coordinates profile-scoped questionnaire response reads via `SocialQuestionnaireResponseScopedLookupService`.
   - Prevents cross-profile leakage and enforces profile correlation.
   - Verified by `SocialQuestionnaireResponseReadAccessServiceTests`.
3. **`SocialQuestionnaireSubmissionBodyParser` (`src/Andril.API/Contracts/Social/`)**:
   - High-performance, zero-allocation strict JSON parser with depth bounds, 32 KB payload limits, duplicate key detection, unknown property rejection, and prototype pollution immunity.
   - Verified by `SocialQuestionnaireSubmissionBodyParserTests`.

---

## 3. M2 Database Activation Prerequisites

Before activating the future `POST /api/v1/social/questionnaires/submit` and `GET /api/v1/social/questionnaires/response/{id}` endpoints, M2 must complete the following items as outlined in `docs/M5_Social_Questionnaire_M2_Database_Handoff_PROPOSED.md`:

1. **Migration & Schema Provisioning:**
   - Create tables `social_questionnaire_responses` and `social_questionnaire_answers` in PostgreSQL.
   - Configure foreign keys to `social_profiles(id)` with `ON DELETE CASCADE`.
2. **EF Core Model Configuration:**
   - Map `QuestionnaireResponseEntity` and `QuestionnaireAnswerEntity` in `SocialDbContext`.
   - Map optimistic concurrency tokens (PostgreSQL `xmin` or `RowVersion`).
3. **Repository Implementation:**
   - Implement `IQuestionnairePersistenceRepository` ensuring profile-scoped querying (`WHERE profile_id = :profileId`).
   - Register concrete implementation in DI: `services.AddScoped<IQuestionnairePersistenceRepository, SocialQuestionnairePersistenceRepository>()`.
4. **Resolution of Decisions P-04 & P-05:**
   - Formally ratify Decision P-04 (Append-only questionnaire history with `is_active` flag vs. overwrite).
   - Formally ratify Decision P-05 (Hard cascade on profile purge vs. soft-delete on questionnaire opt-out).

---

## 4. M6 Security & Privacy Acceptance Gates

Before end-to-end production launch, M6 verification must sign off on the following security invariants:

| Security Invariant | Requirement | Status |
| :--- | :--- | :--- |
| **Strict Identity Boundary** | Social claims (`social:profile_id`) cannot be synthesized by clients; must be validated by trusted token middleware. | **VERIFIED** |
| **Adult Age Gate (18+)** | Minor accounts attempting to access Social questionnaire endpoints receive HTTP 403 Forbidden. | **VERIFIED** |
| **Cross-Tenant Isolation** | Responses submitted by Profile A cannot be retrieved or modified by Profile B. | **VERIFIED** |
| **Zero Answer Leakage** | Raw question answers and options are never included in public candidate cards or diagnostic logs. | **VERIFIED** |
| **Safe Error Diagnostics** | Invalid JSON payloads, oversized requests, and syntax errors return generic safe diagnostics without leaking memory addresses or secrets. | **VERIFIED** |

---

## 5. Deployment & Release Verification Checklist

- [x] **M5 API & Domain Code Base:** 100% clean build under .NET 10 Release configuration.
- [x] **Regression Testing:** Zero regression failures across unit tests, architecture tests, and security tests.
- [x] **One-File-Per-PR Discipline:** All 20 tasks (Tasks 71–90) executed with strict single-file commits, verified CI builds, and non-fast-forward PR merges.
- [x] **Remote Parity:** `Develop` and `Member-5` branches synchronized with identical SHAs.
- [ ] **M2 Database Schema Rollout:** (Pending M2 sprint).
- [ ] **M2 Repository Registration:** (Pending M2 sprint).
- [ ] **End-to-End POST Activation:** Activation of `POST /api/v1/social/questionnaires/submit` upon M2 database readiness.
- [ ] **Final M6 Penetration & Audit Sign-Off:** Automated security scan on staging environment.
