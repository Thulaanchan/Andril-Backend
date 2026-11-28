# Learning API inventory — B6 P01

Audited baseline: `a71dc05d46702a9214558612644451aba2f0a75e` (PR #653 merged and synchronized).
This is the single M1 route inventory. It records implemented runtime composition, not
deployment activation. All Learning runtime switches remain **off by default**.
No hosted migrations, production grants or configuration changes are performed by startup.

**Authentication:** `C` means verified bearer identity, confirmed account middleware,
current server-resolved Learning ownership/eligibility, and repository commit/read-release
authorization. Client document/profile references never confer ownership. Participant
operations additionally require current purpose-specific eligibility/blocking evidence.
`T` means controlled/test-only composition with its supplied authoritative boundary;
it is not a usable production API. Disabled opt-ins have no mapped routes (404).
All private runtime routes enforce no-store, including identity challenges and errors.

**Status:** `R` is implemented opt-in runtime; `N` is added/completed by P01; `H` is held.
Success/error codes are HTTP outcomes, not claims that dependencies are configured.
Authenticated ownership denial is 403 or opaque 404 as shown; failed/unresolved persistence
returns sanitized 503, never an internal exception. No automatic mutation replay.

**Flutter screen column:** requirement-based integration destinations only. This backend
checkout contains no Flutter application or verified Dart screen identifiers. Flutter
owners must bind these destinations to their actual screens; names below are not fabricated
claims about shipped frontend classes. Dates use `yyyy-MM-dd`; existing enum wire values
are integers. Existing `/api/learning` and `/api/v1/learning` paths are preserved.

## Runtime inventory

| Method | Route | Request → response DTO / content | Authentication | Status / HTTP outcomes | Corresponding Flutter integration screen |
|---|---|---|---|---|---|
| POST | `/api/learning/cvs` | `CreateCvRequest { template }` → `CvCreatedResponse { id }` | C owner | R; 200/400/403/503 | CV list / create |
| GET | `/api/learning/cvs` | none → `CvListResponse { ids }` | C owner | R; 200/403/503 | CV list |
| GET | `/api/learning/cvs/{id}` | path id → `CvEditResponse` | C owner | N; 200/400/403/404/503 | CV editor / reopen |
| PUT | `/api/learning/cvs/{id}/template` | `CreateCvRequest { template }` → empty | C owner | N; 200/400/403/404/409/503 | CV template selection |
| PUT | `/api/learning/cvs/{id}/contact-consent` | `ConsentRequest { includeContactInformation }` → empty | C owner | R; 200/400/403/404/409/503 | CV privacy controls |
| POST | `/api/learning/cvs/{id}/sections/{category}/structured` | `ReviewedCvSectionRequest { entries:[{id,fields}] }` → `ReviewedCvSaveResponse { outcome }` | C owner | R; 200/400/403/404/409/503 | CV seven-section editor |
| GET | `/api/learning/cvs/{id}/preview` | path id → inert escaped HTML | C owner + fresh output consent | R; 200/403/404/503 | Private CV preview |
| GET | `/api/learning/cvs/{id}/pdf` | path id → private `application/pdf` bytes | C owner + fresh output consent | R; 200/403/404/503 | CV PDF export |
| POST | `/api/v1/learning/cv-imports/reviews` | `ProjectImportReviewRequest { portfolioId,cvId,projectIds }` → `ProjectImportReviewResponse { reviewId,portfolioId,cvId,projects }` | C source/destination owner | N runtime; 200/400/403/404/409/503 | Portfolio project selection / CV import review |
| POST | `/api/v1/learning/cv-imports/{reviewId}/confirm` | `ProjectImportConfirmRequest { confirmed }` → empty | C source/destination owner | N runtime; 204/400/403/404/409/503 | CV import confirmation |
| POST | `/api/learning/portfolios` | `TemplateRequest { template }` → `{ id }` | C owner | R; 200/400/403/503 | Portfolio list / create |
| GET | `/api/learning/portfolios` | none → `{ ids }` | C owner | R; 200/403/503 | Portfolio list |
| GET | `/api/learning/portfolios/{id}` | path id → `{ id,template,projects:[{id,title,description,mediaReferences,externalLinks}] }` | C owner | R; 200/403/404/503 | Portfolio editor |
| GET | `/api/learning/portfolios/{id}/preview` | path id → inert escaped HTML | C owner | R; 200/403/404/503 | Private Portfolio preview |
| PUT | `/api/learning/portfolios/{id}/template` | `TemplateRequest` → empty | C owner | R; 200/400/403/404/409/503 | Portfolio templates |
| POST | `/api/learning/portfolios/{id}/projects` | `ProjectRequest { title,description,mediaReferences,externalLinks }` → `{ id }` | C owner | R; 200/400/403/404/409/503 | Portfolio project editor |
| PUT | `/api/learning/portfolios/{id}/projects/{projectId}` | `ProjectRequest` → empty | C owner | R; 200/400/403/404/409/503 | Portfolio project editor |
| PUT | `/api/learning/portfolios/{id}/project-order` | `OrderRequest { order }` → empty | C owner | R; 200/400/403/404/409/503 | Portfolio project ordering |
| DELETE | `/api/learning/portfolios/{id}` | path id → empty | C owner | R; 200/403/404/409/503 | Portfolio management |
| GET | `/api/learning/diary` | query `from,through` → `{dates}` calendar dates | C owner | R; 200/400/403/503 | Diary calendar |
| GET | `/api/learning/diary/{date}` | calendar date → private entry DTO | C owner | R; 200/400/403/404/503 | Diary entry |
| POST | `/api/learning/diary/{date}` | `ContentRequest { reflection:{title,body}?,plans:[{id,title,description}] }` → empty | C owner | R; 200/400/403/404/409/503 | Diary create |
| PUT | `/api/learning/diary/{date}` | `ContentRequest` → empty | C owner | R; 200/400/403/404/409/503 | Diary edit |
| DELETE | `/api/learning/diary/{date}` | calendar date → empty | C owner | R; 200/400/403/404/409/503 | Diary explicit delete |
| POST | `/api/learning/activities/{kind}` | `CreateLearningActivityRequest` → `{outcome,activity,activities}` | C owner / resolved participants | R with kind-specific authority; 200/400/403/404/409/503 | Exchange, Pact, Connections, Growth, Mentorship |
| GET | `/api/learning/activities/{kind}` | query `after?,limit?` → private activity list envelope | C owner / participants | R; 200/400/403/503 | Corresponding activity list |
| GET | `/api/learning/activities/{kind}/{id}` | activity id → private activity envelope | C owner / participants | R; 200/400/403/404/503 | Corresponding activity detail |
| POST | `/api/learning/activities/{kind}/{id}/commands` | `ChangeLearningActivityRequest` (action, expected revision and supported action fields) → private activity envelope | C existing role-specific participant | R; 200/400/403/404/409/503 | Activity accept/decline/cancel/disconnect/detail |
| GET | `/api/learning/disclosure` | none → `{outcome,publication}` | C owner | R; 200/403/503 | People Discovery privacy |
| PUT | `/api/learning/disclosure` | `LearningDisclosureChange { visible,displayNameDisclosure,expected? }` → `{outcome,publication}` | C owner | R; 200/400/403/409/503 | People Discovery privacy |
| GET | `/api/learning/mentorship-offers/{offerId}/publication` | offer id → `{outcome,publication}` | C exact private offer owner | R; 200/400/403/404/503 | Mentorship offer publication |
| PUT | `/api/learning/mentorship-offers/{offerId}/publication` | `LearningOfferPublicationChange { published,expected? }` → `{outcome,publication}` | C exact private offer owner | R; 200/400/403/404/409/503 | Mentorship offer publication |
| GET | `/api/learning/people` | query `query?,limit?` → `{outcome,people}`; disclosed profile ID/display name only | C + current separate discovery disclosure | R; 200/400/403/503 | People Discovery |
| GET | `/api/learning/classrooms` | query `query?,limit?` → `LearningClassroomResult` | C + M5 discovery/lifecycle/block rules | R; 200/400/403/503 | Classroom discovery |
| GET | `/api/learning/xp` | query `after?,limit?` → `{outcome,total,entries,progress}` | C owner | R read only; 200/400/403/503 | Learning XP / progress / history |

All runtime rows may also return 401 (missing/invalid bearer) or 403 (unconfirmed identity).
Route constraints/model binding can produce 400/404 before the repository. Unknown state
or ambiguous commit is 503; clients must not infer success or blindly replay writes.

### DTO/privacy and registration details

- `CvEditResponse`: `id`, `template`, `includeContactInformation`, and seven
  `{id,category,entries:[{id,legacyText,fields}]}` sections. It deliberately omits account,
  owner-profile and other-module identity. This private editable content is **not** preview data.
  Legacy/imported text remains preserved and excluded from preview/PDF until explicitly reviewed.
- Sections remain PersonalInformation, ProfessionalSummary, Education, WorkExperience,
  Skills, Projects, CertificationsAndAchievements. CV templates remain Modern,
  Professional, Creative; Portfolio templates remain Developer, Creative, Professional.
- CV template PUT changes only the template on the current persisted document. Structured
  edits merge reviewed fields; they do not silently replace unrelated/legacy/imported content.
- Private preview/PDF reuse the existing approved field allowlist, contact-consent checks,
  Unicode renderer and fresh owner release authorization. No new public URLs or activation.
- Import uses the delivered IMP-01/02 review/confirmation lifecycle and Title/Description-only
  unclassified append. Durable source identities reject duplicates. Source changes invalidate
  reviews; current M2 serializable authorization coordinates production CV/Portfolio writers.
- Import opt-in: `Learning:ProjectImports:Enabled`, `Learning:ProjectImports:RuntimeRole`,
  plus existing `Learning:PrivateCv:Enabled` and `Learning:PrivatePortfolios:Enabled`.
  The role is provisioned explicitly with `LearningProjectImportRuntimePermissions.GrantSql`;
  it cannot mutate Portfolio, consent, ownership, identity, delete records or bypass RLS.
  All four import/CV/Portfolio tables must retain FORCE RLS and be owned outside runtime role.
- Existing switches: `Learning:PrivateCv`, `PrivatePortfolios`, `PrivateDiary`,
  `OwnerActivities`, `PeerActivities`, `Publications`, `XpRead` (`Enabled`/`RuntimeRole`).
  Classrooms additionally require verified Shared Groups persistence and Learning enabled.
- `kind`: SkillExchange=1, Pact=2, GrowthSelfReport=3, MentorshipOffer=4,
  MentorshipRequest=5, Circle=6, Connection=7. Circle stays unavailable without a verified
  resource binding/capability mapping. Requests cannot select arbitrary owners/modules.

## Controlled or unavailable requirements — not activated by this audit

| Method | Route | DTO | Authentication | Status / exact dependency | Flutter integration screen |
|---|---|---|---|---|---|
| GET | `/api/v1/learning/setup` | `LearningSetupResponse` | T | H: controlled host only | Initial Learning preferences |
| PUT | `/api/v1/learning/setup/selections` | `LearningSelectionUpdateRequest`, `Idempotency-Key` → 204 | T | H: controlled host only | Teaching / learning skill selection |
| POST | `/api/v1/learning/setup/goals` | `LearningGoalUpdateRequest`, `Idempotency-Key` → 204 | T | H: controlled host only | Personal goals |
| PUT | `/api/v1/learning/setup/goals/{goalId}` | `LearningGoalUpdateRequest`, `Idempotency-Key` → 204 | T | H: controlled host only | Goal editor |
| DELETE | `/api/v1/learning/setup/goals/{goalId}` | `LearningRevisionRequest`, `Idempotency-Key` → 204 | T | H: controlled host only | Goal removal |
| GET | `/api/learning/home` | `LearningHomeResult` | T | H: unregistered controlled composition | Learning Home |
| — | No activated AI route | Dormant `[NonController] AiChatController`; no public route/verb contract | denied | H: actual AI policy producer/version and concrete transport revocation guarantee | Learning AI Chat |
| — | No cleanup endpoint/worker activation | Internal authenticated maintenance contracts only | denied | H: M2/M6/Product worker grant + retention/deletion eligibility including external stores | No client screen |
| — | No XP award/configuration route | Server-only trusted earning authority/configuration | denied | H: unapproved events, amounts, caps and thresholds remain unconfigured | XP read screen only |

Setup/Home are not wired to the isolated test repository: it explicitly rejects production
connections, and no production `ILearningSkillCatalogueReader` supplies an approved current
catalogue/revision. A reviewed publisher/data source and M2-backed setup repository composition
(including profile revision initialization, catalogue coordination and operation reconciliation)
are required; M1 owns the adapter work once that authoritative catalogue input is established.
No dummy catalogue, fake setup completion or permissive authority is installed by this audit.
Other unresolved dependencies are preserved in `decisions/AND-M1-B5-P15-MENTORSHIP-REQUESTS.md`.

## Swagger and source evidence

Development-only, loopback-protected `/openapi/v1.json` and `/swagger` describe enabled
runtime routes; disabled/held APIs must not appear as production-ready. P01 adds explicit
CV editing/template/structured response and PDF content metadata and reuses the API-owned
OpenAPI registration for acceptance tests. Import retains its versioned DTOs/status mapping.
No duplicate aliases or version migrations are introduced.

Sources: `src/Andril.API/Program.cs`, `Extensions/Learning*RuntimeExtensions.cs`,
`Extensions/ControlledLearningActivitiesEndpointExtensions.cs`,
`Extensions/ControlledStructuredCvEndpoints.cs`, and
`Controllers/Learning/ControlledLearningProjectImportEndpoints.cs`.
Acceptance: `LearningApiAuditTests` and `LearningApiAuditPostgresTests`, alongside the
mandatory Learning/CV/Portfolio/peer regressions and legacy import acceptance.
The architecture contract adds only the exact private-import types and two explicit
CV editor routes authorized by this P01 production-registration request. It continues
to reject default activation, generic Learning persistence registration and anonymous
private endpoints; import enabled/disabled composition is verified separately.
