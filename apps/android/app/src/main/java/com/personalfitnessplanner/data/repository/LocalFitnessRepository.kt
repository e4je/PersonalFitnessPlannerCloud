package com.personalfitnessplanner.data.repository

import androidx.room.withTransaction
import com.personalfitnessplanner.data.defaultplan.DefaultTrainingPlan
import com.personalfitnessplanner.data.local.AppDatabase
import com.personalfitnessplanner.data.local.ExerciseEntity
import com.personalfitnessplanner.data.local.OutboxStatus
import com.personalfitnessplanner.data.local.PlanAssignmentEntity
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.PlanDayWithSlots
import com.personalfitnessplanner.data.local.PlanVersionWithDays
import com.personalfitnessplanner.data.local.SetQuality
import com.personalfitnessplanner.data.local.SyncOperation
import com.personalfitnessplanner.data.local.SyncOutboxEntity
import com.personalfitnessplanner.data.local.UnitSystem
import com.personalfitnessplanner.data.local.UserEntity
import com.personalfitnessplanner.data.local.WorkoutSessionEntity
import com.personalfitnessplanner.data.local.WorkoutSessionWithSets
import com.personalfitnessplanner.data.local.WorkoutSetEntity
import com.personalfitnessplanner.data.local.WorkoutStatus
import com.personalfitnessplanner.sync.IdempotencyKeys
import com.personalfitnessplanner.domain.PlanLifecycleRules
import com.squareup.moshi.JsonAdapter
import com.squareup.moshi.Moshi
import com.squareup.moshi.Types
import java.nio.charset.StandardCharsets
import java.time.Clock
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flatMapLatest

data class LocalUserProfile(
    val email: String = "local@personal-fitness.invalid",
    val displayName: String = "本地用户",
    val timezone: String = LocalFitnessRepository.DEFAULT_TIMEZONE,
    val weightUnit: UnitSystem = UnitSystem.KG,
)

data class WorkoutSetInput(
    val weightKg: Double? = null,
    val reps: Int? = null,
    val durationSeconds: Int? = null,
    val isWarmup: Boolean = false,
    val rir: Int? = null,
    val quality: SetQuality? = null,
    val pain: Boolean = false,
    val notes: String? = null,
)

/**
 * Offline-first mutation API consumed by ViewModels. Every durable workout mutation and its
 * outbox operation are committed in the same Room transaction.
 */
