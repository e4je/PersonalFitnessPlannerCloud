package com.personalfitnessplanner.domain

object PlanLifecycleRules {
    fun effectiveSetCount(
        trainingWeek: Int,
        prescribedSets: Int,
        adaptationWeeks: Int,
        adaptationSets: Int,
    ): Int {
        require(prescribedSets > 0) { "prescribedSets must be positive" }
        require(adaptationWeeks >= 0) { "adaptationWeeks cannot be negative" }
        require(adaptationSets > 0) { "adaptationSets must be positive" }
        return if (trainingWeek in 1..adaptationWeeks) {
            minOf(adaptationSets, prescribedSets)
        } else {
            prescribedSets
        }
    }
}
