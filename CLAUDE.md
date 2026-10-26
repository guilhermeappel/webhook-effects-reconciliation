# webhook-effects-reconciliation

Companion code for an article on measuring where webhook effects get lost (.NET 10, EF Core 10, PostgreSQL).
See [README.md](README.md). Branching, commits and code standards live in [CONTRIBUTING.md](CONTRIBUTING.md)
and `.editorconfig`; follow them.

## Working rules

- Validate before agreeing: check the user's assumption against the code before confirming it.
- Keep changes focused; prefer editing existing code over adding new code.
- Every behavior change gets a test. Run the narrowest test that can falsify the change first.
- The article's listings come from this code. If a change alters a listing (endpoint, `InboxStatus`, the processor,
  the two SQL files) or the rule table, say so in the change instead of letting the article drift.
- Before creating a branch, run `git status --short`. Treat uncommitted work as something to preserve (commit,
  named stash, or `git worktree`), never discard.
- Never commit, push, or open a PR unless asked. Commits use Conventional Commits.
- Nothing may identify a real company, customer, person or proprietary system, including commit messages and PR text.
  Test data is synthetic.

## Commands

```bash
dotnet build -warnaserror
dotnet test                       # needs Docker (Testcontainers starts PostgreSQL)
dotnet format --verify-no-changes
docker compose up -d
```

## Code conventions (short form)

File-scoped namespaces, braces always, `var` always, `_camelCase` private fields, `PascalCase` constants,
`I`-prefixed interfaces, `Async` suffix, `CancellationToken ct` last, `sealed` and `internal` by default, no magic
strings or numbers, comments explain why. Details in CONTRIBUTING.md.

## Design invariants

- The endpoint stores the raw body and answers; it never rejects a body it cannot parse (it dead-letters it).
- The processor owns retries: a retry never overtakes an earlier unfinished event for the same entity, and an
  event that runs out of attempts is dead-lettered, not left as failed.
- `ChargeRules.Decide` is a pure function; the repair and the live handler both call it.
- The repair never writes in dry-run mode and writes one record at a time with the `xmin` check.
