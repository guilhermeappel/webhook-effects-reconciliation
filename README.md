# webhook-effects-reconciliation

Companion code for the article **Processed Is Not Applied: Measuring Where Webhook Effects Get Lost**: the C#
(.NET 10, EF Core 10, PostgreSQL) version of the inbox design it describes, with tests that run against a real
database. It is a reduced model for reading and experimenting, not a framework and not production code.

## What the code does

The endpoint stores each webhook as it arrived and answers at once. A background processor then hands each stored
event to a handler and records the outcome as an `InboxStatus`:

| Value | Status | Meaning |
|---|---|---|
| 0 | `Pending` | Stored, not processed yet |
| 1 | `Applied` | The handler changed local state |
| 2 | `NoOp` | Valid event, nothing to change: the state already matched, or a rule ignores it |
| 3 | `SkippedNotFound` | The event refers to a charge we do not have. It is looked at again about 5 and 10 minutes later; if the charge is still missing, it stays this way and a warning is logged |
| 4 | `Failed` | An exception; retried with backoff (2, 4, 8 ... minutes, at most 60) |
| 5 | `DeadLettered` | The 8th failure, a payload with the wrong shape, or a body the endpoint could not read, that has no event type, or whose values the database cannot hold; logged as an error |

A retry never overtakes an earlier unfinished event for the same charge. The charge transitions come from one pure
function, `ChargeRules.Decide`, which the handler and the repair both call.

## What is in it

| Folder | What | Article |
|---|---|---|
| `src/WebhookInbox/Inbox` | Endpoint that stores the raw event and answers; inbox statuses; processor that owns the retries | Endpoint, inbox status and processor listings |
| `src/WebhookInbox/Charges` | The transition rules as a pure function (`ChargeRules.Decide`), the payload, the handler | The rule table |
| `src/WebhookInbox/Reconciliation` | Attribution of a divergence to the layer where the effect was lost | Attribution query |
| `src/WebhookInbox/Repair` | Repair of deleted charges: a dry run unless `apply` is true, one record at a time, optimistic concurrency | Repair section |
| `src/WebhookInbox/Persistence` | EF Core model; `xmin` as the concurrency token | |
| `sql/attribution.sql`, `sql/invariants.sql` | The two queries of the article | |
| `tests/WebhookInbox.Tests` | xUnit and Testcontainers (PostgreSQL) | |
| `docs/` | Design notes: what the example leaves out, and the rule matrix | |

## Running it

Needs the .NET 10 SDK (10.0.401 or a newer 10.0.x, see `global.json`), Docker with Compose v2, and two terminals:
`dotnet run` keeps the first one.

```bash
docker compose up -d --wait
dotnet run --project src/WebhookInbox
```

`dotnet run` uses the `http` profile in `src/WebhookInbox/Properties/launchSettings.json`, which starts the app in
the Development environment on `http://localhost:5000`. The Development settings (a throwaway token and the local
connection string) are in `src/WebhookInbox/appsettings.Development.json`; `dotnet run` reads them, and a published
build does not contain them. Without the Development environment the app runs as in production, finds no connection
string or webhook token, and refuses to start. (On macOS, port 5000 is often taken by AirPlay Receiver: start the app
with `--urls http://localhost:5077` and use that port below.)

In Development the app creates its tables (`Charges` and `InboxEvents`) in the `payments` database of whatever
PostgreSQL answers on the connection string's port, which is not necessarily the container: a local installation
can be listening on 5432 too. If that port is taken, publish the container on another one and point the app at it,
in the shell you start `dotnet run` from:

```bash
# bash
POSTGRES_PORT=55432 docker compose up -d --wait
export ConnectionStrings__Payments='Host=localhost;Port=55432;Database=payments;Username=postgres;Password=postgres'
```

```powershell
# PowerShell
$env:POSTGRES_PORT = '55432'; docker compose up -d --wait
$env:ConnectionStrings__Payments = 'Host=localhost;Port=55432;Database=payments;Username=postgres;Password=postgres'
```

### Send an event

In the second terminal, with bash (Git Bash on Windows works):

```bash
curl -i -X POST http://localhost:5000/webhooks/payments \
  -H 'content-type: application/json' -H 'webhook-token: local-development-token' \
  -d '{"id":"evt_1","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"OVERDUE"}}'
```

