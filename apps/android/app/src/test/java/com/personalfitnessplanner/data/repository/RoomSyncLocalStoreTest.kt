package com.personalfitnessplanner.data.repository

import androidx.room.Room
import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.defaultplan.DefaultTrainingPlan
import com.personalfitnessplanner.data.local.AppDatabase
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.SyncOperation
import com.personalfitnessplanner.data.local.SyncOutboxEntity
import com.personalfitnessplanner.data.local.WorkoutSessionEntity
import com.personalfitnessplanner.data.local.WorkoutStatus
import com.personalfitnessplanner.data.remote.BootstrapDto
import com.personalfitnessplanner.data.remote.CardioSessionDto
import com.personalfitnessplanner.data.remote.PlanAssignmentDto
import com.personalfitnessplanner.data.remote.PlanVersionDto
import com.personalfitnessplanner.data.remote.ReadinessDto
import com.personalfitnessplanner.data.remote.SyncChangeDto
import com.personalfitnessplanner.data.remote.SyncChangesDto
import com.personalfitnessplanner.data.remote.UserDto
import com.personalfitnessplanner.data.remote.WorkoutSessionDto
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.flow.first
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class RoomSyncLocalStoreTest {
    private val clock = Clock.fixed(Instant.parse(NOW), ZoneOffset.UTC)
    private lateinit var database: AppDatabase
    private lateinit var store: RoomSyncLocalStore

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            RuntimeEnvironment.getApplication(),
            AppDatabase::class.java,
        ).allowMainThreadQueries().build()
        store = RoomSyncLocalStore(database, clock = clock)
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun bootstrapPreservesEveryAssignedPlanVersionAndActivatesCurrentPlanForServerUser() = runBlocking {
        LocalFitnessRepository(database, clock = clock).initialize()
        val canonical = DefaultTrainingPlan.create(clock.millis())
        val builtInAssignment = checkNotNull(
            database.planDao().activeAssignment(LocalFitnessRepository.LOCAL_USER_ID),
        )
        val staleEquipment = canonical.equipment.first().copy(id = STALE_REMOTE_EQUIPMENT_ID)
        val staleExercises = canonical.exercises.take(2).mapIndexed { index, exercise ->
            exercise.copy(
                id = "$STALE_REMOTE_EXERCISE_ID-$index",
                equipmentId = if (index == 0) staleEquipment.id else null,
            )
        }
        database.catalogDao().upsertEquipment(listOf(staleEquipment))
        database.catalogDao().upsertExercises(staleExercises)
        database.catalogDao().upsertAlternatives(
            listOf(
                canonical.alternatives.first().copy(
                    id = STALE_REMOTE_ALTERNATIVE_ID,
                    exerciseId = staleExercises[0].id,
                    alternativeExerciseId = staleExercises[1].id,
                ),
            ),
        )
        val stalePlan = canonical.plan.copy(id = STALE_REMOTE_PLAN_ID, isBuiltIn = false)
        val staleVersion = canonical.planVersion.copy(
            id = STALE_REMOTE_PLAN_VERSION_ID,
            planId = stalePlan.id,
        )
        val staleDay = canonical.days.first().copy(
            id = STALE_REMOTE_PLAN_DAY_ID,
            planVersionId = staleVersion.id,
        )
        val staleSlot = canonical.slots.first().copy(
            id = STALE_REMOTE_PLAN_SLOT_ID,
            planDayId = staleDay.id,
        )
        val staleOption = canonical.options.first().copy(
            id = STALE_REMOTE_PLAN_OPTION_ID,
            planSlotId = staleSlot.id,
            exerciseId = staleExercises[0].id,
            equipmentId = staleEquipment.id,
        )
        database.planDao().upsertPlans(listOf(stalePlan))
        database.planDao().upsertVersions(listOf(staleVersion))
        database.planDao().upsertDays(listOf(staleDay))
        database.planDao().upsertSlots(listOf(staleSlot))
        database.planDao().upsertOptions(listOf(staleOption))
        database.workoutDao().upsertSession(
            localWorkout(version = 1, notes = "stale-server-cache").copy(
                id = STALE_REMOTE_WORKOUT_ID,
                userId = REMOTE_USER_ID,
                idempotencyKey = "stale-server-cache",
            ),
        )
        database.workoutDao().upsertSession(
            localWorkout(version = 1, notes = "pending-local-change").copy(
                id = PENDING_REMOTE_WORKOUT_ID,
                userId = REMOTE_USER_ID,
                idempotencyKey = "pending-local-change",
            ),
        )
        database.syncDao().enqueue(
            SyncOutboxEntity(
                id = "pending-workout-outbox",
                aggregateType = "workout_session",
                aggregateId = PENDING_REMOTE_WORKOUT_ID,
                operation = SyncOperation.UPSERT,
                payloadJson = "{}",
                idempotencyKey = "pending-workout-key",
                nextAttemptAt = clock.millis(),
                createdAt = clock.millis(),
                updatedAt = clock.millis(),
            ),
        )
        val currentRemotePlan = PlanVersionDto(
            id = REMOTE_PLAN_VERSION_ID,
            planId = "remote-plan",
            planName = "Remote plan",
            versionNumber = 2,
            version = 6,
        )
        val historicalRemotePlan = PlanVersionDto(
            id = HISTORICAL_PLAN_VERSION_ID,
            planId = "remote-plan",
            planName = "Remote plan",
            versionNumber = 1,
            status = "archived",
            version = 4,
        )

        store.replaceServerOwnedData(
            BootstrapDto(
                user = UserDto(
                    id = REMOTE_USER_ID,
                    email = "athlete@example.com",
                    displayName = "Athlete",
                    timezone = "Asia/Shanghai",
                    version = 3,
                ),
                currentPlan = currentRemotePlan,
                planVersions = listOf(historicalRemotePlan, currentRemotePlan),
                assignments = listOf(
                    PlanAssignmentDto(
                        id = HISTORICAL_ASSIGNMENT_ID,
                        userId = REMOTE_USER_ID,
                        planVersionId = HISTORICAL_PLAN_VERSION_ID,
                        startLocalDate = "2026-07-01",
                        endLocalDate = "2026-08-08",
                        isActive = false,
                        version = 3,
                    ),
                    PlanAssignmentDto(
                        id = REMOTE_ASSIGNMENT_ID,
                        userId = REMOTE_USER_ID,
                        planVersionId = REMOTE_PLAN_VERSION_ID,
                        startLocalDate = LOCAL_DATE,
                        isActive = true,
                        version = 2,
                    ),
                ),
                workoutSessions = listOf(
                    WorkoutSessionDto(
                        id = REMOTE_WORKOUT_ID,
                        userId = REMOTE_USER_ID,
                        planVersionId = REMOTE_PLAN_VERSION_ID,
                        planDayCode = "A",
                        localDate = LOCAL_DATE,
                        timezone = "Asia/Shanghai",
                        startedAt = NOW,
                        completedAt = "2026-08-09T01:00:00Z",
                        status = "COMPLETED",
                        version = 4,
                    ),
                ),
                readiness = listOf(
                    ReadinessDto(
                        id = REMOTE_READINESS_ID,
                        userId = REMOTE_USER_ID,
                        localDate = LOCAL_DATE,
                        fatigueScore = 8,
                        version = 4,
                    ),
                ),
                cardioSessions = listOf(
                    CardioSessionDto(
                        id = REMOTE_CARDIO_ID,
                        userId = REMOTE_USER_ID,
                        localDate = LOCAL_DATE,
                        activity = "快走",
                        activityType = "walking",
                        durationMinutes = 30,
                        durationSeconds = 1_800,
                        distanceMeters = 3_000.0,
                        startedAt = NOW,
                        completedAt = "2026-08-09T00:30:00Z",
                        version = 2,
                    ),
                ),
                syncCursor = "bootstrap-cursor",
            ),
        )

        val localUser = database.userDao().getById(LocalFitnessRepository.LOCAL_USER_ID)
        assertThat(localUser?.email).isEqualTo("local@personal-fitness.invalid")
        assertThat(database.userDao().getById(REMOTE_USER_ID)?.email).isEqualTo("athlete@example.com")

        val activeAssignment = database.planDao().activeAssignment(REMOTE_USER_ID)
        assertThat(activeAssignment?.id).isEqualTo(REMOTE_ASSIGNMENT_ID)
        assertThat(activeAssignment?.userId).isEqualTo(REMOTE_USER_ID)
        assertThat(activeAssignment?.planVersionId).isEqualTo(REMOTE_PLAN_VERSION_ID)
        assertThat(database.planDao().currentPlanForUser(REMOTE_USER_ID)?.planVersion?.id)
            .isEqualTo(REMOTE_PLAN_VERSION_ID)
        assertThat(rowExists("plan_versions", HISTORICAL_PLAN_VERSION_ID)).isTrue()
        assertThat(rowExists("plan_assignments", HISTORICAL_ASSIGNMENT_ID)).isTrue()

        database.openHelper.readableDatabase.query(
            "SELECT user_id, is_active FROM plan_assignments WHERE id = ?",
            arrayOf(builtInAssignment.id),
        ).use { cursor ->
            assertThat(cursor.moveToFirst()).isTrue()
            assertThat(cursor.getString(0)).isEqualTo(LocalFitnessRepository.LOCAL_USER_ID)
            assertThat(cursor.getInt(1)).isEqualTo(1)
        }

        val workout = database.workoutDao().getSessionWithSets(REMOTE_WORKOUT_ID)?.session
        assertThat(workout?.userId).isEqualTo(REMOTE_USER_ID)
        assertThat(database.workoutDao().getSessionWithSets(STALE_REMOTE_WORKOUT_ID)).isNull()
        assertThat(database.workoutDao().getSessionWithSets(PENDING_REMOTE_WORKOUT_ID)?.session?.notes)
            .isEqualTo("pending-local-change")
        assertThat(database.syncDao().allPendingCount().first()).isEqualTo(1)
        val readiness = database.readinessDao().forDate(REMOTE_USER_ID, LOCAL_DATE)
        assertThat(readiness?.id).isEqualTo(REMOTE_READINESS_ID)
        assertThat(readiness?.userId).isEqualTo(REMOTE_USER_ID)
        assertThat(database.readinessDao().forDate(LocalFitnessRepository.LOCAL_USER_ID, LOCAL_DATE)).isNull()
        val cardio = database.cardioDao().observeSessions(REMOTE_USER_ID).first().single()
        assertThat(cardio.id).isEqualTo(REMOTE_CARDIO_ID)
        assertThat(cardio.userId).isEqualTo(REMOTE_USER_ID)
        assertThat(cardio.distanceKm).isEqualTo(3.0)
        assertThat(LocalFitnessRepository(database, clock = clock).currentUserId()).isEqualTo(REMOTE_USER_ID)
        assertThat(store.readCursor()).isEqualTo("bootstrap-cursor")

        assertThat(rowExists("equipment", STALE_REMOTE_EQUIPMENT_ID)).isFalse()
        assertThat(rowExists("exercises", staleExercises[0].id)).isFalse()
        assertThat(rowExists("exercise_alternatives", STALE_REMOTE_ALTERNATIVE_ID)).isFalse()
        assertThat(rowExists("training_plans", STALE_REMOTE_PLAN_ID)).isFalse()
        assertThat(rowExists("plan_versions", STALE_REMOTE_PLAN_VERSION_ID)).isFalse()
        assertThat(rowExists("plan_days", STALE_REMOTE_PLAN_DAY_ID)).isFalse()
        assertThat(rowExists("plan_slots", STALE_REMOTE_PLAN_SLOT_ID)).isFalse()
        assertThat(rowExists("plan_slot_options", STALE_REMOTE_PLAN_OPTION_ID)).isFalse()
        assertThat(rowExists("equipment", canonical.equipment.first().id)).isTrue()
        assertThat(rowExists("plan_versions", canonical.planVersion.id)).isTrue()
        assertThat(rowExists("plan_versions", REMOTE_PLAN_VERSION_ID)).isTrue()
    }

    @Test
    fun bootstrapWithoutAssignmentCreatesStableFallbackForCurrentPlan() = runBlocking {
        LocalFitnessRepository(database, clock = clock).initialize()
        val bootstrap = BootstrapDto(
            user = UserDto(
                id = REMOTE_USER_ID,
                email = "new-athlete@example.com",
                displayName = "New Athlete",
                timezone = "Asia/Shanghai",
            ),
            currentPlan = PlanVersionDto(
                id = REMOTE_PLAN_VERSION_ID,
                planId = "remote-plan",
                planName = "System default",
                versionNumber = 1,
            ),
            assignments = emptyList(),
            syncCursor = "first-login-cursor",
        )

        store.replaceServerOwnedData(bootstrap)
        val firstAssignment = checkNotNull(database.planDao().activeAssignment(REMOTE_USER_ID))
        assertThat(firstAssignment.planVersionId).isEqualTo(REMOTE_PLAN_VERSION_ID)
        assertThat(firstAssignment.startLocalDate).isEqualTo(LOCAL_DATE)
        assertThat(database.planDao().currentPlanForUser(REMOTE_USER_ID)?.planVersion?.id)
            .isEqualTo(REMOTE_PLAN_VERSION_ID)

        store.replaceServerOwnedData(bootstrap.copy(syncCursor = "second-login-cursor"))
        val secondAssignment = checkNotNull(database.planDao().activeAssignment(REMOTE_USER_ID))
        assertThat(secondAssignment.id).isEqualTo(firstAssignment.id)
        assertThat(rowCount("plan_assignments", "user_id", REMOTE_USER_ID)).isEqualTo(1)
        assertThat(store.readCursor()).isEqualTo("second-login-cursor")
    }

    @Test
    fun switchingAccountsWithoutPendingMutationsAtomicallyRemovesOldServerScope() = runBlocking {
        store.replaceServerOwnedData(
            BootstrapDto(
                user = remoteUser(REMOTE_USER_ID, "first@example.com"),
                syncCursor = "first-cursor",
            ),
        )
        database.workoutDao().upsertSession(
            localWorkout(version = 1, notes = "first-account-cache").copy(
                id = STALE_REMOTE_WORKOUT_ID,
                userId = REMOTE_USER_ID,
                idempotencyKey = "first-account-cache",
            ),
        )

        store.replaceServerOwnedData(
            BootstrapDto(
                user = remoteUser(SECOND_REMOTE_USER_ID, "second@example.com"),
                cardioSessions = listOf(
                    CardioSessionDto(
                        id = SECOND_REMOTE_CARDIO_ID,
                        userId = SECOND_REMOTE_USER_ID,
                        localDate = LOCAL_DATE,
                        activity = "cycling",
                        durationSeconds = 1_200,
                        startedAt = NOW,
                    ),
                ),
                syncCursor = "second-cursor",
            ),
        )

        assertThat(database.userDao().getById(REMOTE_USER_ID)).isNull()
        assertThat(database.workoutDao().getSessionWithSets(STALE_REMOTE_WORKOUT_ID)).isNull()
        assertThat(database.userDao().getById(SECOND_REMOTE_USER_ID)?.email)
            .isEqualTo("second@example.com")
        assertThat(database.cardioDao().observeSessions(SECOND_REMOTE_USER_ID).first().single().id)
            .isEqualTo(SECOND_REMOTE_CARDIO_ID)
        assertThat(LocalFitnessRepository(database, clock = clock).currentUserId())
            .isEqualTo(SECOND_REMOTE_USER_ID)
        assertThat(store.readCursor()).isEqualTo("second-cursor")
    }

    @Test
    fun switchingAccountsWithPendingMutationsIsRejectedWithoutDeletingOldData() = runBlocking {
        store.replaceServerOwnedData(
            BootstrapDto(
                user = remoteUser(REMOTE_USER_ID, "first@example.com"),
                syncCursor = "first-cursor",
            ),
        )
        database.workoutDao().upsertSession(
            localWorkout(version = 1, notes = "unsynced-first-account").copy(
                id = PENDING_REMOTE_WORKOUT_ID,
                userId = REMOTE_USER_ID,
                idempotencyKey = "unsynced-first-account",
            ),
        )
        database.syncDao().enqueue(
            SyncOutboxEntity(
                id = "pending-account-switch-outbox",
                aggregateType = "workout_session",
                aggregateId = PENDING_REMOTE_WORKOUT_ID,
                operation = SyncOperation.UPSERT,
                payloadJson = "{}",
                idempotencyKey = "pending-account-switch-key",
                nextAttemptAt = clock.millis(),
                createdAt = clock.millis(),
                updatedAt = clock.millis(),
            ),
        )

        val error = try {
            store.replaceServerOwnedData(
                BootstrapDto(
                    user = remoteUser(SECOND_REMOTE_USER_ID, "second@example.com"),
                    syncCursor = "second-cursor",
                ),
            )
            null
        } catch (expected: PendingAccountSwitchException) {
            expected
        }

        assertThat(error).isNotNull()
        assertThat(error?.pendingMutationCount).isEqualTo(1)
        assertThat(database.userDao().getById(REMOTE_USER_ID)?.email).isEqualTo("first@example.com")
        assertThat(database.userDao().getById(SECOND_REMOTE_USER_ID)).isNull()
        assertThat(database.workoutDao().getSessionWithSets(PENDING_REMOTE_WORKOUT_ID)?.session?.notes)
            .isEqualTo("unsynced-first-account")
        assertThat(database.syncDao().pendingMutationCount()).isEqualTo(1)
        assertThat(LocalFitnessRepository(database, clock = clock).currentUserId())
            .isEqualTo(REMOTE_USER_ID)
        assertThat(store.readCursor()).isEqualTo("first-cursor")
    }

    @Test
    fun enteringLocalModeWithoutPendingMutationsPurgesAuthenticatedScope() = runBlocking {
        LocalFitnessRepository(database, clock = clock).initialize()
        store.replaceServerOwnedData(
            BootstrapDto(
                user = remoteUser(REMOTE_USER_ID, "first@example.com"),
                syncCursor = "first-cursor",
            ),
        )
        database.workoutDao().upsertSession(
            localWorkout(version = 1, notes = "remote-cache").copy(
                id = STALE_REMOTE_WORKOUT_ID,
                userId = REMOTE_USER_ID,
                idempotencyKey = "remote-cache",
            ),
        )

        store.releaseServerIdentityForLocalMode()
        LocalFitnessRepository(database, clock = clock).initialize()

        assertThat(database.userDao().getById(REMOTE_USER_ID)).isNull()
        assertThat(database.workoutDao().getSessionWithSets(STALE_REMOTE_WORKOUT_ID)).isNull()
        assertThat(database.userDao().getById(LocalFitnessRepository.LOCAL_USER_ID)).isNotNull()
        assertThat(LocalFitnessRepository(database, clock = clock).currentUserId())
            .isEqualTo(LocalFitnessRepository.LOCAL_USER_ID)
    }

    @Test
    fun enteringLocalModeWithPendingMutationsIsRejectedAndPreservesAuthenticatedScope() = runBlocking {
        LocalFitnessRepository(database, clock = clock).initialize()
        store.replaceServerOwnedData(
            BootstrapDto(
                user = remoteUser(REMOTE_USER_ID, "first@example.com"),
                syncCursor = "first-cursor",
            ),
        )
        database.syncDao().enqueue(
            SyncOutboxEntity(
                id = "pending-local-mode-outbox",
                aggregateType = "workout_session",
                aggregateId = PENDING_REMOTE_WORKOUT_ID,
                operation = SyncOperation.UPSERT,
                payloadJson = "{}",
                idempotencyKey = "pending-local-mode-key",
                nextAttemptAt = clock.millis(),
                createdAt = clock.millis(),
                updatedAt = clock.millis(),
            ),
        )

        val error = try {
            store.releaseServerIdentityForLocalMode()
            null
        } catch (expected: PendingAccountSwitchException) {
            expected
        }

        assertThat(error?.pendingMutationCount).isEqualTo(1)
        assertThat(database.userDao().getById(REMOTE_USER_ID)?.email).isEqualTo("first@example.com")
        assertThat(database.syncDao().pendingMutationCount()).isEqualTo(1)
        assertThat(LocalFitnessRepository(database, clock = clock).currentUserId())
            .isEqualTo(REMOTE_USER_ID)
        assertThat(store.readCursor()).isEqualTo("first-cursor")
    }

    @Test
    fun staleWorkoutUpsertAndDeleteAreIgnoredWhileNewerChangesApply() = runBlocking {
        database.workoutDao().upsertSession(localWorkout(version = 5, notes = "local-v5"))

        store.applyIncrementalChanges(
            changes(
                change(version = 4, operation = "UPSERT", payload = workoutPayload(4, "remote-v4")),
                change(version = 4, operation = "DELETE", payload = null),
            ),
        )

        val afterStaleChanges = database.workoutDao().getSessionWithSets(VERSIONED_WORKOUT_ID)?.session
        assertThat(afterStaleChanges?.version).isEqualTo(5L)
        assertThat(afterStaleChanges?.notes).isEqualTo("local-v5")
        assertThat(afterStaleChanges?.deletedAt).isNull()
        assertThat(afterStaleChanges?.status).isEqualTo(WorkoutStatus.COMPLETED)

        store.applyIncrementalChanges(
            changes(change(version = 6, operation = "UPSERT", payload = workoutPayload(6, "remote-v6"))),
        )

        val afterNewerUpsert = database.workoutDao().getSessionWithSets(VERSIONED_WORKOUT_ID)?.session
        assertThat(afterNewerUpsert?.version).isEqualTo(6L)
        assertThat(afterNewerUpsert?.notes).isEqualTo("remote-v6")
        assertThat(afterNewerUpsert?.userId).isEqualTo(REMOTE_USER_ID)

        store.applyIncrementalChanges(
            changes(change(version = 6, operation = "DELETE", payload = null)),
        )

        assertThat(database.workoutDao().getSessionWithSets(VERSIONED_WORKOUT_ID)).isNull()
    }

    private fun changes(vararg changes: SyncChangeDto) = SyncChangesDto(
        changes = changes.toList(),
        nextCursor = "cursor-${changes.last().version}-${changes.last().operation}",
    )

    private fun change(
        version: Long,
        operation: String,
        payload: Map<String, Any?>?,
    ) = SyncChangeDto(
        id = "change-$version-$operation",
        entityType = "workout_session",
        entityId = VERSIONED_WORKOUT_ID,
        operation = operation,
        version = version,
        payload = payload,
        changedAt = "2026-08-09T02:00:00Z",
    )

    private fun workoutPayload(version: Long, notes: String): Map<String, Any?> = linkedMapOf(
        "id" to VERSIONED_WORKOUT_ID,
        "user_id" to REMOTE_USER_ID,
        "plan_day_code" to "A",
        "local_date" to LOCAL_DATE,
        "timezone" to "Asia/Shanghai",
        "started_at" to NOW,
        "completed_at" to "2026-08-09T01:00:00Z",
        "status" to "COMPLETED",
        "plan_snapshot_json" to "{}",
        "idempotency_key" to "server-$VERSIONED_WORKOUT_ID",
        "notes" to notes,
        "version" to version,
    )

    private fun localWorkout(version: Long, notes: String) = WorkoutSessionEntity(
        id = VERSIONED_WORKOUT_ID,
        userId = LocalFitnessRepository.LOCAL_USER_ID,
        planVersionId = null,
        planDayCode = PlanCode.A,
        localDate = LOCAL_DATE,
        timezone = "Asia/Shanghai",
        startedAt = clock.millis(),
        completedAt = clock.millis() + 1,
        status = WorkoutStatus.COMPLETED,
        planSnapshotJson = "{}",
        idempotencyKey = "local-$VERSIONED_WORKOUT_ID",
        notes = notes,
        version = version,
        createdAt = clock.millis(),
        updatedAt = clock.millis(),
    )

    private fun remoteUser(id: String, email: String) = UserDto(
        id = id,
        email = email,
        displayName = email.substringBefore('@'),
        timezone = "Asia/Shanghai",
    )

    private fun rowExists(table: String, id: String): Boolean = database.openHelper.readableDatabase
        .query("SELECT 1 FROM $table WHERE id = ? LIMIT 1", arrayOf(id))
        .use { it.moveToFirst() }

    private fun rowCount(table: String, column: String, value: String): Int =
        database.openHelper.readableDatabase
            .query("SELECT COUNT(*) FROM $table WHERE $column = ?", arrayOf(value))
            .use { cursor ->
                check(cursor.moveToFirst())
                cursor.getInt(0)
            }

    private companion object {
        const val NOW = "2026-08-09T00:00:00Z"
        const val LOCAL_DATE = "2026-08-09"
        const val REMOTE_USER_ID = "remote-user"
        const val REMOTE_PLAN_VERSION_ID = "remote-plan-version"
        const val HISTORICAL_PLAN_VERSION_ID = "historical-plan-version"
        const val REMOTE_ASSIGNMENT_ID = "remote-assignment"
        const val HISTORICAL_ASSIGNMENT_ID = "historical-assignment"
        const val REMOTE_WORKOUT_ID = "remote-workout"
        const val REMOTE_READINESS_ID = "remote-readiness"
        const val REMOTE_CARDIO_ID = "remote-cardio"
        const val SECOND_REMOTE_USER_ID = "second-remote-user"
        const val SECOND_REMOTE_CARDIO_ID = "second-remote-cardio"
        const val STALE_REMOTE_WORKOUT_ID = "stale-remote-workout"
        const val PENDING_REMOTE_WORKOUT_ID = "pending-remote-workout"
        const val VERSIONED_WORKOUT_ID = "versioned-workout"
        const val STALE_REMOTE_EQUIPMENT_ID = "stale-remote-equipment"
        const val STALE_REMOTE_EXERCISE_ID = "stale-remote-exercise"
        const val STALE_REMOTE_ALTERNATIVE_ID = "stale-remote-alternative"
        const val STALE_REMOTE_PLAN_ID = "stale-remote-plan"
        const val STALE_REMOTE_PLAN_VERSION_ID = "stale-remote-plan-version"
        const val STALE_REMOTE_PLAN_DAY_ID = "stale-remote-plan-day"
        const val STALE_REMOTE_PLAN_SLOT_ID = "stale-remote-plan-slot"
        const val STALE_REMOTE_PLAN_OPTION_ID = "stale-remote-plan-option"
    }
}
