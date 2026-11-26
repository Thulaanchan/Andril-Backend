# M4 D12 LiveKit sandbox readiness

Development only. Production calling, DI, routes and Chat Deny-All are unchanged.
D11 lifecycle, participant binding, authorization, webhook authentication/replay handling,
reconnect, revocation and cleanup reconciliation are reused, not replaced.

The sandbox configuration requires the exact ChatRealtimeDevelopment environment and
an explicit Enabled=true setting. Endpoint validation rejects non-loopback plaintext,
credentials in URLs, paths, queries and fragments. Secrets come only from caller-supplied
configuration. No credentials or tokens are printed. The harness HTTP client disables
redirects/cookies and bounds connection establishment. Adapter deadlines cover both
response headers and body; no automatic retry of uncertain provider mutations occurs.
D11 reconciliation remains responsible for retrying cleanup after provider failure.

## Offline verification

Restore using only the local NuGet cache, then run integration filters for
Communication.Calls and Communication.Chat.Realtime, SecurityTests, ArchitectureTests
and a Release solution build with --no-restore. Offline restore disables NuGet auditing;
this is not a claim of a fresh vulnerability review.
The actual sandbox test is explicitly skipped unless ANDRIL_RUN_LIVEKIT_SANDBOX=true.

## Explicit sandbox execution

Supply these environment variables through a secure local shell or secret manager:
ANDRIL_LiveKitSandbox__ApiEndpoint (HTTPS origin),
ANDRIL_LiveKitSandbox__RtcEndpoint (WSS origin),
ANDRIL_LiveKitSandbox__ApiKey and ANDRIL_LiveKitSandbox__ApiSecret.
Do not put secrets in source, command arguments, transcripts or committed settings.
Run scripts/development/Test-LiveKitSandbox.ps1 -Connect after package restore.
It creates an opaque unique two-participant room, verifies existence, generates distinct
participant credentials, attempts both removals and deletes only its own room, then
verifies absence. Even uncertain creation acknowledgement triggers cleanup attempts.
A provider outage can still require manual cleanup of that test room.

This server API probe does not connect media clients, prove JWT acceptance by an RTC
server, receive actual provider webhook deliveries, or demonstrate two-device media.
Those steps require a reachable sandbox, provider webhook forwarding to a development
host using the existing raw-body verifier/service boundary, and authorized test clients.
There is no public webhook route or production authority activation in this change.
Cloud versus self-hosted revocation/rejoin behavior remains a real-provider validation
item: removal plus a previously issued JWT is not an instantaneous revocation guarantee.
Webhook replay storage and call state remain in-memory and are not multi-node durable.

Provider contracts follow the D11 locally recorded LiveKit references (RoomService Twirp,
JWT grants and raw-body signed webhooks). No provider documentation was fetched during
this offline task. Actual LiveKit sandbox connection: NOT EXECUTED in the offline run.

## Verified offline result (26 November 2026)

Release calling/realtime: 99 passed, 0 failed, 1 actual sandbox test skipped.
SecurityTests: 1489 passed, 0 failed/skipped. ArchitectureTests: 13 passed, 0 failed/skipped.
Release solution build: succeeded, 0 errors, 3 existing warnings (two xUnit2031
in MapContributionCaptureSessionPersistenceTests:423/424 and CS0618 in
GroupMembershipOperationServiceTests:276). Script syntax and git diff checks passed.
