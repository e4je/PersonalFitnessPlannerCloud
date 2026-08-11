package com.personalfitnessplanner.data.local

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

enum class UnitSystem { KG, LB }

enum class PlanCode { A, B }

enum class WorkoutStatus { IN_PROGRESS, COMPLETED, ENDED_EARLY, DELETED }

enum class SetQuality { POOR, FAIR, GOOD }

enum class SyncOperation { UPSERT, DELETE }

enum class OutboxStatus { PENDING, IN_FLIGHT, FAILED }

enum class ThemeMode { SYSTEM, LIGHT, DARK }

@Entity(tableName = "users", indices = [Index(value = ["email"], unique = true)])
data class UserEntity(
    @PrimaryKey val id: String,
    val email: String,
    @ColumnInfo(name = "display_name") val displayName: String,
    val timezone: String,
    @ColumnInfo(name = "weight_unit") val weightUnit: UnitSystem,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "exercises",
    indices = [Index("name"), Index("equipment_id")],
)
data class ExerciseEntity(
    @PrimaryKey val id: String,
    val name: String,
    @ColumnInfo(name = "body_part") val bodyPart: String,
    @ColumnInfo(name = "equipment_id") val equipmentId: String?,
    @ColumnInfo(name = "default_sets") val defaultSets: Int,
    @ColumnInfo(name = "rep_min") val repMin: Int,
    @ColumnInfo(name = "rep_max") val repMax: Int,
    @ColumnInfo(name = "rep_unit") val repUnit: String = "reps",
    val cues: String,
    @ColumnInfo(name = "common_mistakes") val commonMistakes: String,
    @ColumnInfo(name = "definition_version") val definitionVersion: Int = 1,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(tableName = "equipment", indices = [Index("name")])
data class EquipmentEntity(
    @PrimaryKey val id: String,
    val name: String,
    val category: String,
    val brand: String? = null,
    val model: String? = null,
    val notes: String? = null,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "exercise_alternatives",
    indices = [
        Index("exercise_id"),
        Index("alternative_exercise_id"),
        Index(value = ["exercise_id", "alternative_exercise_id"], unique = true),
    ],
)
data class ExerciseAlternativeEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "exercise_id") val exerciseId: String,
    @ColumnInfo(name = "alternative_exercise_id") val alternativeExerciseId: String,
    @ColumnInfo(name = "sort_order") val sortOrder: Int,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(tableName = "training_plans", indices = [Index("name")])
data class TrainingPlanEntity(
    @PrimaryKey val id: String,
    val name: String,
    val description: String,
    @ColumnInfo(name = "is_built_in") val isBuiltIn: Boolean,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "plan_versions",
    indices = [Index("plan_id"), Index(value = ["plan_id", "version_number"], unique = true)],
)
data class PlanVersionEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "plan_id") val planId: String,
    @ColumnInfo(name = "version_number") val versionNumber: Int,
    val status: String,
    @ColumnInfo(name = "published_at") val publishedAt: Long?,
    @ColumnInfo(name = "snapshot_json") val snapshotJson: String,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "plan_days",
    indices = [Index("plan_version_id"), Index(value = ["plan_version_id", "code"], unique = true)],
)
data class PlanDayEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "plan_version_id") val planVersionId: String,
    val code: PlanCode,
    val name: String,
    @ColumnInfo(name = "sort_order") val sortOrder: Int,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "plan_slots",
    indices = [Index("plan_day_id"), Index(value = ["plan_day_id", "position"], unique = true)],
)
data class PlanSlotEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "plan_day_id") val planDayId: String,
    val position: Int,
    @ColumnInfo(name = "body_part") val bodyPart: String,
    val cues: String,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "plan_slot_options",
    indices = [
        Index("plan_slot_id"),
        Index("exercise_id"),
        Index(value = ["plan_slot_id", "exercise_id"], unique = true),
    ],
)
data class PlanSlotOptionEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "plan_slot_id") val planSlotId: String,
    @ColumnInfo(name = "exercise_id") val exerciseId: String,
    @ColumnInfo(name = "equipment_id") val equipmentId: String?,
    @ColumnInfo(name = "is_preferred") val isPreferred: Boolean,
    @ColumnInfo(name = "sort_order") val sortOrder: Int,
    @ColumnInfo(name = "set_count") val setCount: Int,
    @ColumnInfo(name = "intro_set_count", defaultValue = "2") val introSetCount: Int = 2,
    @ColumnInfo(name = "intro_weeks", defaultValue = "2") val introWeeks: Int = 2,
    @ColumnInfo(name = "rep_min") val repMin: Int,
    @ColumnInfo(name = "rep_max") val repMax: Int,
    @ColumnInfo(name = "rep_unit") val repUnit: String = "reps",
    @ColumnInfo(name = "rir_min") val rirMin: Int = 2,
    @ColumnInfo(name = "rir_max") val rirMax: Int = 3,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "plan_assignments",
    indices = [Index("user_id"), Index("plan_version_id"), Index(value = ["user_id", "is_active"])],
)
data class PlanAssignmentEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "user_id") val userId: String,
    @ColumnInfo(name = "plan_version_id") val planVersionId: String,
    @ColumnInfo(name = "start_local_date") val startLocalDate: String,
    @ColumnInfo(name = "end_local_date") val endLocalDate: String? = null,
    @ColumnInfo(name = "is_active") val isActive: Boolean = true,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "workout_sessions",
    indices = [
        Index("user_id"),
        Index("plan_version_id"),
        Index("local_date"),
        Index(value = ["idempotency_key"], unique = true),
    ],
)
data class WorkoutSessionEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "user_id") val userId: String,
    @ColumnInfo(name = "plan_version_id") val planVersionId: String?,
    @ColumnInfo(name = "plan_day_code") val planDayCode: PlanCode?,
    @ColumnInfo(name = "local_date") val localDate: String,
    val timezone: String,
    @ColumnInfo(name = "started_at") val startedAt: Long,
    @ColumnInfo(name = "completed_at") val completedAt: Long?,
    val status: WorkoutStatus,
    @ColumnInfo(name = "is_full_body") val isFullBody: Boolean = true,
    @ColumnInfo(name = "plan_snapshot_json") val planSnapshotJson: String,
    @ColumnInfo(name = "idempotency_key") val idempotencyKey: String,
    val notes: String? = null,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "workout_sets",
    indices = [Index("session_id"), Index("exercise_id"), Index("source_plan_slot_option_id")],
)
data class WorkoutSetEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "session_id") val sessionId: String,
    @ColumnInfo(name = "plan_slot_id") val planSlotId: String?,
    @ColumnInfo(name = "source_plan_slot_option_id") val sourcePlanSlotOptionId: String?,
    @ColumnInfo(name = "exercise_id") val exerciseId: String,
    @ColumnInfo(name = "equipment_id") val equipmentId: String?,
    @ColumnInfo(name = "set_number") val setNumber: Int,
    @ColumnInfo(name = "weight_kg") val weightKg: Double?,
    val reps: Int?,
    @ColumnInfo(name = "duration_seconds") val durationSeconds: Int?,
    @ColumnInfo(name = "is_warmup") val isWarmup: Boolean,
    val rir: Int?,
    val quality: SetQuality?,
    val pain: Boolean,
    val notes: String? = null,
    val completed: Boolean,
    @ColumnInfo(name = "completed_at") val completedAt: Long?,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "daily_readiness",
    indices = [Index("user_id"), Index(value = ["user_id", "local_date"], unique = true)],
)
data class DailyReadinessEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "user_id") val userId: String,
    @ColumnInfo(name = "local_date") val localDate: String,
    @ColumnInfo(name = "fatigue_score") val fatigueScore: Int,
    @ColumnInfo(name = "sleep_quality") val sleepQuality: Int?,
    @ColumnInfo(name = "pain_notes") val painNotes: String? = null,
    val notes: String? = null,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(tableName = "cardio_sessions", indices = [Index("user_id"), Index("local_date")])
