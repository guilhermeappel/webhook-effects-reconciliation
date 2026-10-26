# Contributing

This repository is the companion code of an article, so changes are held to one bar: the code, the tests and
the README must say the same thing as the article's listings. A change that makes one of them untrue is not
finished.

## Branching

Trunk-based, with `main` as the only long-lived branch and always releasable (build and tests green).

- Branch from an up-to-date `main`. Keep branches short-lived.
- Name branches `<type>/<short-kebab-description>`, where type is one of
  `feature`, `fix`, `hotfix`, `chore`, `docs`, `test`, `refactor`.
- One concern per branch. Do not commit to `main` directly, and never rewrite history on `main`.
- Before starting a branch, check `git status --short`. Uncommitted work is preserved first (commit, named
  stash, or a separate `git worktree`), never discarded with `reset --hard` or `checkout -f`.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `refactor:`, `test:`, `docs:`,
`build:`, `ci:`, `chore:`, optionally scoped. Imperative, lowercase subject, no trailing period. Explain the *why* in the body
when it is not obvious from the diff.

## Pull requests

- Target `main`. Open as draft until CI is green and you have self-reviewed the diff.
- Squash-merge, so each PR is one Conventional Commit on `main`. The PR title must itself be a valid one. (The
  maintainer may land a series of focused commits as they are, when each one stands on its own.)
- Fill in the [template](.github/pull_request_template.md).
- Behavior changes need a test. A change to a listing of the article is called out in the PR.
- Before requesting review: `dotnet build -warnaserror`, `dotnet test` (Docker required) and
  `dotnet format --verify-no-changes`.

## Code standards

Enforced by [`.editorconfig`](.editorconfig); the rules that matter most:

- .NET 10, C# latest, `Nullable` enabled, file-scoped namespaces, `using` directives outside the namespace with
  `System.*` first.
- LF line endings, UTF-8 without BOM, 4-space indent (2 for XML/JSON/YAML).
- Always use braces on control structures. Always use `var` for locals.
- Naming: `PascalCase` types, members and constants; `_camelCase` private fields; `I` prefix on interfaces;
  `Async` suffix on async methods.
- Async methods take `CancellationToken ct` as the last parameter and pass it through.
- Default to `sealed`. Types are `internal`: the test project sees them through `InternalsVisibleTo`, and CI
  rejects a public type in the application (CA1515, a warning that `-warnaserror` makes an error).
- No unexplained magic numbers: name limits, timings and thresholds as constants or options. The provider's event
  type names are written where they are matched.
- The current time comes from an injected `TimeProvider` in logic, so a test can control it
  (`FakeTimeProvider`). The endpoint stamps `ReceivedAt` with that same injected clock.
- Comments explain *why*, not *what*. Use English in code, comments, docs and commit messages.
- Package versions live in `Directory.Packages.props` (central package management). A `PackageReference` in a
  project file carries no version.

The build runs a pinned analyzer rule set and the rules of `.editorconfig` that are set to `warning`: file-scoped
namespaces, braces, accessibility modifiers and CA1515. CI builds with `-warnaserror`, which makes a violation of
those an error, and runs `dotnet format --verify-no-changes`, which also catches the `I` prefix on interfaces. The
other style rules (`var`, the placement of `using`, the names of private fields and constants, the `Async` suffix)
are suggestions that neither command enforces, so follow them and look for them in review. Rules that do not fit this repository are switched off in
`.editorconfig`, each with the reason next to it. `nuget.config` restores packages from nuget.org only, so a
contributor's own NuGet sources do not take part in the build.

## Tests

- `tests/WebhookInbox.Tests` uses xUnit and Testcontainers. Anything that touches PostgreSQL semantics (`xmin`,
  unique indexes, ordering by entity) runs against a real PostgreSQL, not an in-memory substitute.
- Test names describe behavior: `Subject_Condition_ExpectedOutcome`, for example
  `Retry_AfterMaxAttempts_TheEventIsDeadLetteredAndStopsBlockingLaterEvents`.
- A bug fix starts with a test that fails for the reason the bug exists.
- Tests are deterministic: no fixed sleeps standing in for a condition; poll with a timeout (`Polling.UntilAsync`)
  and control time with `FakeTimeProvider`.
- The tests share one PostgreSQL server (`PostgresFixture`) and each test creates a database of its own on it.
  Test parallelization is off because the fixture clears Npgsql's process-wide connection pools and the repair
  tests capture the console.

## Public-facing material

Everything in this repository is public. Test data and examples are synthetic: nothing in the code, the docs, the
commit messages or the PR descriptions comes from a real system.

## Security

Report vulnerabilities privately, not in a public issue or PR: see [SECURITY.md](SECURITY.md).
