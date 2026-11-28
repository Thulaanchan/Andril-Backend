# CF-07 unified Group limits

Team Lead decision AND-M5-LEAD-CF07-LIMITS-20261128-01 applies to Learning Classrooms, Hobbies Clubs and Social Zones. It supersedes the Hobbies-only commercial limits described in BRD v2.4-R1 §07.1 and the previous Group integration proposals.

| Creator's current entitlement | Active Groups created in the same module | Total active members per Group |
| --- | --- | --- |
| Free | 5 | 50 |
| Premium | Unlimited | 100 |
| Expired or revoked Premium | 5 for new creation | 50 for new admissions |

Each module counts only the creator's own active Groups in that module. Every active membership role counts toward capacity, including the owner. Expiry and revocation retain existing Groups and memberships. At or above the applicable limit, the database denies additional creation or admission. Renewal restores the applicable Premium limits when the canonical M2 authority verifies it.

Current entitlement, ownership and limits must be validated in the transaction that commits the Group write. Unavailable or indeterminate subscription evidence denies authority. Classroom educator eligibility, Social adult eligibility and opt-in, module-specific blocks and existing host capabilities continue to govern Group operations.

P-07 inactivity, archival and restoration remain pending. The deployed API and PostgreSQL acceptance evidence are documented with the corresponding runtime changes.
