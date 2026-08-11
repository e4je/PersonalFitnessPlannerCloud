package com.personalfitnessplanner.data.remote

import com.personalfitnessplanner.data.local.DailyReadinessEntity
import com.personalfitnessplanner.data.local.CardioSessionEntity
import com.personalfitnessplanner.data.local.EquipmentEntity
import com.personalfitnessplanner.data.local.ExerciseAlternativeEntity
import com.personalfitnessplanner.data.local.ExerciseEntity
import com.personalfitnessplanner.data.local.PlanAssignmentEntity
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.PlanDayEntity
import com.personalfitnessplanner.data.local.PlanSlotEntity
import com.personalfitnessplanner.data.local.PlanSlotOptionEntity
import com.personalfitnessplanner.data.local.PlanVersionEntity
import com.personalfitnessplanner.data.local.SetQuality
import com.personalfitnessplanner.data.local.TrainingPlanEntity
import com.personalfitnessplanner.data.local.UnitSystem
import com.personalfitnessplanner.data.local.UserEntity
import com.personalfitnessplanner.data.local.WorkoutSessionEntity
import com.personalfitnessplanner.data.local.WorkoutSetEntity
import com.personalfitnessplanner.data.local.WorkoutStatus
import com.squareup.moshi.Moshi
import com.squareup.moshi.kotlin.reflect.KotlinJsonAdapterFactory
import java.time.Instant

/** Pure wire-to-Room mappings shared by bootstrap and incremental synchronization. */
internal object RemoteMappers {
    private const val COMPATIBILITY_INTRO_WEEKS = 2
    private const val COMPATIBILITY_INTRO_SET_COUNT = 2
    private val planSnapshotAdapter by lazy(LazyThreadSafetyMode.PUBLICATION) {
        Moshi.Builder()
            .addLast(KotlinJsonAdapterFactory())
            .build()
            .adapter(PlanVersionDto::class.java)
    }

