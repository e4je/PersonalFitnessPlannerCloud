package com.personalfitnessplanner.domain

import com.google.common.truth.Truth.assertThat
import org.junit.Test

class DoubleProgressionEngineTest {
    @Test
    fun increasesOneIncrementOnlyWhenEveryWorkingSetQualifies() {
        val result = DoubleProgressionEngine.recommend(
            baseInput(
                sets = List(3) { goodSet(reps = 12) },
            ),
        )

        assertThat(result.action).isEqualTo(ProgressionAction.INCREASE)
        assertThat(result.nextWeightKg).isEqualTo(42.5)
    }

    @Test
    fun warmupSetsDoNotBlockIncrease() {
        val result = DoubleProgressionEngine.recommend(
            baseInput(
                sets = listOf(
                    ProgressionSet(5, 5, MovementQuality.FAIR, pain = false, isWarmup = true),
                    goodSet(12),
                    goodSet(12),
                ),
            ),
        )

        assertThat(result.action).isEqualTo(ProgressionAction.INCREASE)
    }

    @Test
    fun painNeverSuggestsAddingWeight() {
        val result = DoubleProgressionEngine.recommend(
            baseInput(
                sets = listOf(goodSet(12), goodSet(12).copy(pain = true)),
            ),
        )

        assertThat(result.action).isEqualTo(ProgressionAction.HOLD)
        assertThat(result.reason).isEqualTo(ProgressionReason.PAIN_REPORTED)
        assertThat(result.nextWeightKg).isEqualTo(40.0)
    }

    @Test
    fun moreThanHalfBelowMinimumDropsOneIncrement() {
        val result = DoubleProgressionEngine.recommend(
            baseInput(
                sets = listOf(goodSet(7), goodSet(6), goodSet(10)),
            ),
        )

        assertThat(result.action).isEqualTo(ProgressionAction.DECREASE)
        assertThat(result.nextWeightKg).isEqualTo(37.5)
    }

    @Test
    fun twoConsecutiveFailuresDropsOneIncrement() {
        val result = DoubleProgressionEngine.recommend(
            baseInput(
                sets = listOf(goodSet(9), goodSet(9), goodSet(9)),
                consecutiveFailedSessions = 2,
            ),
        )

        assertThat(result.action).isEqualTo(ProgressionAction.DECREASE)
        assertThat(result.reason).isEqualTo(ProgressionReason.TWO_CONSECUTIVE_FAILURES)
    }

    @Test
    fun historiesAreIsolatedByExactAlternativeExerciseId() {
        val primary = ExerciseWeightRecord("bench-barbell", 10, 60.0, 8)
        val alternativeOld = ExerciseWeightRecord("bench-machine", 20, 40.0, 12)
        val alternativeNew = ExerciseWeightRecord("bench-machine", 30, 45.0, 10)

        assertThat(
            ExerciseWeightHistory.latestForExercise(
                "bench-barbell",
                listOf(alternativeNew, primary, alternativeOld),
            ),
        ).isEqualTo(primary)
        assertThat(
            ExerciseWeightHistory.latestForExercise(
                "bench-machine",
                listOf(primary, alternativeOld, alternativeNew),
            ),
        ).isEqualTo(alternativeNew)
    }

    private fun baseInput(
        sets: List<ProgressionSet>,
        consecutiveFailedSessions: Int = 0,
    ) = ProgressionInput(
        exerciseId = "exercise-id",
        currentWeightKg = 40.0,
        minimumIncrementKg = 2.5,
        repMin = 8,
        repMax = 12,
        sets = sets,
        consecutiveFailedSessions = consecutiveFailedSessions,
    )

    private fun goodSet(reps: Int) = ProgressionSet(
        reps = reps,
        rir = 2,
        quality = MovementQuality.GOOD,
        pain = false,
    )
}
