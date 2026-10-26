# Security policy

This is companion code for an article, maintained by one person on a best-effort basis. There are no
service-level commitments, but real reports are welcome and taken seriously.

## Supported versions

Only the current `main` branch. There are no releases or long-lived branches.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private reporting: on the repository page, open the **Security** tab and choose **Report a
vulnerability**. Include what you found, how to reproduce it, and the impact you expect.

## What is in and out of scope

Local-development defaults, therefore **not** vulnerabilities:

- The throwaway credentials and the published port in `docker-compose.yml` (loopback only) (PostgreSQL `postgres`/`postgres`).
- The webhook token in `src/WebhookInbox/appsettings.Development.json`, which only exists to run the example
  locally. The base `appsettings.json` has an empty token, and the application refuses to start with it.

Reports are in scope when they concern the code as it would be reused, for example:

- a flaw in how events are applied that lets a crafted event corrupt or regress state;
- injection or unsafe handling of input in the webhook endpoint or the SQL files;
- a dependency with a known vulnerability that this project actually exposes.

Limits of the example that are declared in [docs/design-notes.md](docs/design-notes.md), therefore not reports
either: a failed authentication is answered 401 but not logged, and nothing limits how often a caller can try; the
token travels as sent, so TLS is the job of whatever sits in front of the endpoint; the inbox table has no retention.

If you reuse this code in a real system, put your own authentication, rate limiting and TLS in front of the endpoint,
replace the credentials, and run your own review: this project's goal is to show a pattern, not to be
production-hardened.
