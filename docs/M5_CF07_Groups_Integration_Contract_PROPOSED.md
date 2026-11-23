# ANDRIL M5 — CF-07 SHARED GROUPS BACKEND INTEGRATION CONTRACT (PROPOSED)
## Multi-Module Integration Specification: Learning, Hobbies & Social

> **STATUS: PROPOSAL / NOT IMPLEMENTED**  
> **Author:** M5 (Thulaanchan — Shared Groups & Social Backend Lead)  
> **Document Version:** 1.0.0-PROPOSED  
> **Target Release:** ANDRIL v1.0  
> **Compliance:** BRD v2.4-R1 §07.1, §06.1, §01.2; CF-01, CF-06, CF-07, CF-11, CF-12  
> **Cross-Member Dependencies:** M1 (Learning), M2 (Identity/Entitlements), M3 (Hobbies), M4 (Communication), M6 (Moderation/Security)

---

## 1. Executive Summary & Proposal Boundary

This document outlines the **architectural, API, and relational database contract proposal** for the unified Shared Groups Backend (`Andril.Groups`). It specifies the concrete interfaces, transaction boundaries, capacity enforcement rules, and approval gates required to transition M5's offline group domain policies into active, persistent production endpoints.

### Key Operational Rules:
1. **Status:** This document is an integration proposal. **No endpoints are active, no migrations are applied, and no controllers or DI services are registered in this task.**
2. **Offline Domain vs. Concurrent Production:** Offline policies (such as `HobbiesClubAdmissionPreflightService` and `HobbiesClubAdmissionCapacityPolicy`) provide read-only validation. **They are necessary but not sufficient for live concurrent admissions.** Live admissions require atomic database transactions with serializable/row-level lock isolation.
3. **Module Isolation:** Exactly three private, non-collapsible modules:
   - `learning` $\rightarrow$ `GroupKind.Classroom` (M1 integration)
   - `hobbies` $\rightarrow$ `GroupKind.Club` (M3 integration)
   - `social` $\rightarrow$ `GroupKind.Zone` (M5 integration)
4. **Approval Prerequisites:** Activation of any endpoint or schema defined herein strictly requires signed reviews from M2 (identity & entitlements), M3 (Hobbies Club experience), M4 (group messaging), and M6 (security review).

---

## 2. Confirmed Module Quotas, Capacities & Entitlements

### 2.1 Hobbies Clubs (CF-07 / BRD §07.1 confirmed limits)
- **Club Creation Quota:**
  - **Free Tier:** Strictly limited to **5 active Clubs** per profile.
  - **Premium Tier:** **Unlimited** active Clubs.
- **Club Admission Capacity:**
  - **Free Tier Club:** Strictly capped at **50 members**.
  - **Premium Tier Club:** Strictly capped at **100 members**.
- **Premium Expiry / Downgrade Policy:**
  - Existing members are retained (no eviction).
  - New admissions are blocked if the club's current membership count meets or exceeds the Free limit (50).

### 2.2 Learning Classrooms & Social Zones Isolation
- **Non-Generalization Rule:** Hobbies Club creation quotas and membership limits **must NOT be generalized** to Learning Classrooms or Social Zones.
- **Classroom Limits:** Configured under M1 pedagogical rules (pending M1 specification).
- **Social Zone Limits:** Roles, quotas, and retention remain PENDING product/BRD approval (no invented Zone role hierarchies or retention policies).

---

## 3. Proposed Method/Path Inventory & Operation Contracts

The table below summarizes the 10 proposed operations for group lifecycle management:

| # | Operation | Method | Proposed Path | Scope / Kind | Owner | Approval Status |
|---|---|---|---|---|---|---|
| 1 | Group Creation | `POST` | `/api/v1/{module}/groups` | Scoped (`Classroom`/`Club`/`Zone`) | M5 / Caller Module | PROPOSED |
| 2 | Owner Group Retrieval | `GET` | `/api/v1/{module}/groups/{groupId}` | Scoped | M5 | PROPOSED |
| 3 | Authorized Discovery | `GET` | `/api/v1/{module}/groups` | Scoped (Public/Discoverable) | M5 | PROPOSED |
| 4 | Group Configuration | `PATCH` | `/api/v1/{module}/groups/{groupId}` | Scoped | M5 | PROPOSED |
| 5 | Join Request Creation | `POST` | `/api/v1/{module}/groups/{groupId}/join-requests` | Scoped | M5 | PROPOSED |
| 6 | Join Request Status | `GET` | `/api/v1/{module}/groups/{groupId}/join-requests/{requestId}` | Scoped | M5 | PROPOSED |
| 7 | Admission Decision | `POST` | `/api/v1/{module}/groups/{groupId}/join-requests/{requestId}/decision` | Scoped | M5 | PROPOSED |
| 8 | Membership Inspection | `GET` | `/api/v1/{module}/groups/{groupId}/members` | Scoped | M5 | PROPOSED |
| 9 | Leave Group | `POST` | `/api/v1/{module}/groups/{groupId}/leave` | Scoped | M5 | PROPOSED |
| 10 | Group Soft Deletion | `DELETE` | `/api/v1/{module}/groups/{groupId}` | Scoped | M5 | PROPOSED |