class LocalFitnessRepository(
    private val database: AppDatabase,
    private val clock: Clock = Clock.systemUTC(),
    private val idFactory: () -> String = { UUID.randomUUID().toString() },
    moshi: Moshi = defaultRepositoryMoshi(),
) {
    private val payloadAdapter: JsonAdapter<Map<String, Any?>> = moshi.adapter(
        Types.newParameterizedType(
            Map::class.java,
            String::class.java,
            Any::class.java,
        ),
    )

    /** Creates the local user, immutable built-in plan, and an active assignment when absent. */
    suspend fun initialize(profile: LocalUserProfile = LocalUserProfile()): UserEntity {
        val zone = requireValidZone(profile.timezone)
        val now = clock.millis()
        return database.withTransaction {
            val existingUser = database.userDao().getCurrent(LOCAL_USER_ID)
            val user = existingUser ?: UserEntity(
                id = LOCAL_USER_ID,
                email = profile.email,
                displayName = profile.displayName,
                timezone = zone.id,
                weightUnit = profile.weightUnit,
                createdAt = now,
                updatedAt = now,
            ).also { database.userDao().upsert(it) }

            if (database.planDao().getVersionWithDays(DefaultTrainingPlan.VERSION_ID) == null) {
                val seed = DefaultTrainingPlan.create(now)
                database.catalogDao().upsertEquipment(seed.equipment)
                database.catalogDao().upsertExercises(seed.exercises)
                database.catalogDao().upsertAlternatives(seed.alternatives)
                database.planDao().replaceDefaultPlan(seed)
            }

            val activeAssignment = database.planDao().activeAssignment(user.id)
            val replaceLegacyBuiltIn = activeAssignment != null &&
                activeAssignment.planVersionId != DefaultTrainingPlan.VERSION_ID &&
                database.planDao().isBuiltInVersion(activeAssignment.planVersionId) == true
            if (activeAssignment == null || replaceLegacyBuiltIn) {
                val canonicalAssignment = PlanAssignmentEntity(
                    id = stableId("local-assignment:${user.id}:${DefaultTrainingPlan.VERSION_ID}"),
                    userId = user.id,
                    planVersionId = DefaultTrainingPlan.VERSION_ID,
                    startLocalDate = activeAssignment?.startLocalDate
                        ?: LocalDate.now(clock.withZone(zone)).toString(),
                    isActive = true,
                    version = (activeAssignment?.version ?: 0L) + 1L,
                    createdAt = now,
                    updatedAt = now,
                )
                database.planDao().upsertAssignment(canonicalAssignment)
                database.planDao().deactivateOtherAssignments(
                    userId = user.id,
                    activeAssignmentId = canonicalAssignment.id,
                    updatedAt = now,
                )
            }
            user
        }
    }

    fun observeExercises(): Flow<List<ExerciseEntity>> = database.catalogDao().observeExercises()

    fun observeWorkoutHistory(
        userId: String? = null,
    ): Flow<List<WorkoutSessionEntity>> = if (userId != null) {
        database.workoutDao().observeSessions(userId)
    } else {
        database.userDao().observeCurrent(LOCAL_USER_ID).flatMapLatest { user ->
            database.workoutDao().observeSessions(user?.id ?: LOCAL_USER_ID)
        }
    }

    fun observePendingSyncCount(): Flow<Int> = database.syncDao().allPendingCount()

    suspend fun currentUserId(): String =
        database.userDao().getCurrent(LOCAL_USER_ID)?.id ?: LOCAL_USER_ID

    suspend fun currentPlan(userId: String? = null): PlanVersionWithDays? =
        database.planDao().currentPlanForUser(userId ?: currentUserId())

    suspend fun activeWorkout(userId: String? = null): WorkoutSessionWithSets? =
        database.workoutDao().activeSessionForUser(userId ?: currentUserId())?.sorted(payloadAdapter)

    suspend fun getWorkout(sessionId: String): WorkoutSessionWithSets? =
        database.workoutDao().getSessionWithSets(sessionId)?.sorted(payloadAdapter)

    /** Returns the existing in-progress workout before consulting the latest plan assignment. */
    suspend fun startOrResumeWorkout(
        userId: String? = null,
        requestedDay: PlanCode? = null,
        localDate: LocalDate = LocalDate.now(clock),
        timezone: String = DEFAULT_TIMEZONE,
        exerciseSelections: Map<String, String> = emptyMap(),
        skippedSlotIds: Set<String> = emptySet(),
    ): WorkoutSessionWithSets {
        requireValidZone(timezone)
        val now = clock.millis()
        val effectiveUserId = userId ?: currentUserId()
        return database.withTransaction {
            database.workoutDao().activeSessionForUser(effectiveUserId)?.let {
                return@withTransaction it.sorted(payloadAdapter)
            }

            val plan = checkNotNull(database.planDao().currentPlanForUser(effectiveUserId)) {
                "No active training plan for user $effectiveUserId; call initialize first"
            }
            val assignment = database.planDao().activeAssignment(effectiveUserId)
            val latest = database.workoutDao().latestCompletedSession(effectiveUserId)
            val dayCode = requestedDay ?: when (latest?.planDayCode) {
                PlanCode.A -> PlanCode.B
                PlanCode.B -> PlanCode.A
                null -> PlanCode.A
            }
            val day = checkNotNull(plan.days.firstOrNull { it.day.code == dayCode }) {
                "Plan ${plan.planVersion.id} has no $dayCode day"
            }
            val activeSlots = day.slots.filter { it.slot.deletedAt == null }
            val activeSlotIds = activeSlots.map { it.slot.id }.toSet()
            require(exerciseSelections.keys.all { it in activeSlotIds }) {
                "Exercise selections contain a slot outside the active $dayCode plan day"
            }
            require(skippedSlotIds.all { it in activeSlotIds }) {
                "Skipped slots contain a slot outside the active $dayCode plan day"
            }
            require(exerciseSelections.keys.intersect(skippedSlotIds).isEmpty()) {
                "A plan slot cannot be both selected and skipped"
            }
            val selected = activeSlots
                .filterNot { it.slot.id in skippedSlotIds }
                .sortedBy { it.slot.position }
                .map { slot ->
                val requestedExercise = exerciseSelections[slot.slot.id]
                val option = if (requestedExercise != null) {
                    requireNotNull(
                        database.planDao().optionForSlotAndExercise(slot.slot.id, requestedExercise),
                    ) {
                        "Exercise $requestedExercise is not an active option for plan slot ${slot.slot.id}"
                    }
                } else {
                    checkNotNull(
                        slot.options.filter { it.deletedAt == null }
                            .sortedWith(compareByDescending<com.personalfitnessplanner.data.local.PlanSlotOptionEntity> {
                                it.isPreferred
                            }.thenBy { it.sortOrder })
                            .firstOrNull(),
                    ) { "Plan slot ${slot.slot.id} has no exercise option" }
                }
                slot to option
            }
            require(selected.isNotEmpty()) { "At least one plan slot is required to start a workout" }
            val snapshot = planSnapshot(plan, day, selected, payloadAdapter)
            val sessionId = idFactory()
            val session = WorkoutSessionEntity(
                id = sessionId,
                userId = effectiveUserId,
                planVersionId = plan.planVersion.id,
                planDayCode = dayCode,
                localDate = localDate.toString(),
                timezone = timezone,
                startedAt = now,
                completedAt = null,
                status = WorkoutStatus.IN_PROGRESS,
                isFullBody = true,
                planSnapshotJson = snapshot,
                idempotencyKey = IdempotencyKeys.forOperation(WORKOUT_TYPE, sessionId, "create"),
                createdAt = now,
                updatedAt = now,
            )
            val sets = selected.flatMap { (slot, option) ->
                val setCount = prescribedSetCount(option.introSetCount, option.introWeeks, option.setCount, assignment, localDate)
                (1..setCount).map { setNumber ->
                    WorkoutSetEntity(
                        id = idFactory(),
                        sessionId = sessionId,
                        planSlotId = slot.slot.id,
                        sourcePlanSlotOptionId = option.id,
                        exerciseId = option.exerciseId,
                        equipmentId = option.equipmentId,
                        setNumber = setNumber,
                        weightKg = null,
                        reps = null,
                        durationSeconds = null,
                        isWarmup = false,
                        rir = null,
                        quality = null,
                        pain = false,
                        completed = false,
                        completedAt = null,
                        createdAt = now,
                        updatedAt = now,
                    )
                }
            }
            database.workoutDao().insertSessionWithSets(session, sets)
            enqueueSessionMutation(session, sets, SyncOperation.UPSERT, now)
            WorkoutSessionWithSets(session, sets).sorted(payloadAdapter)
        }
    }

    /** Auto-saves a set draft. Identical repeated UI events are a no-op and create no outbox row. */
    suspend fun saveSet(
        sessionId: String,
        setId: String,
        input: WorkoutSetInput,
        markCompleted: Boolean = false,
    ): WorkoutSessionWithSets {
        validate(input)
        val now = clock.millis()
        return database.withTransaction {
            val workout = requireWorkout(sessionId)
            check(workout.session.status != WorkoutStatus.DELETED) { "Workout $sessionId is deleted" }
            val original = checkNotNull(workout.sets.firstOrNull { it.id == setId }) {
                "Set $setId does not belong to workout $sessionId"
            }
            val completed = original.completed || markCompleted
            val candidate = original.copy(
                weightKg = input.weightKg,
                reps = input.reps,
                durationSeconds = input.durationSeconds,
                isWarmup = input.isWarmup,
                rir = input.rir,
                quality = input.quality,
                pain = input.pain,
                notes = input.notes?.trim()?.takeIf(String::isNotEmpty),
                completed = completed,
                completedAt = if (completed) original.completedAt ?: now else null,
            )
            if (candidate == original) return@withTransaction workout.sorted(payloadAdapter)

            val updatedSet = candidate.copy(
                version = original.version + 1,
                updatedAt = now,
            )
            val updatedSession = workout.session.copy(
                version = workout.session.version + 1,
                updatedAt = now,
            )
            val sets = workout.sets.map { if (it.id == setId) updatedSet else it }
            database.workoutDao().updateSet(updatedSet)
            database.workoutDao().upsertSession(updatedSession)
            enqueueSessionMutation(updatedSession, sets, SyncOperation.UPSERT, now)
            WorkoutSessionWithSets(updatedSession, sets).sorted(payloadAdapter)
        }
    }

    suspend fun completeSet(
        sessionId: String,
        setId: String,
        input: WorkoutSetInput,
    ): WorkoutSessionWithSets = saveSet(sessionId, setId, input, markCompleted = true)

    /**
     * Replaces the selected exercise for one plan slot in an in-progress workout.
     *
     * A slot with any completed set is deliberately rejected so one slot cannot become a mixed
     * old/new exercise record. Selecting the option already used by every set is an idempotent
     * no-op. Exercise-specific draft values are never carried to the replacement exercise.
     */
    suspend fun swapExercise(
        sessionId: String,
        planSlotId: String,
        exerciseId: String,
    ): WorkoutSessionWithSets = database.withTransaction {
        val workout = requireWorkout(sessionId)
        check(workout.session.status == WorkoutStatus.IN_PROGRESS) {
            "Exercise can only be changed in an in-progress workout"
        }
        val slotSets = workout.sets.filter {
            it.planSlotId == planSlotId && it.deletedAt == null
        }
        require(slotSets.isNotEmpty()) {
            "Plan slot $planSlotId does not belong to workout $sessionId"
        }
        val option = requireNotNull(
            database.planDao().optionForSlotAndExercise(planSlotId, exerciseId),
        ) {
            "Exercise $exerciseId is not an active option for plan slot $planSlotId"
        }
        check(slotSets.none { it.completed }) {
            "Plan slot $planSlotId already has a completed set and cannot change exercise"
        }
        if (slotSets.all {
                it.exerciseId == option.exerciseId &&
                    it.equipmentId == option.equipmentId &&
                    it.sourcePlanSlotOptionId == option.id
            }
        ) {
            return@withTransaction workout.sorted(payloadAdapter)
        }

        val enqueueAt = clock.millis()
        val updatedSlotSets = slotSets.map { set ->
            set.copy(
                sourcePlanSlotOptionId = option.id,
                exerciseId = option.exerciseId,
                equipmentId = option.equipmentId,
                weightKg = null,
                reps = null,
                durationSeconds = null,
                isWarmup = false,
                rir = null,
                quality = null,
                pain = false,
                notes = null,
                completed = false,
                completedAt = null,
                version = set.version + 1,
                updatedAt = nextTimestamp(set.updatedAt, enqueueAt),
            )
        }
        val replacements = updatedSlotSets.associateBy(WorkoutSetEntity::id)
        val allSets = workout.sets.map { replacements[it.id] ?: it }
        val updatedSession = workout.session.copy(
            planSnapshotJson = replaceSnapshotSelection(
                workout.session.planSnapshotJson,
                option,
            ),
            version = workout.session.version + 1,
            updatedAt = nextTimestamp(workout.session.updatedAt, enqueueAt),
        )
        database.workoutDao().upsertSets(updatedSlotSets)
        database.workoutDao().upsertSession(updatedSession)
        enqueueSessionMutation(updatedSession, allSets, SyncOperation.UPSERT, enqueueAt)
        WorkoutSessionWithSets(updatedSession, allSets).sorted(payloadAdapter)
    }

    suspend fun finishWorkout(sessionId: String): WorkoutSessionWithSets =
        transitionWorkout(sessionId, WorkoutStatus.COMPLETED)

    suspend fun endWorkoutEarly(sessionId: String): WorkoutSessionWithSets =
        transitionWorkout(sessionId, WorkoutStatus.ENDED_EARLY)

    suspend fun softDeleteWorkout(sessionId: String) {
        val now = clock.millis()
        database.withTransaction {
            val workout = database.workoutDao().getSessionWithSets(sessionId) ?: return@withTransaction
            val deleted = workout.session.copy(
                status = WorkoutStatus.DELETED,
                version = workout.session.version + 1,
                updatedAt = now,
                deletedAt = now,
            )
            database.workoutDao().upsertSession(deleted)
            enqueueSessionMutation(deleted, workout.sets, SyncOperation.DELETE, now)
        }
    }

    /** History is keyed by the exact exercise UUID; alternatives never inherit each other's loads. */
    suspend fun weightHistory(
        userId: String? = null,
        exerciseId: String,
        limit: Int = 20,
    ): List<WorkoutSetEntity> {
        require(limit > 0) { "limit must be positive" }
        return database.workoutDao().weightHistoryForExercise(userId ?: currentUserId(), exerciseId, limit)
    }

    suspend fun latestWorkingSet(
        userId: String? = null,
        exerciseId: String,
    ): WorkoutSetEntity? = database.workoutDao().latestCompletedWorkingSet(
        userId ?: currentUserId(),
        exerciseId,
    )

    private suspend fun transitionWorkout(
        sessionId: String,
        target: WorkoutStatus,
    ): WorkoutSessionWithSets {
        require(target == WorkoutStatus.COMPLETED || target == WorkoutStatus.ENDED_EARLY)
        val now = clock.millis()
        return database.withTransaction {
            val workout = requireWorkout(sessionId)
            if (workout.session.status == target) return@withTransaction workout.sorted(payloadAdapter)
            check(workout.session.status == WorkoutStatus.IN_PROGRESS) {
                "Workout $sessionId is already ${workout.session.status}"
            }
            val updated = workout.session.copy(
                status = target,
                completedAt = now,
                version = workout.session.version + 1,
                updatedAt = now,
            )
            database.workoutDao().upsertSession(updated)
            enqueueSessionMutation(updated, workout.sets, SyncOperation.UPSERT, now)
            WorkoutSessionWithSets(updated, workout.sets).sorted(payloadAdapter)
        }
    }

    private suspend fun requireWorkout(sessionId: String): WorkoutSessionWithSets =
        checkNotNull(database.workoutDao().getSessionWithSets(sessionId)) {
            "Workout $sessionId was not found"
        }

    private suspend fun enqueueSessionMutation(
        session: WorkoutSessionEntity,
        sets: List<WorkoutSetEntity>,
        operation: SyncOperation,
        now: Long,
    ) {
        val mutationId = "${operation.name.lowercase()}-v${session.version}"
        val idempotencyKey = IdempotencyKeys.forOperation(WORKOUT_TYPE, session.id, mutationId)
        val payload = if (operation == SyncOperation.DELETE) {
            linkedMapOf<String, Any?>(
                "id" to session.id,
                "version" to session.version,
                "created_at" to session.createdAt.toIsoInstant(),
                "updated_at" to session.updatedAt.toIsoInstant(),
                "deleted_at" to session.deletedAt?.toIsoInstant(),
            )
        } else {
            sessionPayload(session, sets)
        }
        database.syncDao().enqueue(
            SyncOutboxEntity(
                id = stableId("outbox:$idempotencyKey"),
                aggregateType = WORKOUT_TYPE,
                aggregateId = session.id,
                operation = operation,
                payloadJson = payloadAdapter.toJson(payload),
                idempotencyKey = idempotencyKey,
                status = OutboxStatus.PENDING,
                attemptCount = 0,
                nextAttemptAt = now,
                createdAt = now,
                updatedAt = now,
            ),
        )
    }

    private fun sessionPayload(
        session: WorkoutSessionEntity,
        sets: List<WorkoutSetEntity>,
    ): Map<String, Any?> = linkedMapOf(
        "id" to session.id,
        "plan_version_id" to session.planVersionId,
        "plan_day_code" to session.planDayCode?.name,
        "local_date" to session.localDate,
        "timezone" to session.timezone,
        "started_at" to session.startedAt.toIsoInstant(),
        "completed_at" to session.completedAt?.toIsoInstant(),
        "status" to session.status.name,
        "is_full_body" to session.isFullBody,
        "plan_snapshot_json" to session.planSnapshotJson,
        "notes" to session.notes,
        "version" to session.version,
        "created_at" to session.createdAt.toIsoInstant(),
        "updated_at" to session.updatedAt.toIsoInstant(),
        "sets" to sets.sortedWith(compareBy<WorkoutSetEntity>({ it.planSlotId }, { it.setNumber })).map { set ->
            linkedMapOf<String, Any?>(
                "id" to set.id,
                "plan_slot_id" to set.planSlotId,
                "source_plan_slot_option_id" to set.sourcePlanSlotOptionId,
                "exercise_id" to set.exerciseId,
                "equipment_id" to set.equipmentId,
                "set_number" to set.setNumber,
                "weight_kg" to set.weightKg,
                "reps" to set.reps,
                "duration_seconds" to set.durationSeconds,
                "is_warmup" to set.isWarmup,
                "rir" to set.rir,
                "quality" to set.quality?.name,
                "pain" to set.pain,
                "notes" to set.notes,
                "completed" to set.completed,
                "completed_at" to set.completedAt?.toIsoInstant(),
                "version" to set.version,
                "created_at" to set.createdAt.toIsoInstant(),
                "updated_at" to set.updatedAt.toIsoInstant(),
                "deleted_at" to set.deletedAt?.toIsoInstant(),
            )
        },
        "deleted_at" to session.deletedAt?.toIsoInstant(),
    )

    private fun replaceSnapshotSelection(
        snapshotJson: String,
        option: com.personalfitnessplanner.data.local.PlanSlotOptionEntity,
    ): String {
        val snapshot = runCatching { payloadAdapter.fromJson(snapshotJson) }
            .getOrNull()
            ?.toMutableMap()
            ?: return snapshotJson
        val slots = snapshot["slots"] as? List<*> ?: return snapshotJson
        var replaced = false
        snapshot["slots"] = slots.map { rawSlot ->
            val rawMap = rawSlot as? Map<*, *> ?: return@map rawSlot
            if (rawMap["slot_id"] != option.planSlotId) return@map rawSlot
            replaced = true
            linkedMapOf<String, Any?>().apply {
                rawMap.forEach { (key, value) -> if (key is String) put(key, value) }
                put("option_id", option.id)
                put("exercise_id", option.exerciseId)
                put("equipment_id", option.equipmentId)
                put("set_count", option.setCount)
                put("intro_set_count", option.introSetCount)
                put("rep_min", option.repMin)
                put("rep_max", option.repMax)
                put("rep_unit", option.repUnit)
                put("rir_min", option.rirMin)
                put("rir_max", option.rirMax)
            }
        }
        return if (replaced) payloadAdapter.toJson(snapshot) else snapshotJson
    }

    private fun prescribedSetCount(
        introSetCount: Int,
        introWeeks: Int,
        fullSetCount: Int,
        assignment: PlanAssignmentEntity?,
        localDate: LocalDate,
    ): Int {
        if (assignment == null || introWeeks <= 0) return fullSetCount.coerceAtLeast(1)
        val start = runCatching { LocalDate.parse(assignment.startLocalDate) }.getOrNull()
            ?: return fullSetCount.coerceAtLeast(1)
        val days = ChronoUnit.DAYS.between(start, localDate)
        val trainingWeek = if (days >= 0) (days / 7L + 1L).toInt() else 0
        return PlanLifecycleRules.effectiveSetCount(
            trainingWeek = trainingWeek,
            prescribedSets = fullSetCount.coerceAtLeast(1),
            adaptationWeeks = introWeeks,
            adaptationSets = introSetCount.coerceAtLeast(1),
        )
    }

    private fun validate(input: WorkoutSetInput) {
        require(input.weightKg == null || input.weightKg >= 0.0) { "weightKg cannot be negative" }
        require(input.reps == null || input.reps >= 0) { "reps cannot be negative" }
        require(input.durationSeconds == null || input.durationSeconds >= 0) {
            "durationSeconds cannot be negative"
        }
        require(input.rir == null || input.rir in 0..10) { "rir must be in 0..10" }
    }

    private fun requireValidZone(timezone: String): ZoneId = try {
        ZoneId.of(timezone)
    } catch (error: Exception) {
        throw IllegalArgumentException("Invalid IANA timezone: $timezone", error)
    }

    companion object {
        const val LOCAL_USER_ID = "00000000-0000-4000-8000-000000000001"
        const val DEFAULT_TIMEZONE = "Asia/Shanghai"
        private const val WORKOUT_TYPE = "workout_session"
    }
}

