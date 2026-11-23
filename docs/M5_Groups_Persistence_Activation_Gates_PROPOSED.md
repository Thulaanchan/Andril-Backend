# ANDRIL M5 — SHARED GROUPS PRODUCTION PERSISTENCE ACTIVATION GATES (PROPOSED)
## Comprehensive Database Architecture, Capacity Concurrency & Cross-Member Integration Plan

> **STATUS: PROPOSED ACTIVATION PLAN / NOT IMPLEMENTED**  
> **Author:** M5 (Thulaanchan — Shared Groups & Social Backend Lead)  
> **Document Version:** 1.0.0-PROPOSED  
> **Target Release:** ANDRIL v1.0  
> **Compliance:** BRD v2.4-R1 §07.1, §06.1, §01.2; CF-01, CF-06, CF-07, CF-11, CF-12  
> **Cross-Member Approval Owners:** M1 (Learning), M2 (Identity/Entitlements), M3 (Hobbies), M4 (Communication), M6 (Moderation/Security)

---

## 1. Executive Summary & Activation Boundary

This document establishes the **decision-ready engineering roadmap and governance gates** required to implement relational database persistence, atomic capacity concurrency, and active production services for the unified Shared Groups Backend (`Andril.Groups`).

### Mandatory Operational Boundaries:
1. **Zero Database Activation in this Task:** This document creates no database migrations, runs no schema scripts, registers no EF Core DbContexts in production DI, and connects to no persistent databases.
2. **Offline Domain vs. Concurrent Production:** M5's offline group policies (`HobbiesClubAdmissionCapacityPolicy`, `HobbiesClubCreationQuotaPolicy`, `HobbiesClubAdmissionPreflightService`) provide read-only validation. Live concurrent group admissions require atomic database transactions with serializable or row-level lock isolation.
3. **Current Domain Model Gaps (Explicitly Acknowledged):**
   - Current domain entities (`GroupEntry`, `GroupMembership`, `GroupJoinRequest`) are minimal identity and scope representations.
   - They do **NOT** implement role hierarchies (e.g., `Leader`, `Admin`, `Member`) or join-request lifecycle statuses (e.g., `Pending`, `Approved`, `Rejected`, `Expired`).
   - Rich lifecycle behavior, state transitions, and administrative roles exist only as proposals and must be formally approved before physical database modeling.

---

## 2. Core Relational Architecture & Technical Specifications

### 2.1 Module-Scoped Partitioning & Group Kinds
- **Isolation Rule:** Exactly three non-collapsible module scopes enforced by physical check constraints:
  - `learning` $\rightarrow$ `GroupKind.Classroom` (M1 integration)
  - `hobbies` $\rightarrow$ `GroupKind.Club` (M3 integration)
  - `social` $\rightarrow$ `GroupKind.Zone` (M5 integration)
- **Constraint Proposal:**
  ```sql
  CONSTRAINT chk_group_module_kind CHECK (
      (module = 'learning' AND group_kind = 1) OR
      (module = 'hobbies' AND group_kind = 2) OR
      (module = 'social' AND group_kind = 3)
  );
  ```

### 2.2 Hobbies Club Quotas, Capacity & Entitlements (CONFIRMED)
- **Creation Quota:**
  - **Free Tier:** Strictly capped at **5 active Clubs** per profile.
  - **Premium Tier:** **Unlimited** active Clubs.
- **Admission Capacity:**
  - **Free Tier Club:** Strictly capped at **50 members**.
  - **Premium Tier Club:** Strictly capped at **100 members**.
- **Entitlement Verification:** Quota and capacity tier verification strictly requires authoritative M2 Premium entitlement verification.
- **Downgrade / Expiry Policy:**
  - If a club creator's Premium subscription expires, existing members are retained (no eviction).
  - New admissions are blocked if the club's current membership count meets or exceeds the Free limit (50).

### 2.3 Atomic Admission Concurrency & Row-Level Locking
- In high-concurrency admission scenarios (e.g., popular clubs filling the final slot), offline preflight checks are vulnerable to race conditions (TOCTOU).
- Production admissions must use `SERIALIZABLE` isolation or `SELECT ... FOR UPDATE` row-level locks on the target group record:
  ```sql
  BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;
  -- 1. Lock group record and check capacity
  SELECT current_member_count, tier FROM groups WHERE group_id = :groupId FOR UPDATE;
  
  -- 2. Verify capacity against tier
  -- 3. Transition join request status
  UPDATE group_join_requests SET status = 'Approved', reviewed_at_utc = NOW() 
  WHERE request_id = :requestId AND status = 'Pending';
  
  -- 4. Insert membership
  INSERT INTO group_memberships (group_id, module, profile_id, role, joined_at_utc)
  VALUES (:groupId, :module, :requestingProfileId, 'Member', NOW());
  
  -- 5. Atomically increment member count
  UPDATE groups SET current_member_count = current_member_count + 1 WHERE group_id = :groupId;
  COMMIT;
  ```

### 2.4 Learning Classrooms & Social Zones Isolation (PENDING)
- **Non-Generalization Invariant:** Hobbies Club limits must **NOT** be generalized to Classrooms or Zones.
- **Learning Classrooms:** Capacities and educator roles remain pending M1 pedagogical specification.
- **Social Zones:** Roles, joining rules, and quotas remain strictly PENDING under BRD v2.4-R1 Decision P-06.

