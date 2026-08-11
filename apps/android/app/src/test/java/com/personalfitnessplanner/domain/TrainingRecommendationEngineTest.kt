package com.personalfitnessplanner.domain

import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.local.PlanCode
import java.time.LocalDate
import org.junit.Test

class TrainingRecommendationEngineTest {
    private val today = LocalDate.of(2026, 8, 16)

    @Test
    fun firstStrengthSessionDefaultsToA() {
        val result = TrainingRecommendationEngine.recommend(
            RecommendationInput(today = today, completedWorkouts = emptyList()),
        )

        assertThat(result.session).isEqualTo(RecommendedSession.A)
        assertThat(result.reason).isEqualTo(RecommendationReason.FIRST_STRENGTH_SESSION)
    }

    @Test
    fun alternatesFromActuallyCompletedDay() {
        val afterA = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(CompletedWorkout(today.minusDays(2), PlanCode.A)),
            ),
        )
        val afterB = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(CompletedWorkout(today.minusDays(2), PlanCode.B)),
            ),
        )

        assertThat(afterA.session).isEqualTo(RecommendedSession.B)
        assertThat(afterB.session).isEqualTo(RecommendedSession.A)
    }

    @Test
    fun yesterdayFullBodyTriggersRecoveryWithoutLosingNextAB() {
        val result = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(CompletedWorkout(today.minusDays(1), PlanCode.A)),
            ),
        )

        assertThat(result.session).isEqualTo(RecommendedSession.RECOVERY)
        assertThat(result.reason)
            .isEqualTo(RecommendationReason.CONSECUTIVE_FULL_BODY_PROTECTION)
        assertThat(result.nextStrengthDay).isEqualTo(PlanCode.B)
    }

    @Test
    fun threeCompletedThisWeekTriggersRecovery() {
        val result = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(
                    CompletedWorkout(today.minusDays(2), PlanCode.A),
                    CompletedWorkout(today.minusDays(4), PlanCode.B),
                    CompletedWorkout(today.minusDays(6), PlanCode.A),
                ),
            ),
        )

        assertThat(result.session).isEqualTo(RecommendedSession.RECOVERY)
        assertThat(result.reason).isEqualTo(RecommendationReason.WEEKLY_LIMIT_REACHED)
    }

    @Test
    fun fatigueEightThroughTenTriggersRecovery() {
        (8..10).forEach { fatigue ->
            val result = TrainingRecommendationEngine.recommend(
                RecommendationInput(today, emptyList(), fatigueScore = fatigue),
            )
            assertThat(result.session).isEqualTo(RecommendedSession.RECOVERY)
            assertThat(result.reason).isEqualTo(RecommendationReason.HIGH_FATIGUE)
        }
    }

    @Test
    fun manualOverrideWinsOverSafetyDefaults() {
        val result = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(CompletedWorkout(today.minusDays(1), PlanCode.A)),
                fatigueScore = 10,
                manualOverride = RecommendedSession.CARDIO,
            ),
        )

        assertThat(result.session).isEqualTo(RecommendedSession.CARDIO)
        assertThat(result.reason).isEqualTo(RecommendationReason.MANUAL_OVERRIDE)
    }

    @Test
    fun planSpecificFatigueThresholdAndMinimumRestDaysAreApplied() {
        val belowCustomFatigueThreshold = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = emptyList(),
                fatigueScore = 8,
                fatigueThreshold = 9,
            ),
        )
        val needsTwoRestDays = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(CompletedWorkout(today.minusDays(2), PlanCode.A)),
                fatigueScore = 3,
                minimumRestDays = 2,
            ),
        )

        assertThat(belowCustomFatigueThreshold.session).isEqualTo(RecommendedSession.A)
        assertThat(needsTwoRestDays.session).isEqualTo(RecommendedSession.RECOVERY)
        assertThat(needsTwoRestDays.reason)
            .isEqualTo(RecommendationReason.CONSECUTIVE_FULL_BODY_PROTECTION)
        assertThat(needsTwoRestDays.nextStrengthDay).isEqualTo(PlanCode.B)
    }

    @Test
    fun planSpecificWeeklyLimitIsApplied() {
        val result = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = listOf(
                    CompletedWorkout(today.minusDays(2), PlanCode.B),
                    CompletedWorkout(today.minusDays(4), PlanCode.A),
                ),
                weeklyLimit = 2,
            ),
        )

        assertThat(result.session).isEqualTo(RecommendedSession.RECOVERY)
        assertThat(result.reason).isEqualTo(RecommendationReason.WEEKLY_LIMIT_REACHED)
    }
}
