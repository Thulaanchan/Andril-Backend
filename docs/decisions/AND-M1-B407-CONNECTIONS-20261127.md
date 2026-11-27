# B4-07 Learning Connections — confirmed contract

The user explicitly approved this contract in the Group B execution on
27 November 2026 (Asia/Colombo). No additional general approval is required.

1. An eligible Learning profile sends a request to another eligible Learning profile.
2. Only the recipient accepts or declines; only the sender cancels a pending request.
3. Only explicit recipient acceptance establishes the connection.
4. Either connected participant can end it.
5. States: Pending, Accepted, Declined, Cancelled, Disconnected.
6. Reject self requests, duplicate pending requests and duplicate active connections,
   including reversed pairs.
7. Only the two participants view their record, subject to current privacy,
   blocking and lifecycle authorization.
8. Blocking immediately revokes connection-based access; cross-module access is forbidden.
9. Exchanges, Pacts, mentorship, Classroom membership and Social matches never imply a connection.
10. Connections never bypass existing communication authorization.

Implementation preserves terminal history and does not introduce expiry, cooldown,
retention, deletion, XP, notification, messaging, group membership or entitlement
rules. Stored Accepted status is not an access grant. Every protected operation
requires fresh Learning ownership/eligibility and authoritative current privacy
and blocking evidence. Unknown evidence fails closed. Blocking denies access;
it does not silently rewrite stored lifecycle history or grant another module access.
