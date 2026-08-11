package com.personalfitnessplanner.domain

enum class MovementQuality { POOR, FAIR, GOOD }

enum class ProgressionAction { INCREASE, HOLD, DECREASE }

enum class ProgressionReason {
    PAIN_REPORTED,
    ALL_WORKING_SETS_AT_UPPER_BOUND,
    MORE_THAN_HALF_BELOW_LOWER_BOUND,
    TWO_CONSECUTIVE_FAILURES,
    KEEP_BUILDING_REPS,
    NO_COMPLETED_WORKING_SETS,
}

data class ProgressionSet(
    val reps: Int,
    val rir: Int?,
    val quality: MovementQuality?,
    val pain: Boolean,
    val isWarmup: Boolean = false,
    val completed: Boolean = true,
)

data class ProgressionInput(
    /** Records are for this exact exercise UUID, never for a slot or an alternative family. */
    val exerciseId: String,
    val currentWeightKg: Double,
    val minimumIncrementKg: Double,
    val repMin: Int,
    val repMax: Int,
    val sets: List<ProgressionSet>,
    val consecutiveFailedSessions: Int = 0,
)

data class ProgressionRecommendation(
    val exerciseId: String,
    val action: ProgressionAction,
    val nextWeightKg: Double,
    val reason: ProgressionReason,
)

object DoubleProgressionEngine {
    fun recommend(input: ProgressionInput): ProgressionRecommendation {
        require(input.exerciseId.isNotBlank()) { "exerciseId is required" }
        require(input.currentWeightKg >= 0.0) { "currentWeightKg cannot be negative" }
        require(input.minimumIncrementKg > 0.0) { "minimumIncrementKg must be positive" }
        require(input.repMin > 0 && input.repMax >= input.repMin) { "invalid rep range" }
        require(input.consecutiveFailedSessions >= 0) {
            "consecutiveFailedSessions cannot be negative"
        }

        val workingSets = input.sets.filter { !it.isWarmup && it.completed }
        if (workingSets.isEmpty()) {
            return result(input, ProgressionAction.HOLD, input.currentWeightKg, ProgressionReason.NO_COMPLETED_WORKING_SETS)
        }

        if (workingSets.any { it.pain }) {
            return result(input, ProgressionAction.HOLD, input.currentWeightKg, ProgressionReason.PAIN_REPORTED)
        }

        val belowMinimum = workingSets.count { it.reps < input.repMin }
        if (belowMinimum * 2 > workingSets.size) {
            return decrease(input, ProgressionReason.MORE_THAN_HALF_BELOW_LOWER_BOUND)
        }
        if (input.consecutiveFailedSessions >= 2) {
            return decrease(input, ProgressionReason.TWO_CONSECUTIVE_FAILURES)
        }

        val earnedIncrease = workingSets.all { set ->
            set.reps >= input.repMax &&
                set.quality == MovementQuality.GOOD &&
                set.rir != null &&
                set.rir >= 1
        }
        if (earnedIncrease) {
            return result(
                input,
                ProgressionAction.INCREASE,
                input.currentWeightKg + input.minimumIncrementKg,
                ProgressionReason.ALL_WORKING_SETS_AT_UPPER_BOUND,
            )
        }

        return result(
            input,
            ProgressionAction.HOLD,
            input.currentWeightKg,
            ProgressionReason.KEEP_BUILDING_REPS,
        )
    }

    private fun decrease(
        input: ProgressionInput,
        reason: ProgressionReason,
    ): ProgressionRecommendation = result(
        input,
        ProgressionAction.DECREASE,
        (input.currentWeightKg - input.minimumIncrementKg).coerceAtLeast(0.0),
        reason,
    )

    private fun result(
        input: ProgressionInput,
        action: ProgressionAction,
        nextWeightKg: Double,
        reason: ProgressionReason,
    ) = ProgressionRecommendation(
        exerciseId = input.exerciseId,
        action = action,
        nextWeightKg = nextWeightKg,
        reason = reason,
    )
}

data class ExerciseWeightRecord(
    val exerciseId: String,
    val completedAt: Long,
    val weightKg: Double,
    val reps: Int,
)

object ExerciseWeightHistory {
    fun latestForExercise(
        exerciseId: String,
        records: Iterable<ExerciseWeightRecord>,
    ): ExerciseWeightRecord? = records
        .asSequence()
        .filter { it.exerciseId == exerciseId }
        .maxByOrNull { it.completedAt }
}
