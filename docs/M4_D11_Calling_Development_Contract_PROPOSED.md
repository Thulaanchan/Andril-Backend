# M4 D11 — Audio/video calling development contract (candidate)

Status: development-only foundation, 26 November 2026. Production RTC selection remains pending under BRD P-08. No production calling/RTC token issuance, webhook route, production DI registration, Flutter changes or Notification changes are included.

## Backend and Flutter boundary

D11 extends the **same** unmapped D10 SignalR hub, at the integration fixture's loopback-only `http://127.0.0.1:<ephemeral>/__development/chat`. The server environment must explicitly be `ChatRealtimeDevelopment`. ASP.NET Core authentication, the existing verified subject parser and finite authentication expiry apply. The test host alone supplies allowlisted synthetic bearer credentials and authorities. There is no approved public call URL. Clients never provide ownership, isAdult, isMatched or authorization flags.

| Hub method | Arguments | Meaning |
|---|---|---|
| SubscribeCalls / UnsubscribeCalls | module, conversationId | Independently authorized signaling subscription |
| InitiateCall | module, conversationId, requestId, type | Create/replay an exact one-to-one call |
| AcceptCall / RejectCall | module, callId | Callee decision |
| CancelCall | module, callId | Caller cancels ringing |
| GetCall | module, callId | Current authorized authoritative snapshot |
| GetCallCredential | module, callId | Fresh authorized participant-specific development media connection data |
| DisconnectCall / ReconnectCall | module, callId | Record disconnect / prepare an authorized reconnect |
| EndCall | module, callId | Participant ends the call; provider cleanup is coordinated |

Module values: Learning 1, Hobbies 2, Social 3. Type: Audio 1, Video 2. Outcome: Unavailable 0, Success 1, InvalidRequest 2, Conflict 3, ProviderUnavailable 4. Results carry outcome and an optional view. A provider failure can include the authorized persisted Failed snapshot; it never exposes provider exception details.

A view contains callId, module, conversationId, type, status, version, createdAtUtc, updatedAtUtc, cleanupPending and signalingPending. It contains no account/profile/subject identity or provider credential. CallStatus values follow: Initiated 0, Ringing 1, Accepted 2, Connecting 3, Active 4, Ended 5, Rejected 6, Cancelled 7, Missed 8, Failed 9, Disconnected 10.

The state machine enforces Initiated → Ringing → Accepted → Connecting → Active → Ended, with explicit rejection/cancellation/missed/failure/disconnect transitions. Terminal states cannot reopen. Acceptance is callee-only, cancellation caller-only, and end/read/credentials require an exact current participant. Concurrent transitions and scoped initiation retries are serialized in the in-memory repository. Duplicate acceptance reuses the provider association. State version, not timestamps or arrival order, determines reconciliation order.

Ring timeout has **no default approved value**. Missed is applied only when an explicit finite development ring policy is supplied. No call duration policy is invented. The tests' timing values are fixture configuration, not product rules.

## Signaling and recovery

Events: IncomingCall, CallRinging, CallAccepted, CallConnecting, CallActive, CallRejected, CallCancelled, CallEnded, CallFailed, CallDisconnected and CallMissed. IncomingCall is routed only to the callee; other state events go only to currently authorized subscribed participants. Payload fields are version (protocol v1), eventId, callId, module, conversationId, status, stateVersion, kind and createdAtUtc. No room, token, account/profile details or private failure reason is broadcast.

Logical event identity is stable per call/state-version/kind. D10's individual connection transport is reused without treating SignalR groups as authorization. Each delivery checks current call authority, current exact profile scope, conversation access, connection expiry and current stored version. Stale/replayed signals cannot reopen a call. CallAccepted and CallConnecting describe the same current Connecting snapshot after successful provider preparation; the intermediate Accepted transition remains in the repository's state history.

Delivery is not durable. A publisher failure leaves persisted state authoritative and sets signalingPending. Reconciliation can retry the current snapshot. Lost acknowledgements can cause repeat delivery; Flutter must deduplicate eventId and ignore older stateVersion, then GetCall after reconnect. SignalR reconnection reauthenticates and resubscribes; do not assume connection subscriptions survive. An offline incoming-call notification/listing workflow is a separate agreed Notification handoff, not implemented by modifying Notifications here.

Signaling loss is distinct from media loss. Flutter must query GetCall after signaling reconnect. Media disconnect observations enter Disconnected, and ReconnectCall plus fresh authorized credentials prepare Connecting. Active requires verified provider media observations for the two exact participant identities with current application authority; a client assertion or provider webhook alone never grants application access. No client method simply sets Active.

