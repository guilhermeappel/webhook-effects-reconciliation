# Design notes

What the example does on purpose, and what it leaves out. The README has the short list; this has the rest, so
that nothing in the code looks like an oversight.

## The endpoint

- A body that cannot be read, or that has no `event`, answers 200 and is stored as dead-lettered, by design. The
  endpoint logs it as an error because the processor never sees it. An event with an `event` but no `id` is kept
  and keyed by a hash of its body, so a redelivery is still recognized as a duplicate.
- **One body is refused after authentication.** A body over `Gateway:MaxBodyBytes` (1 MiB by default) gets a 413, is not stored, and is logged as a warning (the declared
  length and the limit, nothing of the body), because nothing else records that an event was turned away. This is the
  one exception to "never reject a body": the endpoint holds a body in memory several times over while it
  parses, hashes and stores it, and a webhook is small. The bytes are counted as they are read; the length the sender
  declares is only a shortcut, because a chunked body declares none. A provider that retries a 413 will stop at some
  point, so set the limit above the largest event it sends. The limit can be set up to 10 MiB; above that Kestrel's
  own limit (30,000,000 bytes) would be the one that applies, and a body that size takes hundreds of megabytes here.
- If the database cannot hold the values (a NUL character in an id, an id too large to index), the endpoint stores a
  dead letter that keeps the body and answers 200. Any other database error, such as a timeout or a lost
  connection, is a 500 so that the provider resends: it says nothing about the body.
- The stored body is the body decoded as UTF-8: invalid bytes become U+FFFD, and a NUL character is written as
  `\u0000`, because PostgreSQL text cannot hold it. Two bodies that differ only in invalid bytes are therefore stored
  as the same text (and, for an event without an id, they are the same event). If you copy this endpoint and verify a signature, verify it over the bytes you received, before
  decoding.
- A duplicate delivery answers 200. EF Core would log the unique-constraint violation twice, as errors with a stack
  trace (about 9 KB each time), for something the endpoint treats as normal, and a caller holding the token could use
  that to fill the log. `appsettings.json` therefore turns off the `Microsoft.EntityFrameworkCore.Update` and
  `Microsoft.EntityFrameworkCore.Database.Command` categories. What matters is logged by the application: the
  warnings and errors of the endpoint and the processor, and the unhandled exception of a 500.
- Exception text is not sanitized, and a PostgreSQL error can quote request values in its detail. Keep `Include Error
  Detail` off in the connection string (it is by default), and use a structured console formatter such as
  `AddJsonConsole()` if the logs are read by people or tools that trust their line breaks.
- Authentication is a shared token in a header (`Gateway:TokenHeader`, `webhook-token` by default; set it to the
  header your provider uses). The digests of the received and the expected token are compared in constant time. Use
  a long random token (32 bytes or more; it is not enforced, apart from refusing whitespace at either end, which a
  secret read from a file often has and which would make every request a 401); it is compared as UTF-8, so keep it to
  ASCII or base64. With
  `Authorization` as the header, the token includes the `Bearer ` prefix. A failed attempt answers 401 and is not
  logged, and nothing limits how often a caller can try: put rate limiting and TLS in front of the endpoint.
- Single tenant: one token, and no account in the event or in the deduplication key. In production there is one
  provider account per training center.
- The ids, event types and keys in the logs come from the request. They are logged through `LogText.Safe`, which
  replaces control characters, format characters (such as the right-to-left override and the zero-width ones),
  line and paragraph separators and unassigned code points, and cuts them to 200 characters (plus `...`), so a
  crafted id cannot forge a line of the log.
  What is stored is unchanged.

## The processor

- Ordering is per provider object (a charge or a subscription), not per customer or per plan. Ordering by `Id`
  holds only because delivery is sequential.
- Under sequential delivery, a 401 for a wrong token stops the queue while the token is being replaced, so the
  replacement has to be coordinated.
- One processor instance. See the README for what several would need.
- Every failed attempt is logged as a warning, and the last one, which dead-letters the event, as an error. The reason
  stored in the row and logged is the innermost exception's message (a failed save says only that an error occurred
  while saving; the database's own message is the useful one), cut to 1,000 characters (plus `...`) in the row.
- An event type that no handler takes (neither `PAYMENT_` nor `SUBSCRIPTION_`) is recorded as `NoOp` with a warning
  in the log. `NoOp` means "nothing to change", which for such a type is not the same as having looked at it; a
  separate status would change the enum, so the log carries the difference.
- A payment event that reports a settled status for a charge the rules keep closed (a cancelled charge, a refunded
  one) is also `NoOp`, with a warning that gives the rule's reason. That can be right, for instance after a plan switch cancelled the charge,
  but the money arrived and nothing was applied, so it is not left to the status alone.
- Six charge statuses are mapped. Any other status (chargeback, refund in progress, and so on) counts as "not
  modeled" and the event is a `NoOp`; a deletion flag still cancels an open charge.
- The inbox table has no retention: nothing removes old rows.

## The repair

- It is a class driven by the tests, not a command-line tool. A dry run is the behavior unless `apply` is true, and
  the provider is asked in both modes.
- It skips a charge that has unfinished inbox events, so it does not run under the processor, and it checks the
  `xmin` of each charge it writes. An event that arrives between that check and the write is not covered.
- It ignores `PlanEffect.Reevaluate`: it changes the charge and does not ask for the plan to be re-evaluated. A
  deletion that cancels a charge asks for no re-evaluation, so nothing is lost there, and cancelling a deleted charge
  does not give anyone access back by itself. When the flag is stale on a charge the provider shows as paid, a real
  repair has to re-evaluate the plan, or the plan keeps the old access. The audited-case tests judge access with a
  small helper of their own (`AuditedCases.IsBlocked`), not with code of the application.
- A provider error for one id aborts the rest of the batch; the records written before it stay written.

## Everything else

- The schema comes from `EnsureCreated`, off unless configured, instead of migrations.
- The development token and database password are for a laptop and are not part of a published build.
