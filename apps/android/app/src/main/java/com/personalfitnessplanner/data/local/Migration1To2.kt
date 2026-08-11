package com.personalfitnessplanner.data.local

import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

/**
 * Version 2 introduced immutable workout plan snapshots, per-request idempotency,
 * precise selected-option tracking, the durable outbox, incremental cursors, and
 * persisted application settings. Existing user data is altered in place.
 */
val MIGRATION_1_2: Migration = object : Migration(1, 2) {
    override fun migrate(database: SupportSQLiteDatabase) {
        database.execSQL(
            "ALTER TABLE workout_sessions ADD COLUMN plan_snapshot_json TEXT NOT NULL DEFAULT '{}'",
        )
        database.execSQL(
            "ALTER TABLE workout_sessions ADD COLUMN idempotency_key TEXT NOT NULL DEFAULT ''",
        )
        database.execSQL(
            "UPDATE workout_sessions SET idempotency_key = id WHERE idempotency_key = ''",
        )
        database.execSQL(
            "ALTER TABLE workout_sets ADD COLUMN source_plan_slot_option_id TEXT",
        )
        database.execSQL(
            "ALTER TABLE plan_slot_options ADD COLUMN intro_set_count INTEGER NOT NULL DEFAULT 2",
        )
        database.execSQL(
            "ALTER TABLE plan_slot_options ADD COLUMN intro_weeks INTEGER NOT NULL DEFAULT 2",
        )

        database.execSQL(
            """
            CREATE TABLE IF NOT EXISTS sync_outbox (
                id TEXT NOT NULL PRIMARY KEY,
                aggregate_type TEXT NOT NULL,
                aggregate_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                next_attempt_at INTEGER NOT NULL,
                last_error TEXT,
                version INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                deleted_at INTEGER
            )
            """.trimIndent(),
        )
        database.execSQL(
            """
            CREATE TABLE IF NOT EXISTS sync_state (
                id TEXT NOT NULL PRIMARY KEY,
                user_id TEXT NOT NULL,
                scope TEXT NOT NULL,
                cursor TEXT,
                last_synced_at INTEGER,
                full_resync_required INTEGER NOT NULL,
                last_error TEXT,
                version INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                deleted_at INTEGER
            )
            """.trimIndent(),
        )
        database.execSQL(
            """
            CREATE TABLE IF NOT EXISTS app_settings (
                id TEXT NOT NULL PRIMARY KEY,
                user_id TEXT,
                api_base_url TEXT NOT NULL,
                timezone TEXT NOT NULL,
                weight_unit TEXT NOT NULL,
                training_days_json TEXT NOT NULL,
                rest_seconds INTEGER NOT NULL,
                theme_mode TEXT NOT NULL,
                onboarding_complete INTEGER NOT NULL,
                version INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                deleted_at INTEGER
            )
            """.trimIndent(),
        )

        database.execSQL(
            "CREATE UNIQUE INDEX IF NOT EXISTS index_workout_sessions_idempotency_key " +
                "ON workout_sessions(idempotency_key)",
        )
        database.execSQL(
            "CREATE INDEX IF NOT EXISTS index_workout_sets_source_plan_slot_option_id " +
                "ON workout_sets(source_plan_slot_option_id)",
        )
        database.execSQL(
            "CREATE INDEX IF NOT EXISTS index_sync_outbox_aggregate_id ON sync_outbox(aggregate_id)",
        )
        database.execSQL(
            "CREATE UNIQUE INDEX IF NOT EXISTS index_sync_outbox_idempotency_key " +
                "ON sync_outbox(idempotency_key)",
        )
        database.execSQL(
            "CREATE INDEX IF NOT EXISTS index_sync_outbox_status_next_attempt_at " +
                "ON sync_outbox(status, next_attempt_at)",
        )
        database.execSQL(
            "CREATE INDEX IF NOT EXISTS index_sync_state_user_id ON sync_state(user_id)",
        )
        database.execSQL(
            "CREATE UNIQUE INDEX IF NOT EXISTS index_sync_state_user_id_scope " +
                "ON sync_state(user_id, scope)",
        )
        database.execSQL(
            "CREATE INDEX IF NOT EXISTS index_app_settings_user_id ON app_settings(user_id)",
        )
    }
}
