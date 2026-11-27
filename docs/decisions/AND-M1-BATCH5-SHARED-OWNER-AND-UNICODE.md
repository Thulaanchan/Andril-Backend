# Batch 5 shared owner integration and verified Unicode PDF

Authority: existing cross-member implementation clearance and Batch 5 execution.
PR #568's exact nineteen-file scope, merged state and both successful CI checks
were verified; Member-1 was fast-forwarded and normally pushed with live parity
at `5d2cd230e8aedcc11d9b2d7545c64c637c7d96c1` before implementation.

## Shared authoritative identity/lifecycle reuse

`ICurrentLearningOwnerBoundary` is implemented against actual existing M2/Common
`ICurrentUser`, `IAccountRepository.FindBySubjectAsync`, `IPrivateModuleAccess`
and `IPrivateProfileService.WithAuthorizedProfileAsync` contracts. It never
provisions accounts, accepts a client subject as identity, uses uncoordinated
profile reads or caches positive authority. Optional expected account is only an
extra constraint; identity still comes from the trusted authenticated subject.
The callback and release stay within Common's current authorized profile gate;
session changes, foreign module/account and revoked lifecycle withhold the result.
Unavailable authority/cancellation propagate to confidential feature handlers.

The delivered AI profile-access adapter now delegates its duplicated owner proof
to this component. Its public constructor and existing denial behavior are
preserved. Private Growth provenance/voluntary mentorship-offer authority can use
the same boundary directly; its previous constructor remains compatible. A
current-owner XP read adapter uses it for balance/history reads but deliberately
supplies no earning, publication or adjustment authority and an unconfigured policy.

`AddLearningOwnerAuthority` is explicit opt-in composition for scoped shared owner,
limited private-activity and XP-read adapters. TryAdd preserves existing supplied
authorities without certifying them. It installs no database connection, repository,
route, provider, cleanup worker, privacy-processing consent or target/block grant.
Startup does not invoke it. Missing real M2 dependencies fail resolution; denied
Common sources remain denied. This is not a new transaction coordinator or a
transferable resource permission. Repositories retain their existing coordinated
database and purpose-specific authorization checks.

## Pinned font evidence and supported PDF text

Source: official `notofonts/noto-fonts` repository, immutable commit recorded in
`src/Andril.Learning/CV/Fonts/provenance.json`. Exact official paths were discovered
from the pinned Git tree. Each unmodified file matched the upstream Git blob and
has a recorded SHA-256. The accompanying SIL Open Font License 1.1 is retained.
Runtime embeds only those resource bytes, verifies SHA-256 and embedding flags,
and never reads caller fonts, system font directories, URLs or remote resources.

Upstream references: [font repository](https://github.com/notofonts/noto-fonts),
[upstream license](https://github.com/notofonts/noto-fonts/blob/main/LICENSE).

- Modern: NotoSans-Regular.
- Professional: NotoSerif-Regular.
- Creative: NotoSansMono-Regular.

Unicode PDFs use embedded TrueType/CID glyph mapping, per-glyph advances and
ToUnicode maps. Width-aware wrapping and content-driven pages preserve supported
precomposed Latin, Greek, Cyrillic and available punctuation/currency glyphs.
ASCII output retains the existing path. The same approved field/consent projection
feeds both paths; legacy/imported data stays preserved and withheld. No private
metadata, annotations, links or temporary production resources are introduced.

Independent parser QA checks extracted Unicode phrases, exact embedded font hashes,
page count, absent hidden contact/legacy text, metadata and annotations. Poppler
renders all three templates for visual verification. These are synthetic evidence
artifacts outside Git, not user documents or production font/provider activity.

Complex shaping, combining marks, bidi controls and unsupported glyphs still fail
closed rather than render incorrect characters. Tamil, Arabic, CJK and arbitrary
Unicode coverage are not claimed complete. Additional script-specific reviewed
fonts and shaping/bidi integration remain necessary for those scripts.

## Genuine remaining integration holds

- M2/M5: authoritative target/pair privacy, blocking, eligibility, offer and Circle
  resource/membership evidence, with source revocation coordinated through each
  repository's commit/read-release boundary. Owner proof cannot replace these.
- AI: actual current processing-basis/privacy-policy sources, authoritative ordinary
  and AI-purpose discovery ingestion, concrete provider send/revocation protocol and
  transport guarantees. No claim that a journal claim/recheck provides fencing.
- M1/M2/M6: verified production repository/connection/schema/migration/role wiring;
  isolated connection guards remain intact. No hosted migration or live deployment.
- Cleanup: authenticated worker authority, legal/retention and external-content-store
  coverage, inactive/deleted-profile maintenance contract. No retention periods or
  privileges inferred from a deadline or ordinary owner status.
- XP: actual approved Product/M1/M3/M6 earning/configuration values, trusted source
  completion/publication/adjustment authorities. Production stays unconfigured.

Earlier Batch 3 and Group A/B/C holds are preserved. Neither a purpose grant nor
full completion is inferred from shared ownership or isolated synthetic acceptance.