### 2.5 Inactivity, Archival & Soft Deletion (PENDING)
- Group closure via `DELETE /api/v1/{module}/groups/{groupId}` marks `is_deleted = true`.
- Automated detection of inactive/dormant groups, retention windows, and restoration policies remain strictly PENDING under BRD v2.4-R1 Decision P-07.

---

## 3. Requirements Classification & Status Matrix

| Area | Component | Implementation Status | Classification | Governance / Decision Owner |
|---|---|---|---|---|
| **Hobbies** | Club Creation Quota (5 Free / Unlimited Premium) | Implemented in `HobbiesClubCreationQuotaPolicy` | **CONFIRMED** | BRD §07.1 / M3 / M2 |
| **Hobbies** | Club Capacity (50 Free / 100 Premium) | Implemented in `HobbiesClubAdmissionCapacityPolicy` | **CONFIRMED** | BRD §07.1 / M3 |
| **Hobbies** | Premium Expiry Retention Policy | Specified in contract | **CONFIRMED** | BRD §07.1 / M2 / M3 |
| **Identity** | Premium Entitlement Resolver | Not implemented in M2 | **PENDING APPROVAL** | M2 Entitlements Lead |
| **Groups** | Minimal Group Identity & Scope | Implemented in `GroupEntry.cs` | **CONFIRMED** (Domain) | M5 Backend |
| **Groups** | Group Description, Creator, Counts, Tier | Not implemented on domain entity | **PROPOSED** | M5 / M3 / M1 |
| **Groups** | Group Membership Identity | Implemented in `GroupMembership.cs` | **CONFIRMED** (Domain) | M5 Backend |
| **Groups** | Group Role Hierarchies (Leader/Admin/Member) | Not implemented on domain entity | **PROPOSED** | M5 / M3 / M1 / M6 |
| **Groups** | Join Request Identity | Implemented in `GroupJoinRequest.cs` | **CONFIRMED** (Domain) | M5 Backend |
| **Groups** | Join Request Status Lifecycle | Not implemented on domain entity | **PROPOSED** | M5 / M3 / M1 |
| **Learning** | Classroom Capacity and Teacher Roles | Not specified | **PENDING APPROVAL** | M1 Learning Lead |
| **Social** | Zone Roles, Joining, and Quotas | Not approved | **PENDING (P-06)** | Product Lead / M5 |
| **Lifecycle** | Inactivity Archiving & Restoration | Not approved | **PENDING (P-07)** | Product Lead / M6 |
| **Database** | `GroupsDbContext` & PostgreSQL Tables | Not implemented | **PROPOSED** | M2 / M6 Data Architecture |
| **Messaging** | Group Chat Channel & Membership Sync | Not implemented | **PENDING APPROVAL** | M4 Communication Lead |
| **Security** | Group Abuse Reporting & Admin Takedown | Not implemented | **PENDING APPROVAL** | M6 Moderation Lead |

---

## 4. Implementation Order for Future Approved Tasks

When formal governance sign-off is granted by M1, M2, M3, M4, and M6, implementation must proceed in the following ordered sequence:

### Step 1: Domain Entity Enrichment
1. Enrich `GroupEntry.cs` with `Description`, `CreatorProfileId`, `IsDiscoverable`, `IsDeleted`, `CurrentMemberCount`, and `Tier` while maintaining serialization privacy.
2. Add `GroupRole` enum (`Leader`, `Admin`, `Member`) and property to `GroupMembership.cs`.
3. Add `GroupJoinRequestStatus` enum (`Pending`, `Approved`, `Rejected`, `Expired`) and property to `GroupJoinRequest.cs`.
4. Update unit and serialization boundary tests to cover enriched domain properties.

### Step 2: Relational Persistence Layer (`src/Andril.Persistence`)
1. Create isolated `GroupsDbContext.cs` inheriting from EF Core `DbContext`.
2. Author Entity Type Configurations (`GroupConfiguration`, `GroupMembershipConfiguration`, `GroupJoinRequestConfiguration`) specifying composite primary keys, check constraints, and filtered indexes.
3. Implement concrete EF Core repository adapters:
   - `GroupEntryRepository.cs` implementing `IGroupEntryRepository`.
   - `GroupMembershipRepository.cs` implementing `IGroupMembershipRepository`.
   - `GroupJoinRequestRepository.cs` implementing `IGroupJoinRequestRepository`.

### Step 3: Relational Integration Test Suite
1. Author ephemeral PostgreSQL / Testcontainers integration tests in `tests/Andril.IntegrationTests/Database/`.
2. Validate serializable admission concurrency, quota enforcement, and unique constraints under concurrent execution.

### Step 4: Migration Generation
1. Generate initial EF Core migration `InitialGroups` targeting PostgreSQL (only after schema sign-off).
2. Verify migration script safety, idempotent index creation, and foreign key cascade rules.

### Step 5: Service Layer & API Controller Activation (`src/Andril.API`)
1. Implement Group management services enforcing M2 identity and M6 block verification.
2. Scaffold `GroupsController.cs` in `src/Andril.API/Controllers/Groups/`.
3. Register Group services and `GroupsDbContext` in `Program.cs` and DI extensions.

---

*End of Proposed Groups Persistence Activation Gates Plan.*
