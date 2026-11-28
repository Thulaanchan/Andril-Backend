# D13-R01 development media boundary

D13 originated from D12 candidate 2553eb9 and was restored on Member-4 at 8a28e34.
No production source or authority registration
is changed. Added four missing/malformed Authorization tests to the existing LiveKit
raw-body verifier tests. Existing signed-valid/invalid/body-mutation cases and D11
replay/conflicting-event-id/foreign-room/foreign-participant/out-of-order/revocation
cases are retained. These are offline tests, not real provider webhook deliveries.

D11 uses explicit ChatRealtimeDevelopment fixture authority and an unmapped SignalR
hub. There is no approved device-facing subject/session handoff supplied for this
smoke demo. No production authorization adapter or bypass was invented. The external
Python service remains DEV ONLY and cannot prove ANDRIL module/profile authority.

External demo source is preserved outside this repository with a pre-edit hash
checkpoint. The service now requires loopback peer access, distinct per-slot pairing
codes, a fixed server-selected room, A/B synthetic identities and two-minute grants.
Flutter reuses the existing layout with obscured pairing input, SDK event listeners,
a bounded sanitized timeline, explicit failure/disconnect states and background
camera pause. No credentials or token response bodies are ordinary evidence.

Historical source-machine offline validation: calling/provider suite 71 passed; SecurityTests 1489 passed;
ArchitectureTests 13 passed; all failed/skipped counts zero. Release solution build
succeeded with the same three existing warnings (two xUnit2031 and one CS0618).
Credentialed sandbox test excluded. No attached Android devices were detected.
Real two-client RTC/audio/video/reconnect/cached-token rejoin/webhook delivery remain
NOT EXECUTED. Local Flutter/Python QA and runbook are in the external evidence package.

D12 PR #628 is confirmed merged. It was not modified or merged by this task.
Laptop backend validation: calling/realtime 103 passed; SecurityTests 1696 passed;
ArchitectureTests 13 passed, with zero failures/skips. The credentialed sandbox
test was excluded. Release solution build passed with the same three warnings.

Historical laptop Flutter/Python restoration validation: Flutter 3.50.0 / Dart
3.14.0; two Flutter widget tests and eleven Python tests passed. Flutter analysis
reported zero errors/warnings and four informational deprecations (nonfatal-info
mode). Python reported one dependency deprecation warning. Dependency restoration
completed, with four Flutter SDK-required lockfile adjustments and a fresh Python
3.12.14 virtual environment. Android SDK was unavailable; no live media was tested.

D13-R03 publication is limited to this document and the webhook-header tests on
Member-4, targeting Develop. M5 handles merging. Production calling remains disabled,
production Chat Deny-All remains unchanged, and D11 authorized device-session handoff
is still pending. Real two-client media and real webhook delivery are NOT EXECUTED.
