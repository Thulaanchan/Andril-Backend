# Batch 5 P03: bounded private CV shaping

This implements the independently supported complex-script CV work under the
approved seven-section field and existing private preview/PDF contracts. It does
not grant a purpose permission, activate a repository or deploy a provider.

## Verified native-memory defect and correction

The retained P02 multipage process failed with `0xC0000005` in `hb_shape_full`.
The x64 native binary matched the pinned NuGet asset. HarfBuzzSharp 14.2.1.301's
package identifies source commit `4783f51448f9b070dda4f87b83e941c9599e466e`.
At that exact commit, `Blob.FromStream` passes a pointer to a temporary managed
array using `MemoryMode.ReadOnly`, then exits its `fixed` block. The native face
outlives that pointer's pin; keeping the Blob/Face/Font wrappers alive does not
keep that temporary array pinned. This is an actual lifetime defect, not evidence
of a malformed approved CV or permission to retry an unknown native crash.

The local correction uses `MemoryMode.Duplicate` while the verified font array
is pinned. HarfBuzz copies the bytes immediately; the pin is released in `finally`.
Font, Face and Blob remain owned by the render operation and are disposed in
dependency order, including partial-construction failures. No unsafe compilation,
global pin, native exception swallowing or font path/URL is introduced.

- [Exact Blob source](https://github.com/mono/SkiaSharp/blob/4783f51448f9b070dda4f87b83e941c9599e466e/binding/HarfBuzzSharp/Blob.cs#L90-L99)
- [HarfBuzz blob ownership](https://harfbuzz.github.io/harfbuzz-hb-blob.html#hb-memory-mode-t)
- [Pinned binding](https://www.nuget.org/packages/HarfBuzzSharp/14.2.1.301)

## Rendering contract

- Existing approved projection, contact consent, current owner release checks and
  preservation of unclassified legacy/imported text remain in force. Only the
  already allowed structured projection reaches shaping, ActualText or CMaps.
- Sinhala and Tamil use unmodified Noto Sans script fonts from the same immutable
  official upstream commit as the earlier embedded fonts. Exact blob/SHA-256
  provenance and the OFL license are included. Base template typography remains
  Modern Sans, Professional Serif and Creative Mono; script runs use the explicit
  script face without changing saved content or template identity.
- The MIT license and third-party notices from the pinned HarfBuzzSharp packages
  are copied to build/publish output alongside the font license. Only trailing
  whitespace and line endings are normalized; copyright and license text remain
  unchanged, verified against the pinned packages.
- HarfBuzz shapes LTR runs and uses glyph advances/offsets. Complete text-element
  boundaries control wrapping; pagination leaves space for Indic marks. ActualText
  and ToUnicode preserve logical Unicode through ligatures/reordered glyphs.
- Combining Latin is supported by the same shaping path. Unsupported scripts,
  bidi/control characters and missing glyphs fail closed. This is not universal
  Unicode, Arabic/bidi, CJK or a claim of full language/shaping coverage.
- Rendering remains in memory, cancellation-aware and private, with no hyperlinks,
  metadata, external fetches, public URLs or temporary font/PDF files in production.
  Test artifacts and full evidence are retained outside the repository.

## Verification and unchanged dependencies

Compacting-GC regression exercises repeated multipage render/disposal across all
templates. The synthetic process that previously crashed is rerun only after the
verified correction. Independent strict PDF parsing, source text extraction,
embedded-font hashes and Poppler page rendering supplement focused tests.
PostgreSQL/HTTP acceptance verifies persisted structured/legacy identities,
template round-trip, consent on/off and current owner revocation.

The production holds recorded in `AND-M1-BATCH5-SHARED-OWNER-AND-UNICODE.md` remain.
No new authoritative pair/target disclosure, privacy/block/lifecycle writer,
provider transport guarantee, worker retention eligibility, XP policy or hosted
schema authority has arrived since that delivery. Existing isolated guards and
denied/unconfigured production paths remain unchanged. This PR does not claim
completion of B3-10, multi-party production integration or Batch 5.
