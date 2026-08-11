package com.personalfitnessplanner.ui

import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.SetQuality
import com.personalfitnessplanner.data.local.WorkoutSessionEntity
import com.personalfitnessplanner.data.local.WorkoutSessionWithSets
import com.personalfitnessplanner.data.local.WorkoutSetEntity
import com.personalfitnessplanner.data.local.WorkoutStatus
import com.personalfitnessplanner.ui.model.HistoryFilterUi
import java.time.LocalDate
import org.junit.Test

class FitnessViewModelMappingTest {
    @Test
    fun trainingDayCsvUsesIsoDayNumbersAndRejectsEmptySelection() {
        assertThat(parseTrainingDayCsv("1,3,7")).containsExactly(1, 3, 7)
        assertThat(runCatching { parseTrainingDayCsv("") }.isFailure).isTrue()
        assertThat(runCatching { parseTrainingDayCsv("1,8") }.isFailure).isTrue()
    }

    @Test
    fun historyFilterAppliesPeriodAndExactPlanCode() {
        val recentA = workout("recent-a", "2026-08-09", PlanCode.A)
        val oldA = workout("old-a", "2026-07-01", PlanCode.A)
        val recentB = workout("recent-b", "2026-08-08", PlanCode.B)

        val result = filterHistoryRecords(
            records = listOf(recentA, oldA, recentB),
            filter = HistoryFilterUi(period = "近 7 天", workoutType = "A"),
            today = LocalDate.of(2026, 8, 9),
        )

        assertThat(result.map { it.session.id }).containsExactly("recent-a")
    }

    @Test
    fun trendUsesOnlyCompletedWorkingSetsForExactExerciseId() {
        val record = workout(
            id = "session",
            date = "2026-08-09",
            code = PlanCode.A,
            sets = listOf(
                workoutSet("working-a", "session", "exercise-a", 40.0, warmup = false),
                workoutSet("warmup-a", "session", "exercise-a", 20.0, warmup = true),
                workoutSet("working-b", "session", "exercise-b", 100.0, warmup = false),
            ),
        )

        val points = realTrendPoints(listOf(record), exerciseId = "exercise-a")

        assertThat(points).hasSize(1)
        assertThat(points.single().value).isEqualTo(40f)
        assertThat(points.single().displayValue).contains("40 kg")
    }

    private fun workout(
        id: String,
        date: String,
        code: PlanCode,
        sets: List<WorkoutSetEntity> = emptyList(),
    ): WorkoutSessionWithSets = WorkoutSessionWithSets(
        session = WorkoutSessionEntity(
            id = id,
            userId = "user",
            planVersionId = "plan-version",
            planDayCode = code,
            localDate = date,
            timezone = "Asia/Shanghai",
            startedAt = 1_000L,
            completedAt = 2_000L,
            status = WorkoutStatus.COMPLETED,
            planSnapshotJson = "{}",
            idempotencyKey = "key-$id",
            createdAt = 1_000L,
            updatedAt = 2_000L,
        ),
        sets = sets,
    )

    private fun workoutSet(
        id: String,
        sessionId: String,
        exerciseId: String,
        weightKg: Double,
        warmup: Boolean,
    ) = WorkoutSetEntity(
        id = id,
        sessionId = sessionId,
        planSlotId = "slot",
        sourcePlanSlotOptionId = "option",
        exerciseId = exerciseId,
        equipmentId = null,
        setNumber = 1,
        weightKg = weightKg,
        reps = 10,
        durationSeconds = null,
        isWarmup = warmup,
        rir = 2,
        quality = SetQuality.GOOD,
        pain = false,
        completed = true,
        completedAt = 2_000L,
        createdAt = 1_000L,
        updatedAt = 2_000L,
    )
}