data class CardioSessionEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "user_id") val userId: String,
    @ColumnInfo(name = "local_date") val localDate: String,
    val activity: String,
    @ColumnInfo(name = "duration_minutes") val durationMinutes: Int,
    @ColumnInfo(name = "distance_km") val distanceKm: Double?,
    val notes: String? = null,
    @ColumnInfo(name = "started_at") val startedAt: Long,
    @ColumnInfo(name = "completed_at") val completedAt: Long?,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "sync_outbox",
    indices = [
        Index("aggregate_id"),
        Index(value = ["idempotency_key"], unique = true),
        Index(value = ["status", "next_attempt_at"]),
    ],
)
data class SyncOutboxEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "aggregate_type") val aggregateType: String,
    @ColumnInfo(name = "aggregate_id") val aggregateId: String,
    val operation: SyncOperation,
    @ColumnInfo(name = "payload_json") val payloadJson: String,
    @ColumnInfo(name = "idempotency_key") val idempotencyKey: String,
    val status: OutboxStatus = OutboxStatus.PENDING,
    @ColumnInfo(name = "attempt_count") val attemptCount: Int = 0,
    @ColumnInfo(name = "next_attempt_at") val nextAttemptAt: Long,
    @ColumnInfo(name = "last_error") val lastError: String? = null,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(
    tableName = "sync_state",
    indices = [Index("user_id"), Index(value = ["user_id", "scope"], unique = true)],
)
data class SyncStateEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "user_id") val userId: String,
    val scope: String,
    val cursor: String?,
    @ColumnInfo(name = "last_synced_at") val lastSyncedAt: Long?,
    @ColumnInfo(name = "full_resync_required") val fullResyncRequired: Boolean = false,
    @ColumnInfo(name = "last_error") val lastError: String? = null,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)

@Entity(tableName = "app_settings", indices = [Index("user_id")])
data class AppSettingsEntity(
    @PrimaryKey val id: String,
    @ColumnInfo(name = "user_id") val userId: String?,
    @ColumnInfo(name = "api_base_url") val apiBaseUrl: String,
    val timezone: String,
    @ColumnInfo(name = "weight_unit") val weightUnit: UnitSystem,
    @ColumnInfo(name = "training_days_json") val trainingDaysJson: String,
    @ColumnInfo(name = "rest_seconds") val restSeconds: Int,
    @ColumnInfo(name = "theme_mode") val themeMode: ThemeMode,
    @ColumnInfo(name = "onboarding_complete") val onboardingComplete: Boolean,
    val version: Long = 1,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    @ColumnInfo(name = "deleted_at") val deletedAt: Long? = null,
)
