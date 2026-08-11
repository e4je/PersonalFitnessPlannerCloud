package com.personalfitnessplanner.data.repository

import androidx.room.Room
import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.defaultplan.DefaultTrainingPlan
import com.personalfitnessplanner.data.local.AppDatabase
import com.personalfitnessplanner.data.local.PlanAssignmentEntity
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.PlanVersionEntity
import com.personalfitnessplanner.data.local.SetQuality
import com.personalfitnessplanner.data.local.TrainingPlanEntity
import com.personalfitnessplanner.domain.recommendationContractVector
import java.time.Clock
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
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
class LocalFitnessRepositoryTest {
    private val clock = Clock.fixed(
        Instant.parse("2026-08-09T00:00:00Z"),
        ZoneOffset.UTC,
    )
    private lateinit var database: AppDatabase
    private lateinit var repository: LocalFitnessRepository
    private var generatedId = 0

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            RuntimeEnvironment.getApplication(),
            AppDatabase::class.java,
        ).allowMainThreadQueries().build()
        repository = LocalFitnessRepository(
            database = database,
            clock = clock,
            idFactory = { "generated-${generatedId++}" },
        )
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun newPlanAssignmentDoesNotInterruptWorkoutAlreadyInProgress() = runBlocking {
        val contract = recommendationContractVector("new-version-only-affects-new-workouts")
        val expectedExistingVersion = checkNotNull(contract.expected.existingWorkoutPlanVersionId)
        val expectedNextVersion = checkNotNull(contract.expected.nextWorkoutPlanVersionId)
        repository.initialize()
        val started = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
        )
        val originalPlan = checkNotNull(
            database.planDao().getVersionWithDays(checkNotNull(started.session.planVersionId)),
        )
        assertThat(started.session.planVersionId).isEqualTo(expectedExistingVersion)
        val newVersionId = expectedNextVersion
        val now = clock.millis()
        database.planDao().upsertVersions(
            listOf(
                originalPlan.planVersion.copy(
                    id = newVersionId,
                    versionNumber = originalPlan.planVersion.versionNumber + 1,
                    snapshotJson = "{\"version\":2}",
                    publishedAt = now,
                    version = originalPlan.planVersion.version + 1,
                    updatedAt = now + 1,
                ),
            ),
        )
        database.planDao().upsertAssignment(
            PlanAssignmentEntity(
                id = "server-assignment-v2",
                userId = LocalFitnessRepository.LOCAL_USER_ID,
                planVersionId = newVersionId,
                startLocalDate = "2026-08-09",
                createdAt = now + 1,
                updatedAt = now + 1,
            ),
        )

        assertThat(database.planDao().activeAssignment(LocalFitnessRepository.LOCAL_USER_ID)?.planVersionId)
            .isEqualTo(expectedNextVersion)

        val resumed = repository.startOrResumeWorkout(
            requestedDay = PlanCode.B,
            localDate = LocalDate.of(2026, 8, 10),
        )

        assertThat(resumed.session.id).isEqualTo(started.session.id)
        assertThat(resumed.session.planVersionId).isEqualTo(expectedExistingVersion)
        assertThat(resumed.session.planSnapshotJson).isEqualTo(started.session.planSnapshotJson)
        assertThat(resumed.sets.map { it.id }).containsExactlyElementsIn(started.sets.map { it.id }).inOrder()
    }

    @Test
    fun initializeMigratesLegacyBuiltInAssignmentToCanonicalVersionWithoutTwoActiveRows() = runBlocking {
        val now = clock.millis()
        database.planDao().upsertPlans(
            listOf(
                TrainingPlanEntity(
                    id = "legacy-built-in-plan",
                    name = "Legacy built-in",
                    description = "",
                    isBuiltIn = true,
                    createdAt = now - 10,
                    updatedAt = now - 10,
                ),
            ),
        )
        database.planDao().upsertVersions(
            listOf(
                PlanVersionEntity(
                    id = "legacy-built-in-version",
                    planId = "legacy-built-in-plan",
                    versionNumber = 1,
                    status = "PUBLISHED",
                    publishedAt = now - 10,
                    snapshotJson = "{}",
                    createdAt = now - 10,
                    updatedAt = now - 10,
                ),
            ),
        )
        database.planDao().upsertAssignment(
            PlanAssignmentEntity(
                id = "legacy-assignment",
                userId = LocalFitnessRepository.LOCAL_USER_ID,
                planVersionId = "legacy-built-in-version",
                startLocalDate = "2026-07-01",
                createdAt = now - 10,
                updatedAt = now - 10,
            ),
        )

        repository.initialize()

        val active = database.planDao().activeAssignment(LocalFitnessRepository.LOCAL_USER_ID)
        assertThat(active?.planVersionId).isEqualTo(DefaultTrainingPlan.VERSION_ID)
        assertThat(active?.startLocalDate).isEqualTo("2026-07-01")
        database.openHelper.readableDatabase.query(
            "SELECT COUNT(*) FROM plan_assignments WHERE user_id = ? AND is_active = 1 AND deleted_at IS NULL",
            arrayOf(LocalFitnessRepository.LOCAL_USER_ID),
        ).use { cursor ->
            assertThat(cursor.moveToFirst()).isTrue()
            assertThat(cursor.getInt(0)).isEqualTo(1)
        }
    }

    @Test
    fun repeatedSetCompletionIsNoOpAndDoesNotQueueDuplicateMutation() = runBlocking {
        repository.initialize()
        val started = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
        )
        val targetSet = started.sets.first()
        val input = WorkoutSetInput(
            weightKg = 40.0,
            reps = 10,
            rir = 2,
            quality = SetQuality.GOOD,
            notes = " controlled ",
        )

        val completedOnce = repository.completeSet(started.session.id, targetSet.id, input)
        val outboxAfterFirstClick = database.syncDao().readyItems(clock.millis(), limit = 100)
        val completedTwice = repository.completeSet(started.session.id, targetSet.id, input)
        val outboxAfterSecondClick = database.syncDao().readyItems(clock.millis(), limit = 100)

        assertThat(completedTwice).isEqualTo(completedOnce)
        assertThat(outboxAfterFirstClick).hasSize(2) // Session creation plus the completed set mutation.
        assertThat(outboxAfterSecondClick.map { it.id })
            .containsExactlyElementsIn(outboxAfterFirstClick.map { it.id })
            .inOrder()
        assertThat(outboxAfterSecondClick.map { it.idempotencyKey }).containsNoDuplicates()
    }

    @Test
    fun workoutSetsFollowSnapshotSlotPositionsInsteadOfRoomRelationOrder() = runBlocking {
        repository.initialize()
        val plan = checkNotNull(repository.currentPlan())
        val expectedSlotOrder = checkNotNull(plan.days.firstOrNull { it.day.code == PlanCode.A })
            .slots
            .sortedBy { it.slot.position }
            .map { it.slot.id }

        val started = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
        )
        val actualSlotOrder = started.sets.mapNotNull { it.planSlotId }.distinct()

        assertThat(actualSlotOrder).containsExactlyElementsIn(expectedSlotOrder).inOrder()
        assertThat(repository.activeWorkout()?.sets?.mapNotNull { it.planSlotId }?.distinct())
            .containsExactlyElementsIn(expectedSlotOrder)
            .inOrder()
    }

    @Test
    fun swapExercisePersistsAlternativeAndClearsPreviousExerciseDraft() = runBlocking {
        repository.initialize()
        val plan = checkNotNull(repository.currentPlan())
        val slot = checkNotNull(plan.days.firstOrNull { it.day.code == PlanCode.A })
            .slots
            .minBy { it.slot.position }
        val alternative = checkNotNull(
            slot.options.filter { !it.isPreferred && it.deletedAt == null }.minByOrNull { it.sortOrder },
        )
        val started = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
        )
        val slotSets = started.sets.filter { it.planSlotId == slot.slot.id }
        val drafted = repository.saveSet(
            sessionId = started.session.id,
            setId = slotSets.first().id,
            input = WorkoutSetInput(
                weightKg = 60.0,
                reps = 10,
                durationSeconds = 20,
                isWarmup = true,
                rir = 2,
                quality = SetQuality.GOOD,
                pain = true,
                notes = "old exercise setup",
            ),
        )
        val beforeById = drafted.sets.associateBy { it.id }

        val swapped = repository.swapExercise(
            sessionId = started.session.id,
            planSlotId = slot.slot.id,
            exerciseId = alternative.exerciseId,
        )
        val swappedSlotSets = swapped.sets.filter { it.planSlotId == slot.slot.id }

        assertThat(swappedSlotSets).hasSize(slotSets.size)
        swappedSlotSets.forEach { set ->
            val before = checkNotNull(beforeById[set.id])
            assertThat(set.exerciseId).isEqualTo(alternative.exerciseId)
            assertThat(set.equipmentId).isEqualTo(alternative.equipmentId)
            assertThat(set.sourcePlanSlotOptionId).isEqualTo(alternative.id)
            assertThat(set.weightKg).isNull()
            assertThat(set.reps).isNull()
            assertThat(set.durationSeconds).isNull()
            assertThat(set.rir).isNull()
            assertThat(set.quality).isNull()
            assertThat(set.pain).isFalse()
            assertThat(set.notes).isNull()
            assertThat(set.completed).isFalse()
            assertThat(set.completedAt).isNull()
            assertThat(set.version).isEqualTo(before.version + 1)
            assertThat(set.updatedAt).isGreaterThan(before.updatedAt)
        }
        assertThat(swapped.session.version).isEqualTo(drafted.session.version + 1)
        assertThat(swapped.session.updatedAt).isGreaterThan(drafted.session.updatedAt)
        assertThat(swapped.session.planSnapshotJson).contains(alternative.id)
        assertThat(swapped.session.planSnapshotJson).contains(alternative.exerciseId)
        assertThat(repository.getWorkout(started.session.id)).isEqualTo(swapped)

        val outboxAfterSwap = database.syncDao().readyItems(clock.millis(), limit = 100)
        assertThat(outboxAfterSwap.last().payloadJson).contains("\"updated_at\"")
        assertThat(outboxAfterSwap.last().payloadJson).contains("\"version\"")
        val repeated = repository.swapExercise(
            sessionId = started.session.id,
            planSlotId = slot.slot.id,
            exerciseId = alternative.exerciseId,
        )
        val outboxAfterRepeat = database.syncDao().readyItems(clock.millis(), limit = 100)

        assertThat(repeated).isEqualTo(swapped)
        assertThat(outboxAfterRepeat.map { it.id })
            .containsExactlyElementsIn(outboxAfterSwap.map { it.id })
            .inOrder()
        assertThat(outboxAfterRepeat.map { it.idempotencyKey }).containsNoDuplicates()
    }

    @Test
    fun swappedAlternativeHasIndependentExactExerciseWeightHistory() = runBlocking {
        repository.initialize()
        val plan = checkNotNull(repository.currentPlan())
        val slot = checkNotNull(plan.days.firstOrNull { it.day.code == PlanCode.A })
            .slots
            .minBy { it.slot.position }
        val primary = checkNotNull(slot.options.firstOrNull { it.isPreferred })
        val alternative = checkNotNull(
            slot.options.filter { !it.isPreferred && it.deletedAt == null }.minByOrNull { it.sortOrder },
        )

        val primaryWorkout = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
        )
        val primarySet = primaryWorkout.sets.first { it.planSlotId == slot.slot.id }
        repository.completeSet(
            primaryWorkout.session.id,
            primarySet.id,
            WorkoutSetInput(weightKg = 60.0, reps = 10, rir = 2, quality = SetQuality.GOOD),
        )
        repository.finishWorkout(primaryWorkout.session.id)

        val alternativeWorkout = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 11),
        )
        val swapped = repository.swapExercise(
            alternativeWorkout.session.id,
            slot.slot.id,
            alternative.exerciseId,
        )
        val alternativeSet = swapped.sets.first { it.planSlotId == slot.slot.id }
        repository.completeSet(
            swapped.session.id,
            alternativeSet.id,
            WorkoutSetInput(weightKg = 40.0, reps = 12, rir = 2, quality = SetQuality.GOOD),
        )
        repository.finishWorkout(swapped.session.id)

        val primaryHistory = repository.weightHistory(exerciseId = primary.exerciseId)
        val alternativeHistory = repository.weightHistory(exerciseId = alternative.exerciseId)

        assertThat(primaryHistory.map { it.exerciseId }).containsExactly(primary.exerciseId)
        assertThat(primaryHistory.map { it.weightKg }).containsExactly(60.0)
        assertThat(alternativeHistory.map { it.exerciseId }).containsExactly(alternative.exerciseId)
        assertThat(alternativeHistory.map { it.weightKg }).containsExactly(40.0)
        Unit
    }

    @Test
    fun swapExerciseRejectsSlotAfterAnySetIsCompleted() = runBlocking {
        repository.initialize()
        val plan = checkNotNull(repository.currentPlan())
        val slot = checkNotNull(plan.days.firstOrNull { it.day.code == PlanCode.A })
            .slots
            .minBy { it.slot.position }
        val alternative = checkNotNull(slot.options.firstOrNull { !it.isPreferred })
        val started = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
        )
        val completedSet = started.sets.first { it.planSlotId == slot.slot.id }
        repository.completeSet(
            started.session.id,
            completedSet.id,
            WorkoutSetInput(weightKg = 60.0, reps = 10),
        )

        val error = runCatching {
            repository.swapExercise(started.session.id, slot.slot.id, alternative.exerciseId)
        }.exceptionOrNull()

        assertThat(error).isInstanceOf(IllegalStateException::class.java)
        assertThat(error).hasMessageThat().contains("completed set")
    }

    @Test
    fun startWorkoutPersistsTodaySelectionsAndSkippedSlotsIntoSnapshotAndSets() = runBlocking {
        repository.initialize()
        val day = checkNotNull(repository.currentPlan())
            .days
            .first { it.day.code == PlanCode.A }
        val orderedSlots = day.slots.sortedBy { it.slot.position }
        val selectedSlot = orderedSlots.first()
        val skippedSlot = orderedSlots[1]
        val alternative = checkNotNull(
            selectedSlot.options.filter { !it.isPreferred && it.deletedAt == null }
                .minByOrNull { it.sortOrder },
        )

        val started = repository.startOrResumeWorkout(
            requestedDay = PlanCode.A,
            localDate = LocalDate.of(2026, 8, 9),
            exerciseSelections = mapOf(selectedSlot.slot.id to alternative.exerciseId),
            skippedSlotIds = setOf(skippedSlot.slot.id),
        )

        val selectedSets = started.sets.filter { it.planSlotId == selectedSlot.slot.id }
        assertThat(selectedSets).isNotEmpty()
        assertThat(selectedSets.map { it.exerciseId }.distinct()).containsExactly(alternative.exerciseId)
        assertThat(selectedSets.map { it.sourcePlanSlotOptionId }.distinct()).containsExactly(alternative.id)
        assertThat(started.sets.mapNotNull { it.planSlotId }).doesNotContain(skippedSlot.slot.id)
        assertThat(started.session.planSnapshotJson).contains(alternative.id)
        assertThat(started.session.planSnapshotJson).doesNotContain(skippedSlot.slot.id)
        assertThat(repository.getWorkout(started.session.id)).isEqualTo(started)
    }
}
