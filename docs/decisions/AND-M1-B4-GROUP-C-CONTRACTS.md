# Group C implementation and confirmed decisions

User confirmation on 27 November 2026 (Asia/Colombo):
`AND-M1-B410-CV-FIELDS-20261127` approves the exact fields below, mapped to the
existing seven category identifiers. No eighth category or implicit legacy
classification is introduced.

- PersonalInformation: ProfessionalDisplayName, ProfessionalTitle; Email, Phone,
  CityCountry, ProfessionalProfileLinks require explicit document contact consent.
- ProfessionalSummary: ProfessionalObjective, CareerSummary, ProfessionalStrengths.
- Education: Institution, Qualification, FieldOfStudy, StartDate, EndDate,
  GradeOrAcademicResult.
- WorkExperience: Organization, JobTitle, StartDate, EndDate, Responsibilities,
  ProfessionalAchievements.
- Skills: SkillName, Proficiency, SkillCategory.
- Projects: ProjectTitle, Description, Technologies, Contribution, Outcomes.
- CertificationsAndAchievements: CertificationOrAchievementTitle,
  IssuingOrganization, AwardDate, ExpiryDate, Description.

No semantic category mismatch exists. Structured fields are explicit reviewed
input, never inferred from generic Entry.Text, Portfolio imports or consent.
Stored legacy/imported text is retained and withheld. Field names/categories are
allowlisted, controls/unsafe link schemes/prohibited sensitive-content markers
are rejected, and embedded contact-like text is withheld from noncontact fields.
These conservative checks do not claim semantic detection of every secret; the
reviewed-field contract forbids private Diary/other-module/financial/identity
material. Professional links render as inert text only, never clickable actions
or fetched resources. HTML escapes every value. Preview/PDF share one projection;
contact data is off by default and re-read under the existing CV owner/consent
repository release boundary. Explicit structured merges occur against current
stored content and preserve unrelated entries/legacy text/import identities.

The existing JSONB section mapping persists fields without a new schema/history.
Unknown stored entry/section fields fail closed rather than silently disappear.
Three templates retain their existing names and use distinct static font styles.
PDF pagination/wrapping is content-driven, keeping at least the original two
structural pages. Standard built-in fonts support printable ASCII. Unsupported
Unicode PDF text is rejected without replacement/truncation; HTML/storage retain
Unicode. Full Unicode PDF requires a reviewed embeddable font/layout asset and
remains an explicit dependency, not a claim of completed universal export.
No temporary files, external resources, user metadata or annotations are generated.

## Learning XP clearance

The user approved infrastructure implementation while requiring **all production
events, amounts, caps, level/achievement thresholds remain unconfigured** pending
Product/M1/M3/M6 approval. No likes, messages, connections, purchases, removed
Quizzes/Missions or inferred Growth verification automatically earn XP. Learning
XP is separate from Flight Points, purchased coins and other modules.

The isolated immutable ledger reuses the real M2 current-owner serializable
boundary, exact Learning scope and FORCE RLS. Credits have unique durable source
identities and current versioned approved source/policy evidence. Unknown policy
or completion authority yields unavailable, not a fabricated zero-value reward.
Private balance/history/progress reads require current authority at read release.
Technical keyset pages default to 50 with at most 100, not a product quota.
Level/achievement outputs are absent when policy is unconfigured. Policy contains
versioned event values, optional current-total cap and lifetime per-source-kind
occurrence caps, levels and achievement thresholds; these are technical support
mechanisms with no production values/default earning rules. Time-window/reset
semantics are not invented.

Audited server-only reversals/corrections append immutable signed rows tied to an
original credit and unique adjustment operation. Exact adjustment authority,
reason and current policy are mandatory; no client-supplied reward route exists.
Reversal removes only the outstanding amount, cannot repeat under a different
operation, and preserves the source/audit history. Corrections cannot make the
original outstanding amount or total negative or exceed configured total caps.
No automatic replay follows conflicts/uncertain commits; reconciliation uses the
durable source/operation identity. Source/policy revocation before commit rolls
back. Runtime has SELECT/INSERT only, no UPDATE/DELETE or RLS bypass.

Versioned approved configurations are stored immutably in an owner-scoped policy
table. A revision-only publication command resolves exact independently approved
content through a trusted publisher contract. Reusing a revision with different
configuration conflicts; awards and thresholds must match the stored version.
Policy content is rechecked at commit/release, not merely a nonempty version label.
No configuration publication or award route is exposed to end users.

Production policy publication approval, event-verification adapters,
maintenance authority and lifecycle/source coordination remain external gates.
Synthetic policy configurations and adjustments appear only in isolated tests.
No XP production activation, schema migration/history or hosted operation is
introduced. Existing B3-10, Group A and Group B holds are preserved. Full feature
completion is not inferred from isolated component acceptance.
