package com.personalfitnessplanner.data.remote

import com.squareup.moshi.Json
import com.squareup.moshi.JsonClass

/** Wire models intentionally keep timestamps as ISO-8601 strings. */
@JsonClass(generateAdapter = true)
data class LoginRequestDto(
    val email: String,
    val password: String,
    @Json(name = "device_name") val deviceName: String? = null,
)

@JsonClass(generateAdapter = true)
data class RefreshTokenRequestDto(
    @Json(name = "refresh_token") val refreshToken: String,
)

@JsonClass(generateAdapter = true)
data class LogoutRequestDto(
    @Json(name = "refresh_token") val refreshToken: String? = null,
)

@JsonClass(generateAdapter = true)
data class AuthTokensDto(
    @Json(name = "access_token") val accessToken: String,
    @Json(name = "refresh_token") val refreshToken: String? = null,
    @Json(name = "token_type") val tokenType: String = "Bearer",
    @Json(name = "expires_in") val expiresInSeconds: Long? = null,
    @Json(name = "expires_at") val expiresAtEpochSeconds: Long? = null,
)

@JsonClass(generateAdapter = true)
data class ApiMessageDto(
    val message: String = "",
)

@JsonClass(generateAdapter = true)
data class UserDto(
    val id: String,
    val email: String = "",
    @Json(name = "display_name") val displayName: String = "",
    val timezone: String = "UTC",
    @Json(name = "weight_unit") val weightUnit: String = "KG",
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class EquipmentDto(
    val id: String,
    val name: String = "",
    val category: String = "",
    val brand: String? = null,
    val model: String? = null,
    val notes: String? = null,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class ExerciseAlternativeDto(
    val id: String,
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "alternative_exercise_id") val alternativeExerciseId: String,
    @Json(name = "sort_order") val sortOrder: Int = 0,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class ExerciseDto(
    val id: String,
    val name: String = "",
    @Json(name = "body_part") val bodyPart: String = "",
    @Json(name = "equipment_id") val equipmentId: String? = null,
    @Json(name = "default_sets") val defaultSets: Int = 0,
    @Json(name = "rep_min") val repMin: Int = 0,
    @Json(name = "rep_max") val repMax: Int = 0,
    @Json(name = "rep_unit") val repUnit: String = "reps",
    val cues: String = "",
    @Json(name = "common_mistakes") val commonMistakes: String = "",
    @Json(name = "definition_version") val definitionVersion: Int = 1,
    val alternatives: List<ExerciseAlternativeDto> = emptyList(),
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class PlanSlotOptionDto(
    val id: String,
    @Json(name = "plan_slot_id") val planSlotId: String,
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "equipment_id") val equipmentId: String? = null,
    @Json(name = "is_preferred") val isPreferred: Boolean = false,
    @Json(name = "sort_order") val sortOrder: Int = 0,
    @Json(name = "set_count") val setCount: Int = 0,
    @Json(name = "intro_set_count") val introSetCount: Int? = null,
    @Json(name = "intro_weeks") val introWeeks: Int? = null,
    @Json(name = "rep_min") val repMin: Int = 0,
    @Json(name = "rep_max") val repMax: Int = 0,
    @Json(name = "rep_unit") val repUnit: String = "reps",
    @Json(name = "rest_seconds") val restSeconds: Int? = null,
    @Json(name = "duration_seconds_min") val durationSecondsMin: Int? = null,
    @Json(name = "duration_seconds_max") val durationSecondsMax: Int? = null,
    @Json(name = "is_per_side") val isPerSide: Boolean = false,
    @Json(name = "prescription_text") val prescriptionText: String? = null,
    @Json(name = "rir_min") val rirMin: Int = 2,
    @Json(name = "rir_max") val rirMax: Int = 3,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class PlanSlotDto(
    val id: String,
    @Json(name = "plan_day_id") val planDayId: String,
    val position: Int = 0,
    @Json(name = "body_part") val bodyPart: String = "",
    val cues: String = "",
    val options: List<PlanSlotOptionDto> = emptyList(),
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class PlanDayDto(
    val id: String,
    @Json(name = "plan_version_id") val planVersionId: String,
    val code: String = "A",
    val name: String = "",
    @Json(name = "sort_order") val sortOrder: Int = 0,
    val slots: List<PlanSlotDto> = emptyList(),
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class PlanVersionDto(
    val id: String,
    @Json(name = "plan_id") val planId: String,
    @Json(name = "plan_name") val planName: String = "",
    @Json(name = "version_number") val versionNumber: Int = 1,
    val status: String = "published",
    @Json(name = "published_at") val publishedAt: String? = null,
    @Json(name = "snapshot_json") val snapshotJson: String? = null,
    @Json(name = "weekly_frequency") val weeklyFrequency: Int? = null,
    @Json(name = "min_rest_days") val minRestDays: Int? = null,
    @Json(name = "fatigue_threshold") val fatigueThreshold: Int? = null,
    @Json(name = "initial_reduced_weeks") val initialReducedWeeks: Int? = null,
    @Json(name = "initial_set_count") val initialSetCount: Int? = null,
    val rules: Map<String, Any?> = emptyMap(),
    val days: List<PlanDayDto> = emptyList(),
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class PlanAssignmentDto(
    val id: String,
    @Json(name = "user_id") val userId: String,
    @Json(name = "plan_version_id") val planVersionId: String,
    @Json(name = "start_local_date") val startLocalDate: String,
    @Json(name = "end_local_date") val endLocalDate: String? = null,
    @Json(name = "is_active") val isActive: Boolean = true,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class WorkoutSetDto(
    val id: String,
    @Json(name = "session_id") val sessionId: String,
    @Json(name = "plan_slot_id") val planSlotId: String? = null,
    @Json(name = "source_plan_slot_option_id") val sourcePlanSlotOptionId: String? = null,
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "equipment_id") val equipmentId: String? = null,
    @Json(name = "set_number") val setNumber: Int,
    @Json(name = "weight_kg") val weightKg: Double? = null,
    val reps: Int? = null,
    @Json(name = "duration_seconds") val durationSeconds: Int? = null,
    @Json(name = "is_warmup") val isWarmup: Boolean = false,
    val rir: Int? = null,
    val quality: String? = null,
    val pain: Boolean = false,
    val notes: String? = null,
    val completed: Boolean = false,
    @Json(name = "completed_at") val completedAt: String? = null,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class WorkoutSessionDto(
    val id: String,
    @Json(name = "user_id") val userId: String,
    @Json(name = "plan_version_id") val planVersionId: String? = null,
    @Json(name = "plan_day_code") val planDayCode: String? = null,
    @Json(name = "local_date") val localDate: String,
    val timezone: String = "UTC",
    @Json(name = "started_at") val startedAt: String,
    @Json(name = "completed_at") val completedAt: String? = null,
    val status: String = "IN_PROGRESS",
    @Json(name = "is_full_body") val isFullBody: Boolean = true,
    @Json(name = "plan_snapshot_json") val planSnapshotJson: String = "{}",
    @Json(name = "idempotency_key") val idempotencyKey: String? = null,
    val notes: String? = null,
    val sets: List<WorkoutSetDto> = emptyList(),
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class WorkoutSetUpsertDto(
    val id: String,
    @Json(name = "plan_slot_id") val planSlotId: String? = null,
    @Json(name = "source_plan_slot_option_id") val sourcePlanSlotOptionId: String? = null,
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "equipment_id") val equipmentId: String? = null,
    @Json(name = "set_number") val setNumber: Int,
    @Json(name = "weight_kg") val weightKg: Double? = null,
    val reps: Int? = null,
    @Json(name = "duration_seconds") val durationSeconds: Int? = null,
    @Json(name = "is_warmup") val isWarmup: Boolean = false,
    val rir: Int? = null,
    val quality: String? = null,
    val pain: Boolean = false,
    val notes: String? = null,
    val completed: Boolean = false,
    @Json(name = "completed_at") val completedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class WorkoutSessionUpsertDto(
    val id: String,
    @Json(name = "plan_version_id") val planVersionId: String? = null,
    @Json(name = "plan_day_code") val planDayCode: String? = null,
    @Json(name = "local_date") val localDate: String,
    val timezone: String,
    @Json(name = "started_at") val startedAt: String,
    @Json(name = "completed_at") val completedAt: String? = null,
    val status: String,
    @Json(name = "is_full_body") val isFullBody: Boolean = true,
    @Json(name = "plan_snapshot_json") val planSnapshotJson: String,
    val notes: String? = null,
    val sets: List<WorkoutSetUpsertDto> = emptyList(),
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class ReadinessDto(
    val id: String,
    @Json(name = "user_id") val userId: String,
    @Json(name = "local_date") val localDate: String,
    @Json(name = "fatigue_score") val fatigueScore: Int,
    @Json(name = "sleep_quality") val sleepQuality: Int? = null,
    @Json(name = "pain_notes") val painNotes: String? = null,
    val notes: String? = null,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class ReadinessUpsertDto(
    val id: String,
    @Json(name = "local_date") val localDate: String,
    @Json(name = "fatigue_score") val fatigueScore: Int,
    @Json(name = "sleep_quality") val sleepQuality: Int? = null,
    @Json(name = "pain_notes") val painNotes: String? = null,
    val notes: String? = null,
)

@JsonClass(generateAdapter = true)
data class CardioSessionDto(
    val id: String,
    @Json(name = "user_id") val userId: String,
    @Json(name = "local_date") val localDate: String,
    val activity: String = "",
    @Json(name = "activity_type") val activityType: String = "",
    @Json(name = "duration_minutes") val durationMinutes: Int = 0,
    @Json(name = "duration_seconds") val durationSeconds: Int = 0,
    @Json(name = "distance_km") val distanceKm: Double? = null,
    @Json(name = "distance_meters") val distanceMeters: Double? = null,
    val notes: String? = null,
    @Json(name = "started_at") val startedAt: String,
    @Json(name = "completed_at") val completedAt: String? = null,
    val version: Long = 1,
    @Json(name = "created_at") val createdAt: String? = null,
    @Json(name = "updated_at") val updatedAt: String? = null,
    @Json(name = "deleted_at") val deletedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class ExercisePageDto(
    val items: List<ExerciseDto> = emptyList(),
    val cursor: String? = null,
    @Json(name = "next_cursor") val nextCursor: String? = null,
    @Json(name = "has_more") val hasMore: Boolean = false,
)

@JsonClass(generateAdapter = true)
data class EquipmentPageDto(
    val items: List<EquipmentDto> = emptyList(),
    val cursor: String? = null,
    @Json(name = "next_cursor") val nextCursor: String? = null,
    @Json(name = "has_more") val hasMore: Boolean = false,
)

@JsonClass(generateAdapter = true)
data class WorkoutSessionPageDto(
    val items: List<WorkoutSessionDto> = emptyList(),
    val cursor: String? = null,
    @Json(name = "next_cursor") val nextCursor: String? = null,
    @Json(name = "has_more") val hasMore: Boolean = false,
)

@JsonClass(generateAdapter = true)
data class BootstrapDto(
    val user: UserDto? = null,
    @Json(name = "current_plan") val currentPlan: PlanVersionDto? = null,
    @Json(name = "plan_versions") val planVersions: List<PlanVersionDto> = emptyList(),
    val exercises: List<ExerciseDto> = emptyList(),
    val equipment: List<EquipmentDto> = emptyList(),
    val assignments: List<PlanAssignmentDto> = emptyList(),
    @Json(name = "workout_sessions") val workoutSessions: List<WorkoutSessionDto> = emptyList(),
    val readiness: List<ReadinessDto> = emptyList(),
    @Json(name = "cardio_sessions") val cardioSessions: List<CardioSessionDto> = emptyList(),
    val cursor: String? = null,
    @Json(name = "sync_cursor") val syncCursor: String? = null,
)

@JsonClass(generateAdapter = true)
data class SyncChangeDto(
    val id: String,
    @Json(name = "entity_type") val entityType: String,
    @Json(name = "entity_id") val entityId: String,
    val operation: String = "UPSERT",
    val version: Long = 1,
    val payload: Map<String, Any?>? = null,
    @Json(name = "changed_at") val changedAt: String? = null,
)

@JsonClass(generateAdapter = true)
data class SyncChangesDto(
    val changes: List<SyncChangeDto> = emptyList(),
    val cursor: String? = null,
    @Json(name = "next_cursor") val nextCursor: String? = null,
    @Json(name = "has_more") val hasMore: Boolean = false,
    @Json(name = "full_resync_required") val fullResyncRequired: Boolean = false,
)

@JsonClass(generateAdapter = true)
data class SyncOperationDto(
    val id: String,
    @Json(name = "idempotency_key") val idempotencyKey: String,
    @Json(name = "entity_type") val entityType: String,
    @Json(name = "entity_id") val entityId: String,
    val operation: String,
    val payload: Map<String, Any?>? = null,
)

@JsonClass(generateAdapter = true)
data class SyncBatchRequestDto(
    @Json(name = "batch_id") val batchId: String,
    @Json(name = "sent_at") val sentAt: String,
    val operations: List<SyncOperationDto>,
)

@JsonClass(generateAdapter = true)
data class SyncBatchItemResultDto(
    val id: String,
    val status: String,
    val error: String? = null,
    @Json(name = "server_version") val serverVersion: Long? = null,
)

@JsonClass(generateAdapter = true)
data class SyncBatchResponseDto(
    @Json(name = "batch_id") val batchId: String? = null,
    val results: List<SyncBatchItemResultDto> = emptyList(),
    val cursor: String? = null,
)
