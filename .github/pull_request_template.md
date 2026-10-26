## What and why

<!-- One or two sentences. The problem first, then the change. -->

## How it was verified

- [ ] `dotnet build -warnaserror` clean
- [ ] `dotnet test` (Docker running)
- [ ] `dotnet format --verify-no-changes`

## Kept in sync

- [ ] README still describes what the code does
- [ ] If a listing of the article changed (endpoint, `InboxStatus`, the processor, the SQL files) or the rule table, the change says which
- [ ] Test data is synthetic: nothing comes from a real system

## Risk / notes for the reviewer

<!-- Behavior changes, anything that needs a second look. -->
