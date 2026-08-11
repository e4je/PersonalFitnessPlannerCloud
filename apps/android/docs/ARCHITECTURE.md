# Architecture

Personal Fitness Planner is a single-activity, offline-first Android application.

```text
Compose UI -> ViewModel / StateFlow -> Repository
                                   |-> Room (authoritative local cache)
                                   |-> Retrofit / OkHttp (HTTPS API)
                                   |-> DataStore (preferences)
                                   `-> Android Keystore (tokens)

Room outbox -> SyncCoordinator -> WorkManager -> /api/v1/sync/batch
```

The application uses unidirectional state updates. Published plan versions are immutable; a workout stores its plan-version ID and exercise snapshot. Server-owned exercise/plan definitions are never edited by the regular client. Unsynced workout events remain local until acknowledged by the API. Personal equipment-note editing/persistence is not wired in the current release.

## Conflict policy

- Exercise, equipment, plan, and assignment conflicts: server wins.
- Workout writes: idempotency key prevents duplicate sessions or sets.
- Deletion: soft deletion (`deleted_at`) is synchronized.
- Pull: an opaque cursor is stored in `sync_state` and advanced only after a transaction commits.
- A newly downloaded plan becomes active only after any in-progress workout ends.

## Database lifecycle

Room schema exports are committed under `app/schemas`. Database upgrades use explicit migrations. `fallbackToDestructiveMigration` is intentionally not enabled.

## Backup and sensitive-data boundary

- Runtime access/refresh tokens are AES-GCM encrypted with a non-exportable Android Keystore key.
- Android Auto Backup, cloud backup, and device-to-device transfer exclude the complete Room database, preferences/DataStore, app-private files, and external app files. This prevents automatic copying of workout/health records, outbox contents, settings, encrypted-token blobs, and local JSON backups.
- Cache exports used for Android sharing are temporary and are not system-backup inputs.
- Record migration is explicit: the user exports CSV/JSON and controls the destination. The local JSON export itself must be treated as sensitive plaintext outside the app sandbox.

This policy intentionally favors health-data confidentiality over transparent device restore. Reinstalling the app starts with an empty local database unless the user separately retained an export or reconnects to a compatible backend.
