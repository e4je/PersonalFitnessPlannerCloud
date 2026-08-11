package com.personalfitnessplanner.domain

import com.personalfitnessplanner.data.local.PlanCode
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.temporal.TemporalAdjusters
import java.time.temporal.ChronoUnit

enum class RecommendedSession { A, B, RECOVERY, CARDIO, REST }

enum class RecommendationReason {
    MANUAL_OVERRIDE,
    HIGH_FATIGUE,
    WEEKLY_LIMIT_REACHED,
    CONSECUTIVE_FULL_BODY_PROTECTION,
    FIRST_STRENGTH_SESSION,
    ALTERNATE_AFTER_A,
    ALTERNATE_AFTER_B,
}

data class CompletedWorkout(
    val localDate: LocalDate,
    val planCode: PlanCode,
    val isFullBody: Boolean = true,
)

data class RecommendationInput(
    val today: LocalDate,
    val completedWorkouts: List<CompletedWorkout>,
    val fatigueScore: Int? = null,
    val weeklyLimit: Int = 3,
    val minimumRestDays: Int = 1,
    val fatigueThreshold: Int = 8,
    val manualOverride: RecommendedSession? = null,
)

data class TrainingRecommendation(
    val session: RecommendedSession,
    val reason: RecommendationReason,
    /** The A/B day that remains next even when today's recommendation is recovery. */
    val nextStrengthDay: PlanCode,
)

object TrainingRecommendationEngine {
    fun recommend(input: RecommendationInput): TrainingRecommendation {
        require(input.weeklyLimit > 0) { "weeklyLimit must be positive" }
        require(input.minimumRestDays >= 0) { "minimumRestDays cannot be negative" }
        require(input.fatigueThreshold in 0..10) {
            "fatigueThreshold must be between 0 and 10"
        }
        require(input.fatigueScore == null || input.fatigueScore in 0..10) {
            "fatigueScore must be between 0 and 10"
        }

        val completed = input.completedWorkouts
            .filter { !it.localDate.isAfter(input.today) }
            .sortedByDescending { it.localDate }
        val lastStrength = completed.firstOrNull()
        val nextStrengthDay = when (lastStrength?.planCode) {
            PlanCode.A -> PlanCode.B
            PlanCode.B -> PlanCode.A
            null -> PlanCode.A
        }

        input.manualOverride?.let { override ->
            return TrainingRecommendation(
                session = override,
                reason = RecommendationReason.MANUAL_OVERRIDE,
                nextStrengthDay = nextStrengthDay,
            )
        }

        if ((input.fatigueScore ?: 0) >= input.fatigueThreshold) {
            return recovery(RecommendationReason.HIGH_FATIGUE, nextStrengthDay)
        }

        val weekStart = input.today.with(TemporalAdjusters.previousOrSame(DayOfWeek.MONDAY))
        val completedThisWeek = completed.count { !it.localDate.isBefore(weekStart) }
        if (completedThisWeek >= input.weeklyLimit) {
            return recovery(RecommendationReason.WEEKLY_LIMIT_REACHED, nextStrengthDay)
        }

        val daysSinceLastFullBody = completed.firstOrNull { it.isFullBody }
            ?.let { ChronoUnit.DAYS.between(it.localDate, input.today) }
        if (daysSinceLastFullBody != null &&
            daysSinceLastFullBody in 0L..input.minimumRestDays.toLong()
        ) {
            return recovery(
                RecommendationReason.CONSECUTIVE_FULL_BODY_PROTECTION,
                nextStrengthDay,
            )
        }

        return when (lastStrength?.planCode) {
            null -> TrainingRecommendation(
                RecommendedSession.A,
                RecommendationReason.FIRST_STRENGTH_SESSION,
                PlanCode.A,
            )
            PlanCode.A -> TrainingRecommendation(
                RecommendedSession.B,
                RecommendationReason.ALTERNATE_AFTER_A,
                PlanCode.B,
            )
            PlanCode.B -> TrainingRecommendation(
                RecommendedSession.A,
                RecommendationReason.ALTERNATE_AFTER_B,
                PlanCode.A,
            )
        }
    }

    private fun recovery(reason: RecommendationReason, nextStrengthDay: PlanCode) =
        TrainingRecommendation(RecommendedSession.RECOVERY, reason, nextStrengthDay)
}
