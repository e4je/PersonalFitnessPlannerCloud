package com.personalfitnessplanner.data.local

import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.google.common.truth.Truth.assertThat
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class Migration1To2Test {
    @Test
    fun migrationPreservesRowsAndAddsSyncSnapshotAndCursorStructures() {
        val configuration = SupportSQLiteOpenHelper.Configuration.builder(
            RuntimeEnvironment.getApplication(),
        )
            .name(null)
            .callback(
                object : SupportSQLiteOpenHelper.Callback(1) {
                    override fun onCreate(db: SupportSQLiteDatabase) {
                        db.execSQL(
                            "CREATE TABLE workout_sessions (id TEXT NOT NULL PRIMARY KEY)",
                        )
                        db.execSQL(
                            "CREATE TABLE workout_sets (id TEXT NOT NULL PRIMARY KEY)",
                        )
                        db.execSQL(
                            "CREATE TABLE plan_slot_options (id TEXT NOT NULL PRIMARY KEY)",
                        )
                    }

                    override fun onUpgrade(
                        db: SupportSQLiteDatabase,
                        oldVersion: Int,
                        newVersion: Int,
                    ) = Unit
                },
            )
            .build()
        val helper = FrameworkSQLiteOpenHelperFactory().create(configuration)
        val db = helper.writableDatabase
        try {
            db.execSQL("INSERT INTO workout_sessions(id) VALUES ('existing-session')")
            db.execSQL("INSERT INTO workout_sets(id) VALUES ('existing-set')")

            MIGRATION_1_2.migrate(db)

            assertThat(columns(db, "workout_sessions"))
                .containsAtLeast("plan_snapshot_json", "idempotency_key")
            assertThat(columns(db, "workout_sets"))
                .contains("source_plan_slot_option_id")
            assertThat(columns(db, "plan_slot_options"))
                .containsAtLeast("intro_set_count", "intro_weeks")
            assertThat(tableExists(db, "sync_outbox")).isTrue()
            assertThat(tableExists(db, "sync_state")).isTrue()
            assertThat(tableExists(db, "app_settings")).isTrue()

            db.query(
                "SELECT id, plan_snapshot_json, idempotency_key FROM workout_sessions",
            ).use { cursor ->
                assertThat(cursor.moveToFirst()).isTrue()
                assertThat(cursor.getString(0)).isEqualTo("existing-session")
                assertThat(cursor.getString(1)).isEqualTo("{}")
                assertThat(cursor.getString(2)).isEqualTo("existing-session")
            }
        } finally {
            helper.close()
        }
    }

    private fun columns(db: SupportSQLiteDatabase, table: String): List<String> =
        db.query("PRAGMA table_info(`$table`)").use { cursor ->
            val nameIndex = cursor.getColumnIndexOrThrow("name")
            buildList {
                while (cursor.moveToNext()) add(cursor.getString(nameIndex))
            }
        }

    private fun tableExists(db: SupportSQLiteDatabase, table: String): Boolean =
        db.query(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?",
            arrayOf(table),
        ).use { it.moveToFirst() }
}