With PowerShell 7, use `curl.exe` and a backtick for the line continuation. (Windows PowerShell 5.1 changes the
quotes it passes to `curl.exe`: put the body in a file and use `--data-binary '@body.json'` instead.)

```powershell
curl.exe -i -X POST http://localhost:5000/webhooks/payments `
  -H 'content-type: application/json' -H 'webhook-token: local-development-token' `
  -d '{"id":"evt_1","event":"PAYMENT_UPDATED","payment":{"id":"pay_1","status":"OVERDUE"}}'
```

The answer is `200`. Then look at the row (the time will differ):

```bash
docker compose exec postgres psql -U postgres -d payments -c 'select "Id","EventType","Status","NotFoundAttempts","NextAttemptAt" from "InboxEvents"'
```

```text
 Id |    EventType    | Status | NotFoundAttempts |         NextAttemptAt
----+-----------------+--------+------------------+-------------------------------
  1 | PAYMENT_UPDATED |      3 |                1 | 2026-10-11 06:15:32.681355+00
```

`Status` is stored as the integer value of `InboxStatus` (see the table above). There is no charge `pay_1`, so the
event ends as `SkippedNotFound`. It is looked at again about 5 and 10 minutes later (the processor wakes up to 30
seconds late): `NotFoundAttempts` counts the looks, and `NextAttemptAt` shows the next one until the third. After the
third the processor logs a warning, because a row that stays `SkippedNotFound` is easy to overlook.

Three more things to try with the same command. The same body again answers `200` and stores nothing, and logs
nothing. A wrong token answers `401`
(`-H 'webhook-token: nope'`). A body over `Gateway:MaxBodyBytes` (1 MiB; set it with `Gateway__MaxBodyBytes`, up to
10 MiB) answers `413`, is not stored, and is logged as a warning that says its declared length and the limit.

## Tests

```bash
dotnet build -warnaserror
dotnet test          # starts PostgreSQL with Testcontainers: Docker must be running; about a minute
```

Tests are named for the behavior they check. They cover the transition rules, the 18 audited cases, the endpoint
(duplicate delivery, unparseable bodies, events without an id, the body limit, the token), the processor (order per
entity, `MaxAttempts`, the short retry of "not found", a database outage in the loop), the repair (dry run, provider
confirmation, unfinished inbox events, `xmin` conflicts), the attribution, and the two SQL queries. How the rules
are checked against the production implementation is in [docs/rule-matrix.md](docs/rule-matrix.md).

The audited cases (`AuditedCases.cs`) are synthetic data shaped by an audit: the statuses, the grouping of charges by
customer and the age in days of each deleted charge are as found there, and the labels, keys and everything else are
invented. No identifiers, amounts, calendar dates or customer data come from a real system.

## What the example leaves out

The short list; the rest is in [docs/design-notes.md](docs/design-notes.md).

- Only charges are modeled. Subscriptions are not: in production a deletion also closes the subscription and the
  assignment tied to the charge, here the subscription handler is a stub that reports `NoOp` and the plan service
  only records that a re-evaluation was requested.
- The handler asks for the plan re-evaluation before the processor commits the transaction; in production,
  re-evaluate after the commit or through an outbox. The repair changes the charge and does not ask for the plan to be
  re-evaluated, which a real repair has to do.
- One tenant (one token) and one processor instance, with sequential delivery assumed. Several instances would need
  row locking or a lease on the row, not only a filter on the status.
- The provider client is an interface; no HTTP client is included, and the repair is a class driven by the tests.
- The two queries in `sql/` are written for the platform that was measured (an inbox with the statuses `PENDING`,
  `PROCESSED`, `FAILED` and `IGNORED`, and its own tables), not for the tables this example creates. The tests create
  scratch tables with that shape.
- Authentication is a shared token in a header, compared by SHA-256 digests in constant time. Replace it before
  reusing the code; what the endpoint does and does not protect against is in the design notes.

## How this was built

The C# port and its tests were written largely with Claude Code (Anthropic) under the rules in CLAUDE.md and
CONTRIBUTING.md; the author directed the design and reviewed and ran every change. It is a reduced model of the
measured system, which is a separate Python application.

Event and status names follow the payment gateway the platform uses; this project has no affiliation with it.

## Contributing, security and license

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). The code is under the [MIT license](LICENSE),
copyright Guilherme Appel.
