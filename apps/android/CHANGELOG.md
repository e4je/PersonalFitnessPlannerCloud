# Changelog

## Unreleased - Unified repository integration

- Replaced the built-in plan identity with the canonical JSON UUID tree and shared recommendation/progression vectors.
- Preserved server user and entity UUIDs, added readiness/cardio/full-resync mapping, and consumed server plan rules at runtime.
- Hardened API-origin changes, refresh/logout token handling, and retention-gap recovery while protecting pending Outbox mutations.
- Added stable local fallback assignment behavior for a server current plan without an explicit assignment.
- Added transient-token bootstrap preflight and fail-closed account/local-mode switching so one account cannot inherit another account's Room cache, cursor, Outbox, or in-memory UI.
- Neutralized spreadsheet formulas in user-controlled CSV cells while preserving RFC-style quoting for commas, quotes, and line breaks.

## 1.0.0 - 2026-08-09

- Initial offline-first Android release.
- Added A/B full-body planning, synced-readiness/fatigue recommendations, exact alternative-exercise progression, workout execution, real history filters/trends, and persistent personal exercise notes.
- Added Room persistence, versioned plans, outbox synchronization, background retry, encrypted tokens and local export.
- Enforced HTTPS for build-time and runtime API base URLs, including localhost; HTTP remains available only inside explicit MockWebServer test construction.
- Excluded Room training records, preferences, tokens, and private export files from Android cloud backup and device transfer.
- Completed 43 Debug + 43 Release JVM tests, lint (0 errors), debug/release assembly, five device tests, installation, and a 953 ms cold-start acceptance pass.
