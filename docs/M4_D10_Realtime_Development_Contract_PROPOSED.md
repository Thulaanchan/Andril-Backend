# M4 D10 — Development realtime messaging contract (candidate v1)

Status: development-only candidate, 26 November 2026. SignalR/WebSockets is a proposed transport, not an approved production API. M5 owns Develop integration; M2/M5 production subscription/revocation authority is outstanding.

## Availability and authentication

There is **no mapped production endpoint and no production service registration**. The candidate hub also aborts connections unless its server environment is explicitly ChatRealtimeDevelopment. The executable integration fixture alone maps `http://127.0.0.1:<ephemeral-port>/__development/chat`. Its Kestrel listener is loopback-only and lives for the test. Run:

```text
dotnet test tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ChatRealtime
```

The hub uses ASP.NET Core `[Authorize]` and the existing verified `sub`, authenticated-role and non-anonymous checks. It requires one finite `exp` and registers the server-derived subject, never a supplied account/profile. The test host uses private allowlisted synthetic bearer credentials held only in that test process. No synthetic authentication/grants are installed in Program. This is not an endpoint Flutter can use against production. A future approved adapter must use the existing validated JWT mechanism; URL/query token handling is not introduced here. The test transport passes Authorization headers during negotiation and WebSocket upgrade. SignalR mapping closes connections on authentication expiration; the routing service independently checks connection and current session expiry before protected operations/delivery.

## Operations and state

SignalR JSON protocol v1, with numeric enum values matching the existing types:

| Client invocation | Arguments | Result |
|---|---|---|
| `SubscribeConversation` | module, conversationId | status and optional checkpoint |
| `UnsubscribeConversation` | module, conversationId | status |
| `ReadHistory` | module, conversationId, pageSize, cursor/null | status and D9 page |
| `Acknowledge` | module, conversationId, messageId | status and lastReconciledMessageId |
| `RefreshSubscriptions` | none | no payload; revalidates current subscriptions |

`ChatConversationStatus`: 0 Unavailable, 1 Success, 2 Denied, 3 InvalidRequest, 4 Conflict. Subscription failures expose Unavailable without private membership reasons. History may return InvalidRequest for malformed/page-scope cursors. Acknowledge records client reconciliation of a readable canonical message; it does not certify durable client receipt, all earlier messages, or a server-wide ordering watermark. Connection state is the SignalR lifecycle plus a connection-local checkpoint. Checkpoints and subscriptions do not survive reconnect/restart. Clients retain their own reconciled IDs; no unsafe checkpoint-to-history filtering is invented.

Subscription requires explicit current realtime authority **in addition to** D9 ReadMessageHistory authority and exact conversation membership. List/details/send grants do not imply subscription. Current scope is resolved on the server for the requested module and rechecked before protected delivery. Learning, Hobbies and Social scopes remain independent. No shared groups are used; recipients are individually checked and routed to a server-owned connection ID. Multiple permitted connections are supported. Local routing changes and delivery attempts are serialized.

Message mutation stays in D9. `ChatRealtimeMessageSender` first invokes the existing D9 service and publishes only a successful canonical result. It has no separate storage path. Publication outcome is NotAttempted, Attempted or Uncertain, independent of the storage result. Attempted does not mean client acknowledgement/durable delivery. Failed storage emits no event. Publisher failure/cancellation after storage does not report rollback. A retry uses the same D9 request identity.

## Server events

`ConversationMessageCreated` has the following candidate payload (no text, sender/profile IDs, token or claims):

```json
{
  "version": 1,
  "eventId": "<canonical-message-uuid>",
  "module": 3,
  "conversationId": "<conversation-uuid>",
  "messageId": "<canonical-message-uuid>",
  "createdAtUtc": "<canonical-server-message-time>"
}
```

EventId is stable for the logical message and is reused on retry. CreatedAtUtc reuses the D9 server-assigned creation time; it is not a new dispatch timestamp. Module values are Learning = 1, Hobbies = 2, Social = 3; this example uses Social. Consumers key deduplication by version/module/conversation/message ID, not timestamp. The development router suppresses acknowledged send attempts per connection, but a lost network acknowledgement may produce repeats. Reconnect resets that volatile routing state.

`ConversationAccessRevoked` contains only version, module and conversationId. It removes local routing even if the control event cannot be delivered. A future event is prevented by fresh server checks, not by trusting the client to process this event. Revocation reasons and profile metadata are withheld. Current owner evidence failures also fail closed. `RefreshSubscriptions` is an explicit refresh hook, not an implemented production owner-event feed.

`ReadHistory` returns items with messageId, module, conversationId, text, createdAtUtc and nullable moodTone, plus nextCursor. It omits account/private-profile metadata. Content follows D9 validation: non-null Learning/Hobbies MoodTone is rejected; non-null Social vocabulary remains unavailable pending approval.

## Flutter reconnect and reconciliation

1. Configure the supported SignalR client to reconnect after transport loss with bounded retry/backoff. Refresh authentication through the approved identity flow; never retry authorization failures as if they were connectivity failures.
2. On each new connection, resubscribe and reauthorize. No group/subscription/checkpoint survival is assumed.
3. Treat events as invalidation hints. Fetch a fresh D9 history snapshot via ReadHistory, then follow its opaque cursor with the same module, conversation and page size. Do not resume an old snapshot to discover messages appended after that snapshot's ceiling.
4. D9 returns newest-first committed sequence order, independent of timestamp ties/reversal. Assemble pages in that order, deduplicate against stored message IDs and overlapping live events, and reverse the assembled snapshot if the UI displays oldest-first. Reconcile again if another event arrived during paging. Never order concurrent messages solely by timestamps or UUIDs.
5. Acknowledge only after successful local reconciliation. AccessRevoked, Unavailable or authentication expiry must clear protected UI/subscription state. A subscription failure cannot be fixed by switching client-supplied profile IDs.

The executable test client automatically observes a dropped real WebSocket, reconnects with authentication, resubscribes and pages D9 history; the controlled offline interval verifies missed-message recovery. It is a protocol fixture, not a delivered Flutter client. Flutter source is unchanged.

## Remaining production decisions

This candidate has no durable outbox or delivery guarantee. Message history, dedup sets, checkpoints, cursors and connection routing are process-local development state; cursor lifecycle, limits, cleanup/backpressure, persistent ordering and restart recovery need approved persistence/transport contracts. A slow/failing connection can delay this serialized development dispatcher.

Local checks cannot retract in-flight bytes or make external owner revocation atomic with a network write. Production delivery stays disabled until authoritative account/session/profile standing, Social 18+/opt-in/match/block/unmatch evidence, realtime subscription authority and revocation coordination are ratified. No cached membership or connection alone grants delivery. Durable publication and multi-node routing are future integration work, not claimed here.
