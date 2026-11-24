# ANDRIL M5 — SOCIAL MIGRATION APPROVAL GATE
## Pre-Migration Governance Audit, Invariant Verification & Decision P-04 Sign-Off

> **STATUS: APPROVED PRE-MIGRATION GATE**  
> **Module Ownership:** M5 (Thulaanchan — Social & Personality Matching Domain Lead)  
> **Collaborating Leads:** M2 (Vizhan — Identity & Database Owner), M6 (Lucina — Security & Moderation Lead)  
> **Integration Branch:** `Develop`  
> **Document Version:** 1.0.0  
> **Compliance:** BRD v2.4-R1 §16.2, §04.1, §05.1; SOC-01, CF-04, CF-11, CF-18, CF-20, Decision P-04  

---

## 1. Executive Summary & Objective

Prior to scaffolding the initial Entity Framework Core relational migration for the Social module (`InitialSocialQuestionnaire` in Task 102), this document formally records the **pre-migration governance audit** and **approval gate sign-off**.

Under delegated authority from **M2 (Vizhan)**, M5 is responsible for implementing the isolated relational database persistence layer (`SocialDbContext`, entity configurations, migrations, repositories, and integration tests).

This gate confirms that:
1. All physical table and schema definitions are policy-neutral and do not pre-empt pending governance decisions.
2. Migration history is strictly partitioned within the `andril_social` schema namespace.
3. Design-time scaffolding executes in a completely offline sandbox without live database dependencies.
4. No migrations will be applied to live staging or production databases without separate verified deployment authorization.

---

## 2. Governance Invariant Verification Matrix

| Gate # | Governance Dimension | Verification Standard | Audit Finding & Actual Source State | Gate Status |
| :---: | :--- | :--- | :--- | :---: |
| **G-01** | **Dedicated Schema Namespace** | Dedicated schema `andril_social` completely partitioned from `andril_identity` and `andril_hobbies`. | `SocialDbContext.Schema` is constant `"andril_social"`. Generated DDL and mappings target only `andril_social`. | **PASSED** (Implemented) |
| **G-02** | **Migrations History Isolation** | Migration tracking table isolated within `andril_social` to prevent cross-module collisions. | Configured via `SocialDbContextFactory` to `andril_social.__EFMigrationsHistory_Social`. Verified by unit tests. | **PASSED** (Implemented) |
| **G-03** | **Decision P-04 Neutrality** | Migration does not embed unapproved retention timers, automated purging routines, or hardcoded overwrite/append triggers. | Schema maps pure structural snapshot entities. Repository rejects destructive overwrites pending P-04. | **PASSED** (Implemented) |
| **G-04** | **Identity Profile Integrity** | Foreign key references `andril_identity.private_module_profiles(id)` with cascading deletion. | Column `profile_id` is enforced via non-empty check constraint `ck_questionnaire_profile_id`. Physical FK constraint is pending M2 establishing a unique alternate key on `(id, module)` in `andril_identity.private_module_profiles`. | **PENDING M2 SPEC** (Design Approved, Schema Pending) |
| **G-05** | **Offline Scaffolding Safety** | Design-time factory cannot connect to live or staging network endpoints. | `SocialDbContextFactory` installs `OfflineConnectionGuard` throwing `InvalidOperationException` on any connection attempt. | **PASSED** (Implemented) |
| **G-06** | **Zero Production Mutation** | Implementation does not execute live migration against production Supabase instances. | Scope restricted to source code migrations and ephemeral local test fixtures. | **PASSED** (Implemented) |

---

## 3. Physical Schema Specification Sign-Off

### 3.1 Schema: `andril_social`

```sql
CREATE SCHEMA IF NOT EXISTS andril_social;
```

### 3.2 Table: `andril_social.social_questionnaire_responses`
- **Columns:**
  - `id` (UUID, Primary Key, Non-empty check constraint `ck_questionnaire_response_id`)
  - `profile_id` (UUID, Non-empty check constraint `ck_questionnaire_profile_id`; physical composite FK to `andril_identity.private_module_profiles(id, module)` pending M2 key coordination)
  - `version` (VARCHAR(32), Non-null)
  - `status` (VARCHAR(32), Enum conversion, Non-null)
  - `created_at_utc` (TIMESTAMPTZ, Finite check constraint `ck_questionnaire_timestamp`)
  - `row_version` (BYTEA in Initial migration; native PostgreSQL concurrency via Npgsql `xmin` pending additive migration R05)
- **Indexes:**
  - `ix_social_questionnaire_profile_version` ON `(profile_id, version)`

### 3.3 Table: `andril_social.social_questionnaire_answers`
- **Columns:**
  - `id` (UUID, Primary Key, Non-empty check constraint `ck_questionnaire_answer_id`)
  - `response_id` (UUID, Foreign Key to `andril_social.social_questionnaire_responses(id)` ON DELETE CASCADE, Non-empty check constraint `ck_questionnaire_answer_response_id`)
  - `question_id` (VARCHAR(128), Non-null)
  - `option_id` (VARCHAR(128), Non-null)
  - `answered_at_utc` (TIMESTAMPTZ, Finite check constraint `ck_questionnaire_answer_timestamp`)
- **Indexes:**
  - `ix_social_questionnaire_answers_response` ON `(response_id)`

---

## 4. Authorization Sign-Off

- **M5 Lead (Thulaanchan):** Approved for Task 102 migration generation.
- **M2 Delegation Reference:** Formally delegated under M5 Tasks 91–110 directive for isolated Social persistence.
- **M6 Security Controls:** Fail-closed offline connection guard, schema isolation, and tenant boundary verified.
