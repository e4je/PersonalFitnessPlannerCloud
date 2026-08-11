# Test inventory

> Historical baseline: the 43/43/5 counts and `app/build` report paths below belong to the pre-integration Android handoff. They were not regenerated from the current unified source tree.

Automated JVM tests live in `app/src/test`; device tests live in `app/src/androidTest`. The final build report in `README.md` records commands and outcomes.

Coverage targets include recommendation rules, progression rules, independent alternative-exercise loads, idempotent outbox behavior, authentication refresh, Room migration, and key Compose screens.

Saved acceptance reports:

- `app/build/reports/tests/testDebugUnitTest/index.html`: 43 tests, 0 failures, 0 skipped.
- `app/build/reports/tests/testReleaseUnitTest/index.html`: 43 tests, 0 failures, 0 skipped.
- `app/build/reports/androidTests/connected/debug/index.html`: 5 tests, 0 failures, 0 skipped, REDMI Android 14 / API 34.
- `app/build/reports/lint-results-debug.html`: 0 errors, 10 non-blocking warnings.

Those reports were generated from the original handoff source before integration. See the repository-root `docs/test-report.md` for current verification status.