private fun planSnapshot(
    plan: PlanVersionWithDays,
    day: PlanDayWithSlots,
    selected: List<Pair<com.personalfitnessplanner.data.local.PlanSlotWithOptions, com.personalfitnessplanner.data.local.PlanSlotOptionEntity>>,
    adapter: JsonAdapter<Map<String, Any?>>,
): String = adapter.toJson(
    linkedMapOf(
        "plan_id" to plan.planVersion.planId,
        "plan_version_id" to plan.planVersion.id,
        "version_number" to plan.planVersion.versionNumber,
        "day_code" to day.day.code.name,
        "day_name" to day.day.name,
        "slots" to selected.map { (slot, option) ->
            linkedMapOf<String, Any?>(
                "slot_id" to slot.slot.id,
                "position" to slot.slot.position,
                "body_part" to slot.slot.bodyPart,
                "cues" to slot.slot.cues,
                "option_id" to option.id,
                "exercise_id" to option.exerciseId,
                "equipment_id" to option.equipmentId,
                "set_count" to option.setCount,
                "intro_set_count" to option.introSetCount,
                "rep_min" to option.repMin,
                "rep_max" to option.repMax,
                "rep_unit" to option.repUnit,
                "rir_min" to option.rirMin,
                "rir_max" to option.rirMax,
            )
        },
    ),
)