---

### Detailed Contract Specifications

#### Op 1: Group Creation
- **Requirement ID:** CF-07.1 / CF-06.1
- **Owner:** M5 (Shared Backend), invoked by M1/M3/M5 frontends.
- **HTTP Method & Path:** `POST /api/v1/{module}/groups`
- **Actor & Authentication:** Authenticated user with valid Bearer JWT.
- **Module-Profile Ownership:** Caller must own a verified profile within `{module}` (M2 validated).
- **Group Kind & Scope:** Exactly matches `{module}` (`learning:Classroom`, `hobbies:Club`, `social:Zone`).
- **Preconditions:**
  - Caller profile active.
  - For `hobbies`: Verified against `HobbiesClubCreationQuotaPolicy` (Free: $\le 5$, Premium: unlimited).
- **Request DTO:**
  ```json
  {
    "name": "Robotics Enthusiasts",
    "description": "Weekly robotics building club.",
    "isDiscoverable": true
  }
  ```
- **Response DTO (`201 Created`):**
  ```json
  {
    "groupId": "018d9f4e-1111-7000-8000-000000000001",
    "module": "hobbies",
    "groupKind": "Club",
    "name": "Robotics Enthusiasts",
    "createdAtUtc": "2026-11-23T16:00:00Z"
  }
  ```
- **Denial / Errors:**
  - `401 Unauthorized`: Missing or invalid token.
  - `403 Forbidden`: Creation quota reached or profile inactive.
  - `400 Bad Request`: Incompatible scope or invalid name.
- **Database Transaction:** Atomic insert into `groups` table and initial `group_memberships` (owner/leader role).

#### Op 2: Owner-Scoped Group Retrieval
- **Requirement ID:** CF-07.2
- **Owner:** M5
- **HTTP Method & Path:** `GET /api/v1/{module}/groups/{groupId}`
- **Actor:** Authenticated caller.
- **Authorization:** Caller must be a confirmed member of `groupId` within `{module}` or group must be discoverable.
- **Response DTO (`200 OK`):**
  ```json
  {
    "groupId": "018d9f4e-1111-7000-8000-000000000001",
    "module": "hobbies",
    "groupKind": "Club",
    "name": "Robotics Enthusiasts",
    "memberCount": 24,
    "capacity": 50,
    "role": "Leader"
  }
  ```
- **Errors:** `404 Not Found` (conceals group existence if caller has no visibility).

#### Op 3: Authorized Group Discovery
- **Requirement ID:** CF-06.1 / CF-07
- **Owner:** M5
- **HTTP Method & Path:** `GET /api/v1/{module}/groups?query={query}&page={page}`
- **Preconditions:** Caller must have active `{module}` profile standing; blocks with group creator are filtered.
- **Response DTO (`200 OK`):** Paginated array of public group summaries (concealing internal IDs and private memberships).

#### Op 4: Group Configuration Update
- **Requirement ID:** CF-07.3
- **Owner:** M5
- **HTTP Method & Path:** `PATCH /api/v1/{module}/groups/{groupId}`
- **Authorization:** Caller must hold `Leader` or `Admin` role in `groupId`.
- **Request DTO:**
  ```json
  {
    "name": "Advanced Robotics & AI Club",
    "isDiscoverable": false
  }
  ```
- **Errors:** `403 Forbidden` if caller is regular member; `404 Not Found` if group nonexistent.

#### Op 5: Join Request Creation
- **Requirement ID:** CF-07.4
- **Owner:** M5
- **HTTP Method & Path:** `POST /api/v1/{module}/groups/{groupId}/join-requests`
- **Preconditions:**
  - Caller profile belongs to `{module}` and is active.
  - Caller is not already a member.
  - Group is within capacity (`HobbiesClubAdmissionCapacityPolicy`).
  - No active block between caller and group leaders (CF-12).
- **Response DTO (`202 Accepted`):**
  ```json
  {
    "requestId": "018d9f4e-2222-7000-8000-000000000002",
    "status": "Pending",
    "requestedAtUtc": "2026-11-23T16:05:00Z"
  }
  ```

#### Op 6: Join Request Status Inspection
- **Requirement ID:** CF-07.5
- **Owner:** M5
- **HTTP Method & Path:** `GET /api/v1/{module}/groups/{groupId}/join-requests/{requestId}`
- **Authorization:** Requesting profile owner or group administrator.
- **Response DTO (`200 OK`):** Privacy-safe status projection (`Pending`, `Approved`, `Rejected`, `Expired`).

#### Op 7: Admission Approval / Rejection Decision
- **Requirement ID:** CF-07.6
- **Owner:** M5
- **HTTP Method & Path:** `POST /api/v1/{module}/groups/{groupId}/join-requests/{requestId}/decision`
- **Authorization:** Group leader/admin.
- **Preconditions:**
  - Hard capacity check enforced **atomically within database transaction**.
  - Downgraded clubs with $\ge 50$ members fail admission.
