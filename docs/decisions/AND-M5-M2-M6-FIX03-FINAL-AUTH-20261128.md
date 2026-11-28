# M2 historical secret-scanning decision

Decision reference `AND-M5-M2-M6-FIX03-FINAL-AUTH-20261128`
Approved by the Team Lead on 28 November 2026 for controlled M2 remediation.

AUTH-03 authorizes investigation, source remediation and reporting under the existing security boundaries. AUTH-04 permits individual historical dispositions for independently verified synthetic fixtures and demonstrable false positives. The approved replacement development computer is cleared for routine development, Git, PostgreSQL and CI activity. The original workstation remains subject to OS225 containment. The suspicious `Runtime.ps1` remains prohibited. No conclusion about the original incident was established by this review.

## Independently verified evidence

The [redacted Member-02 QA run](https://github.com/Thulaanchan/Andril-Backend/actions/runs/46117692324) reported eleven historical and two tracked-source occurrences at `581717dd16b93720f9bbe8882100e90d021e30a1`. The checksum-verified Gitleaks 8.30.1 reproduction matched those results. The review mapped historical source paths from the redacted GitHub path hashes and verified exact commit, rule, line, column and fingerprint using the pinned scanner.

| Source | Introducing commit | Historical occurrences | Security reasoning |
| --- | --- | ---: | --- |
| Frontend API readiness documentation | `ffbce2a62ccc711c5d7aa133a19dc892e2654479` and a separate rewording in `0d704324938e56a88527f137595b7b78d0057e0a` | 4 | The flagged spans are documentation about distinguishing API failure or retry states. The prose contains neither a usable credential nor a credential assignment. |
| Group membership security tests | `3acfb7b230d244eb46bb83360a3b509adcb703a8` | 2 | The marker originates in the diagnostic concealment regression. The test injects an argument exception through its stub and asserts the marker is absent from response details and JSON. |
| AI endpoint security tests | `58218ac21c5435373e3fa3971324ca30ce0782da` | 5 | The marker originates in controlled exception-capture fixtures and reappears in the helper that checks responses and logged payloads for disclosure. All test inputs and paired assertions use a shared synthetic value at the corrected head. |

No credential exposure was identified in these reviewed occurrences. Each individual disposition, including repeated merge occurrences, is recorded in the [exact-finding manifest](../../scripts/ci/member-02-gitleaks-dispositions.json). The current-source documentation and AI test marker were corrected directly. Only the exact reviewed historical occurrences qualify under this decision.

The verification job retains full Git ancestry and tracked-source scans and records every detected occurrence using sanitized metadata. Tracked-source findings, unexpected historical occurrences, mismatched locations, missing or invalid evidence, and scanner failures block the gate. The consolidated report continues to enforce all required jobs. Treat any change to the manifest, verifier or approved classifications as a security review change and record the applicable decision on the PR.

## Project correspondence

The Team Lead supplied the following historical communication references. Their underlying records were unavailable for independent inspection.

- `AND-M2-M5-M6-FIX03-JOINT-FINAL-20261127`
- `AND-M1-M2-CONSOLIDATED-STANDING-AUTH-20261127`
- `AND-M5-M2-CI01-R06-CONSOLIDATED-AUTH-20261127`
- `AND-M6-M2-CI01-R06-STANDING-FINAL-AUTH-20261127`
- `AND-M2-M6-OS225-NEW-ENVIRONMENT-20261127`
