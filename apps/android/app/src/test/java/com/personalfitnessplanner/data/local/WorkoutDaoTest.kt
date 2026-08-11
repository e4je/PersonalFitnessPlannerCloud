package com.personalfitnessplanner.data.local

import androidx.room.Room
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class WorkoutDaoTest {
    private lateinit var database: AppDatabase

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            RuntimeEnvironment.getApplication(),
            AppDatabase::class.java,
        ).allowMainThreadQueries().build()
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun latestWeightIsScopedToExactSelectedExercise() = runBlocking {
        val dao = database.workoutDao()
        dao.insertSessionWithSets(
            completedSession(id = "session-1", completedAt = 300),
            listOf(
                completedSet(
                    id = "set-primary",
                    exerciseId = "barbell-bench",
                    weightKg = 60.0,
                    completedAt = 100,
                ),
                completedSet(
                    id = "set-alternative",
                    exerciseId = "machine-chest-press",
                    weightKg = 40.0,
                    completedAt = 200,
                ),
            ),
        )

        val primary = dao.latestCompletedWorkingSet("user-1", "barbell-bench")
        val alternative = dao.latestCompletedWorkingSet("user-1", "machine-chest-press")

        assertThat(primary?.weightKg).isEqualTo(60.0)
        assertThat(primary?.exerciseId).isEqualTo("barbell-bench")
        assertThat(alternative?.weightKg).isEqualTo(40.0)
        assertThat(alternative?.exerciseId).isEqualTo("machine-chest-press")
    }

    @Test
    fun sessionKeepsImmutablePlanVersionAndSnapshot() = runBlocking {
        val session = completedSession(id = "snapshot-session", completedAt = 300)
        database.workoutDao().insertSessionWithSets(session, emptyList())

        val stored = database.workoutDao().getSessionWithSets(session.id)?.session

        assertThat(stored?.planVersionId).isEqualTo("plan-version-1")
        assertThat(stored?.planSnapshotJson).isEqualTo("{\"version\":1}")
    }

    @Test
    fun outboxIdempotencyKeyCannotBeQueuedTwice() = runBlocking {
        val item = SyncOutboxEntity(
            id = "outbox-1",
            aggregateType = "workout_session",
            aggregateId = "session-1",
            operation = SyncOperation.UPSERT,
            payloadJson = "{}",
            idempotencyKey = "idem-1",
            nextAttemptAt = 0,
            createdAt = 1,
            updatedAt = 1,
        )

        val first = database.syncDao().enqueue(item)
        val duplicate = database.syncDao().enqueue(item.copy(id = "outbox-2"))

        assertThat(first).isNotEqualTo(-1)
        assertThat(duplicate).isEqualTo(-1)
    }

    private fun completedSession(id: String, completedAt: Long) = WorkoutSessionEntity(
        id = id,
        userId = "user-1",
        planVersionId = "plan-version-1",
        planDayCode = PlanCode.A,
        localDate = "2026-08-09",
        timezone = "Asia/Shanghai",
        startedAt = 1,
        completedAt = completedAt,
        status = WorkoutStatus.COMPLETED,
        planSnapshotJson = "{\"version\":1}",
        idempotencyKey = "idempotency-$id",
        createdAt = 1,
        updatedAt = completedAt,
    )

    private fun completedSet(
        id: String,
        exerciseId: String,
        weightKg: Double,
        completedAt: Long,
    ) = WorkoutSetEntity(
        id = id,
        sessionId = "session-1",
        planSlotId = "slot-1",
        sourcePlanSlotOptionId = "option-$exerciseId",
        exerciseId = exerciseId,
        equipmentId = "equipment-$exerciseId",
        setNumber = 1,
        weightKg = weightKg,
        reps = 10,
        durationSeconds = null,
        isWarmup = false,
        rir = 2,
        quality = SetQuality.GOOD,
        pain = false,
        completed = true,
        completedAt = completedAt,
        createdAt = 1,
        updatedAt = completedAt,
    )
}