- **Request DTO:**
  ```json
  {
    "decision": "Approve"
  }
  ```
- **Database Transaction:**
  ```sql
  BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;
  -- 1. Lock group record
  SELECT current_member_count, tier FROM groups WHERE group_id = :groupId FOR UPDATE;
  -- 2. Verify capacity
  -- 3. Insert membership
  INSERT INTO group_memberships (group_id, module, profile_id, role, joined_at_utc) ...
  -- 4. Increment count
  UPDATE groups SET current_member_count = current_member_count + 1 WHERE group_id = :groupId;
  COMMIT;
  ```

#### Op 8: Membership Inspection
- **Requirement ID:** CF-07.7
- **Owner:** M5
- **HTTP Method & Path:** `GET /api/v1/{module}/groups/{groupId}/members`
- **Authorization:** Confirmed active member of `groupId`.
- **Response DTO (`200 OK`):** List of scoped member display profiles (concealing cross-module and account-level IDs).

#### Op 9: Leave Group
- **Requirement ID:** CF-07.8
- **Owner:** M5
- **HTTP Method & Path:** `POST /api/v1/{module}/groups/{groupId}/leave`
- **Preconditions:** Sole leader cannot leave without transferring leadership.
- **Database Transaction:** Delete from `group_memberships`, decrement `current_member_count`.

#### Op 10: Group Closure / Soft Deletion
- **Requirement ID:** CF-07.9
- **Owner:** M5
- **HTTP Method & Path:** `DELETE /api/v1/{module}/groups/{groupId}`
- **Authorization:** Group Creator or authorized M6 Moderation Administrator.
- **Lifecycle Action:** Mark `is_deleted = true`, deactivate memberships, emit M4 cleanup event.

---

## 4. Proposed Database Schema (PROPOSED)

All physical table definitions below are marked **PROPOSED** pending final M2/M6 schema review.

```sql
-- PROPOSED: Core Groups Table
CREATE TABLE IF NOT EXISTS groups (
    group_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    module VARCHAR(32) NOT NULL, -- 'learning', 'hobbies', 'social'
    group_kind INT NOT NULL,     -- 1: Classroom, 2: Club, 3: Zone
    name VARCHAR(128) NOT NULL,
    description TEXT NULL,
    creator_profile_id UUID NOT NULL,
    is_discoverable BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    current_member_count INT NOT NULL DEFAULT 1,
    tier VARCHAR(16) NOT NULL DEFAULT 'Free', -- 'Free' or 'Premium'
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_group_module_kind CHECK (
        (module = 'learning' AND group_kind = 1) OR
        (module = 'hobbies' AND group_kind = 2) OR
        (module = 'social' AND group_kind = 3)
    )
);

-- PROPOSED: Group Memberships Table
CREATE TABLE IF NOT EXISTS group_memberships (
    group_id UUID NOT NULL REFERENCES groups(group_id) ON DELETE CASCADE,
    module VARCHAR(32) NOT NULL,
    profile_id UUID NOT NULL,
    role VARCHAR(32) NOT NULL DEFAULT 'Member', -- 'Leader', 'Admin', 'Member'
    joined_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (group_id, profile_id),
    CONSTRAINT chk_membership_module CHECK (module IN ('learning', 'hobbies', 'social'))
);

-- PROPOSED: Group Join Requests Table
CREATE TABLE IF NOT EXISTS group_join_requests (
    request_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id UUID NOT NULL REFERENCES groups(group_id) ON DELETE CASCADE,
    module VARCHAR(32) NOT NULL,
    requesting_profile_id UUID NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'Pending', -- 'Pending', 'Approved', 'Rejected', 'Expired'
    requested_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    reviewed_at_utc TIMESTAMPTZ NULL,
    reviewed_by_profile_id UUID NULL,
    CONSTRAINT uq_pending_join_request UNIQUE (group_id, requesting_profile_id, status)
);

-- PROPOSED: Indexes for Fast Scoped Queries
CREATE INDEX IF NOT EXISTS idx_groups_module_discoverable 
    ON groups(module, is_discoverable) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS idx_group_memberships_profile 
    ON group_memberships(profile_id, module);
```

---

## 5. Required Approval Gates for Production Activation

Before any route in this contract is enabled in `Program.cs` or registered in DI:

1. **M2 (Identity & Entitlements):**
   - Authoritative caller profile verification middleware.
   - Authoritative Premium entitlement status resolver for Club creator quota & capacity.
2. **M3 (Hobbies Lead):**
   - Hobbies Club UX alignment on membership capacity and quota messaging.
3. **M4 (Communications Lead):**
   - Channel creation and membership synchronization events.
4. **M6 (Security & Moderation):**
   - Penetration testing, rate limiting, and administrative group takedown integration.

---

*End of Proposed Groups Integration Contract.*
