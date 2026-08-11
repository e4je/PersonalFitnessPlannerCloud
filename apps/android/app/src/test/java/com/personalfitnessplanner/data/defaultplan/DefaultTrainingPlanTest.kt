package com.personalfitnessplanner.data.defaultplan

import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.local.PlanCode
import org.junit.Test

class DefaultTrainingPlanTest {
    private val seed = DefaultTrainingPlan.create(nowEpochMillis = 1_700_000_000_000)

    @Test
    fun containsEightOrderedSlotsForBothAAndB() {
        assertThat(seed.days.map { it.code }).containsExactly(PlanCode.A, PlanCode.B).inOrder()
        seed.days.forEach { day ->
            val slots = seed.slots.filter { it.planDayId == day.id }
            assertThat(slots).hasSize(8)
            assertThat(slots.map { it.position }).containsExactly(1, 2, 3, 4, 5, 6, 7, 8).inOrder()
        }
    }

    @Test
    fun everySlotHasOnePreferredOptionAndAlternatives() {
        seed.slots.forEach { slot ->
            val options = seed.options.filter { it.planSlotId == slot.id }
            assertThat(options.size).isAtLeast(3)
            assertThat(options.count { it.isPreferred }).isEqualTo(1)
            assertThat(options.map { it.sortOrder }).containsNoDuplicates()
            assertThat(options.all { it.introWeeks == 2 && it.introSetCount == minOf(2, it.setCount) }).isTrue()
        }
    }

    @Test
    fun everyOptionResolvesExerciseEquipmentAndTechniqueCues() {
        val exerciseIds = seed.exercises.map { it.id }.toSet()
        val equipmentIds = seed.equipment.map { it.id }.toSet()

        seed.options.forEach { option ->
            assertThat(option.exerciseId).isIn(exerciseIds)
            assertThat(option.equipmentId).isIn(equipmentIds)
            assertThat(option.repMin).isGreaterThan(0)
            assertThat(option.repMax).isAtLeast(option.repMin)
        }
        assertThat(seed.slots.all { it.cues.isNotBlank() }).isTrue()
        assertThat(seed.exercises.all { it.cues.isNotBlank() && it.commonMistakes.isNotBlank() })
            .isTrue()
    }

    @Test
    fun identifiersAreStableAcrossSeedRuns() {
        val second = DefaultTrainingPlan.create(nowEpochMillis = 1_800_000_000_000)

        assertThat(second.plan.id).isEqualTo(seed.plan.id)
        assertThat(second.planVersion.id).isEqualTo(seed.planVersion.id)
        assertThat(second.exercises.map { it.id }).containsExactlyElementsIn(seed.exercises.map { it.id }).inOrder()
        assertThat(second.options.map { it.id }).containsExactlyElementsIn(seed.options.map { it.id }).inOrder()
    }

    @Test
    fun usesCanonicalIdsAndGloballyDeduplicatesExercises() {
        assertThat(seed.plan.id).isEqualTo("10000000-0000-0000-0000-000000000000")
        assertThat(seed.planVersion.id).isEqualTo("10000000-0000-0000-0000-000000000001")
        assertThat(seed.slots).hasSize(16)
        assertThat(seed.options).hasSize(79)
        assertThat(seed.exercises).hasSize(66)
        assertThat(seed.equipment).hasSize(52)
        assertThat(seed.exercises.map { it.id }).containsNoDuplicates()
        assertThat(seed.options.first().id).isEqualTo("30000000-0000-0000-0000-000000000011")
    }

    @Test
    fun storesTheCompleteCanonicalContractAsTheVersionSnapshot() {
        val snapshot = seed.planVersion.snapshotJson

        assertThat(snapshot).contains("\"schema_version\"")
        assertThat(snapshot).contains("\"2026.08.1\"")
        assertThat(snapshot).contains("\"weekly_strength_target\"")
        assertThat(snapshot).contains("\"minimum_rest_days\"")
        assertThat(snapshot).contains("\"fatigue_threshold\"")
        assertThat(snapshot).contains("\"selection_rule\"")
        assertThat(snapshot).contains("\"rest_seconds\"")
        assertThat(snapshot).contains("\"per_side\"")
    }
}