    fun user(dto: UserDto, now: Long): UserEntity = UserEntity(
        id = dto.id,
        email = dto.email,
        displayName = dto.displayName,
        timezone = dto.timezone,
        weightUnit = enumValueOrDefault(dto.weightUnit, UnitSystem.KG),
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun equipment(dto: EquipmentDto, now: Long): EquipmentEntity = EquipmentEntity(
        id = dto.id,
        name = dto.name,
        category = dto.category,
        brand = dto.brand,
        model = dto.model,
        notes = dto.notes,
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun exercise(dto: ExerciseDto, now: Long): ExerciseEntity = ExerciseEntity(
        id = dto.id,
        name = dto.name,
        bodyPart = dto.bodyPart,
        equipmentId = dto.equipmentId,
        defaultSets = dto.defaultSets,
        repMin = dto.repMin,
        repMax = dto.repMax,
        repUnit = dto.repUnit,
        cues = dto.cues,
        commonMistakes = dto.commonMistakes,
        definitionVersion = dto.definitionVersion,
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun alternative(dto: ExerciseAlternativeDto, now: Long): ExerciseAlternativeEntity =
        ExerciseAlternativeEntity(
            id = dto.id,
            exerciseId = dto.exerciseId,
            alternativeExerciseId = dto.alternativeExerciseId,
            sortOrder = dto.sortOrder,
            version = dto.version,
            createdAt = dto.createdAt.epochMillis(now),
            updatedAt = dto.updatedAt.epochMillis(now),
            deletedAt = dto.deletedAt.epochMillisOrNull(),
        )

    fun assignment(dto: PlanAssignmentDto, now: Long): PlanAssignmentEntity =
        PlanAssignmentEntity(
            id = dto.id,
            userId = dto.userId,
            planVersionId = dto.planVersionId,
            startLocalDate = dto.startLocalDate,
            endLocalDate = dto.endLocalDate,
            isActive = dto.isActive,
            version = dto.version,
            createdAt = dto.createdAt.epochMillis(now),
            updatedAt = dto.updatedAt.epochMillis(now),
            deletedAt = dto.deletedAt.epochMillisOrNull(),
        )

    fun plan(dto: PlanVersionDto, now: Long): ServerPlanEntities {
        val plan = TrainingPlanEntity(
            id = dto.planId,
            name = dto.planName,
            description = "",
            isBuiltIn = false,
            version = dto.version,
            createdAt = dto.createdAt.epochMillis(now),
            updatedAt = dto.updatedAt.epochMillis(now),
            deletedAt = dto.deletedAt.epochMillisOrNull(),
        )
        val version = PlanVersionEntity(
            id = dto.id,
            planId = dto.planId,
            versionNumber = dto.versionNumber,
            status = dto.status.uppercase(),
            publishedAt = dto.publishedAt.epochMillisOrNull(),
            // Keep every rule and nested prescription even when the server's legacy
            // snapshot_json field is absent or incomplete.
            snapshotJson = planSnapshotAdapter.toJson(dto),
            version = dto.version,
            createdAt = dto.createdAt.epochMillis(now),
            updatedAt = dto.updatedAt.epochMillis(now),
            deletedAt = dto.deletedAt.epochMillisOrNull(),
        )
        val days = dto.days.map { planDay(it, now) }
        val slots = dto.days.flatMap { it.slots }.map { planSlot(it, now) }
        val options = dto.days.flatMap { it.slots }.flatMap { it.options }
            .map {
                planSlotOption(
                    dto = it,
                    now = now,
                    planIntroSetCount = dto.initialSetCount ?: COMPATIBILITY_INTRO_SET_COUNT,
                    planIntroWeeks = dto.initialReducedWeeks ?: COMPATIBILITY_INTRO_WEEKS,
                )
            }
        return ServerPlanEntities(plan, version, days, slots, options)
    }

    fun planDay(dto: PlanDayDto, now: Long): PlanDayEntity = PlanDayEntity(
        id = dto.id,
        planVersionId = dto.planVersionId,
        code = enumValueOrDefault(dto.code, PlanCode.A),
        name = dto.name,
        sortOrder = dto.sortOrder,
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun planSlot(dto: PlanSlotDto, now: Long): PlanSlotEntity = PlanSlotEntity(
        id = dto.id,
        planDayId = dto.planDayId,
        position = dto.position,
        bodyPart = dto.bodyPart,
        cues = dto.cues,
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun planSlotOption(
        dto: PlanSlotOptionDto,
        now: Long,
        planIntroSetCount: Int = COMPATIBILITY_INTRO_SET_COUNT,
        planIntroWeeks: Int = COMPATIBILITY_INTRO_WEEKS,
    ): PlanSlotOptionEntity =
        PlanSlotOptionEntity(
            id = dto.id,
            planSlotId = dto.planSlotId,
            exerciseId = dto.exerciseId,
            equipmentId = dto.equipmentId,
            isPreferred = dto.isPreferred,
            sortOrder = dto.sortOrder,
            setCount = dto.setCount,
            introSetCount = minOf(dto.introSetCount ?: planIntroSetCount, dto.setCount),
            introWeeks = dto.introWeeks ?: planIntroWeeks,
            repMin = dto.durationSecondsMin ?: dto.repMin,
            repMax = dto.durationSecondsMax ?: dto.durationSecondsMin ?: dto.repMax,
            repUnit = when {
                dto.durationSecondsMin != null || dto.durationSecondsMax != null -> "seconds"
                dto.isPerSide && dto.repUnit == "reps" -> "reps_per_side"
                else -> dto.repUnit
            },
            rirMin = dto.rirMin,
            rirMax = dto.rirMax,
            version = dto.version,
            createdAt = dto.createdAt.epochMillis(now),
            updatedAt = dto.updatedAt.epochMillis(now),
            deletedAt = dto.deletedAt.epochMillisOrNull(),
        )

    fun workout(dto: WorkoutSessionDto, now: Long): ServerWorkoutEntities {
        val session = WorkoutSessionEntity(
            id = dto.id,
            userId = dto.userId,
            planVersionId = dto.planVersionId,
            planDayCode = dto.planDayCode?.let { enumValueOrNull<PlanCode>(it) },
            localDate = dto.localDate,
            timezone = dto.timezone,
            startedAt = dto.startedAt.epochMillis(now),
            completedAt = dto.completedAt.epochMillisOrNull(),
            status = enumValueOrDefault(dto.status, WorkoutStatus.IN_PROGRESS),
            isFullBody = dto.isFullBody,
            planSnapshotJson = dto.planSnapshotJson,
            idempotencyKey = dto.idempotencyKey ?: "server:${dto.id}",
            notes = dto.notes,
            version = dto.version,
            createdAt = dto.createdAt.epochMillis(now),
            updatedAt = dto.updatedAt.epochMillis(now),
            deletedAt = dto.deletedAt.epochMillisOrNull(),
        )
        return ServerWorkoutEntities(
            session = session,
            sets = dto.sets.map { workoutSet(it, now) },
        )
    }

    fun workoutSet(dto: WorkoutSetDto, now: Long): WorkoutSetEntity = WorkoutSetEntity(
        id = dto.id,
        sessionId = dto.sessionId,
        planSlotId = dto.planSlotId,
        sourcePlanSlotOptionId = dto.sourcePlanSlotOptionId,
        exerciseId = dto.exerciseId,
        equipmentId = dto.equipmentId,
        setNumber = dto.setNumber,
        weightKg = dto.weightKg,
        reps = dto.reps,
        durationSeconds = dto.durationSeconds,
        isWarmup = dto.isWarmup,
        rir = dto.rir,
        quality = dto.quality?.let { enumValueOrNull<SetQuality>(it) },
        pain = dto.pain,
        notes = dto.notes,
        completed = dto.completed,
        completedAt = dto.completedAt.epochMillisOrNull(),
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun readiness(dto: ReadinessDto, now: Long): DailyReadinessEntity = DailyReadinessEntity(
        id = dto.id,
        userId = dto.userId,
        localDate = dto.localDate,
        fatigueScore = dto.fatigueScore,
        sleepQuality = dto.sleepQuality,
        painNotes = dto.painNotes,
        notes = dto.notes,
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )

    fun cardio(dto: CardioSessionDto, now: Long): CardioSessionEntity = CardioSessionEntity(
        id = dto.id,
        userId = dto.userId,
        localDate = dto.localDate,
        activity = dto.activity.ifBlank { dto.activityType },
        durationMinutes = dto.durationMinutes.takeIf { it > 0 }
            ?: (dto.durationSeconds / 60).coerceAtLeast(1),
        distanceKm = dto.distanceKm ?: dto.distanceMeters?.div(1_000.0),
        notes = dto.notes,
        startedAt = dto.startedAt.epochMillis(now),
        completedAt = dto.completedAt.epochMillisOrNull(),
        version = dto.version,
        createdAt = dto.createdAt.epochMillis(now),
        updatedAt = dto.updatedAt.epochMillis(now),
        deletedAt = dto.deletedAt.epochMillisOrNull(),
    )
}

internal data class ServerPlanEntities(
    val plan: TrainingPlanEntity,
    val version: PlanVersionEntity,
    val days: List<PlanDayEntity>,
    val slots: List<PlanSlotEntity>,
    val options: List<PlanSlotOptionEntity>,
)

internal data class ServerWorkoutEntities(
    val session: WorkoutSessionEntity,
    val sets: List<WorkoutSetEntity>,
)

internal fun String?.epochMillis(fallback: Long): Long = epochMillisOrNull() ?: fallback

internal fun String?.epochMillisOrNull(): Long? = this?.let { value ->
    runCatching { Instant.parse(value).toEpochMilli() }.getOrNull()
}

private inline fun <reified T : Enum<T>> enumValueOrNull(value: String): T? =
    enumValues<T>().firstOrNull { it.name.equals(value, ignoreCase = true) }

private inline fun <reified T : Enum<T>> enumValueOrDefault(value: String, default: T): T =
    enumValueOrNull<T>(value) ?: default