## Media connection data and device behavior

GetCallCredential is allowed only for Connecting/Active sessions. It returns endpoint, opaque room, opaque participant identity, token and finite expiresAtUtc exclusively to the exact authorized caller/callee. The backend signs tokens; Flutter never receives API signing secrets. Request lifetime is explicit and constrained by current participant authority expiry. Ended/rejected/cancelled/missed/failed/disconnected calls cannot issue credentials.

Flutter uses the returned LiveKit connection data with its supported client. Microphone/camera permissions, mute, local camera enable/disable and audio routing are device-level responsibilities, not backend grants. On Android permission failure, do not claim media connected: reject/cancel an unanswered call or EndCall an accepted one as appropriate, dispose media resources, and reconcile GetCall. Video permission refusal does not silently change an authorized Video session into another type; approved downgrade UX remains separate.

REAL LIVEKIT CONNECTION NOT EXECUTED in the automated demonstration. Fake-provider credentials are explicitly synthetic and not usable LiveKit tokens. Actual library-signed JWTs and HTTP/webhook contracts are tested against an in-memory HTTP handler. No Flutter two-device or audio/video media demonstration is claimed.

## LiveKit candidate and webhook security

The unregistered adapter uses LiveKit's documented Twirp RoomService methods CreateRoom, ListRooms, ListParticipants, RemoveParticipant and DeleteRoom. Room names and participant identities are opaque per-call server values, with two participant slots. Server-management JWTs remain server-only; participant grants bind one room and identity, exclude room-admin/data-publish permissions and allow microphone only for Audio or microphone/camera for Video. HTTPS/WSS is required except explicit loopback development HTTP/WS. API credentials are injected server-side, never implicitly logged or loaded into Flutter.

JWT signing and validation use the existing Microsoft System.IdentityModel.Tokens.Jwt 8.19.2 dependency; no cryptographic signing implementation is hand-written. No new NuGet dependency was added. The community Livekit.Server.Sdk.Dotnet 1.2.7 was evaluated via its NuGet metadata and pabloFuente repository (Apache-2.0, active release metadata; .NET community SDK, not an official LiveKit server SDK). It would introduce protobuf/Newtonsoft and newer token-library dependencies. It was not selected or declared vulnerability-free; a full transitive advisory/provenance assessment would be required before adding it. The existing trusted dependency graph is preserved.

Webhook verification consumes the **unaltered raw body** and Authorization JWT, checks HS256 signature, configured issuer, finite lifetime and signed SHA-256 body binding using supported Microsoft/standard library facilities. Only then are room_started, participant_joined, participant_left and room_finished decoded. The exact registered room/participant association is checked. Provider event IDs and body fingerprints deduplicate replays; conflicting reused IDs are rejected. Per-participant provider timestamps prevent an older join undoing a later leave. Terminal call state cannot be reopened by any webhook. There is no exposed webhook route and no skip-auth switch.

Provider failures after room creation may leave uncertain external side effects. Failed/Ended call state prevents new credentials; cleanupPending records incomplete removal/closure and ReconcileAsync retries. Explicit reconciliation also checks current application permission and provider room existence. Expiry alone does not disconnect an existing media participant. Provider removal/room closure capabilities and self-hosted versus Cloud token revocation semantics require real provider-boundary testing before production. No instantaneous revocation/durable outbox/multi-node consistency guarantee is claimed.

## Notification and production integration handoff

Notification delivery must use a separately agreed M4/M5 contract carrying only scoped call ID/state/version and a safe hint, with recipient authorization checked at delivery. No Notification implementation was changed. Current incoming-call signals require an online authorized subscription; offline push/missed-call delivery is not guaranteed.

Production remains non-granting until M2 ownership/standing/session authority, M5 applicable connection and Social 18+/opt-in/match/block/unmatch authority, call-specific grants and revocation coordination are ratified. Development authorities are confined to test fixtures. Persistence, multi-node routing, durable state/signaling cleanup and actual sandbox media/Flutter validation are subsequent integration work.

References inspected on 26 November 2026:
- https://docs.livekit.io/frontends/reference/tokens-grants/
- https://docs.livekit.io/intro/basics/rooms-participants-tracks/webhooks-events/
- https://docs.livekit.io/intro/basics/rooms-participants-tracks/participants/
- https://docs.livekit.io/reference/other/roomservice-api/
- https://www.nuget.org/packages/Livekit.Server.Sdk.Dotnet/1.2.7
- https://github.com/pabloFuente/livekit-server-sdk-dotnet