/**
 * Room does not guarantee the order of a @Relation collection. The immutable session snapshot
 * stores the plan slot position, so it remains the source of truth even after a newer plan is
 * assigned or the original plan is no longer available locally.
 */
private fun WorkoutSessionWithSets.sorted(
    adapter: JsonAdapter<Map<String, Any?>>,
): WorkoutSessionWithSets {
    val slotPositions = runCatching {
        val snapshot = adapter.fromJson(session.planSnapshotJson).orEmpty()
        (snapshot["slots"] as? List<*>)
            .orEmpty()
            .mapNotNull { rawSlot ->
                val slot = rawSlot as? Map<*, *> ?: return@mapNotNull null
                val id = slot["slot_id"] as? String ?: return@mapNotNull null
                val position = (slot["position"] as? Number)?.toInt() ?: return@mapNotNull null
                id to position
            }
            .toMap()
    }.getOrDefault(emptyMap())
    return copy(
        sets = sets.sortedWith(
            compareBy<WorkoutSetEntity>(
                { slotPositions[it.planSlotId] ?: Int.MAX_VALUE },
                { it.setNumber },
                { it.id },
            ),
        ),
    )
}

private fun Long.toIsoInstant(): String = Instant.ofEpochMilli(this).toString()

private fun nextTimestamp(previous: Long, wallClock: Long): Long =
    maxOf(wallClock, if (previous == Long.MAX_VALUE) Long.MAX_VALUE else previous + 1L)

private fun stableId(value: String): String = UUID.nameUUIDFromBytes(
    value.toByteArray(StandardCharsets.UTF_8),
).toString()
