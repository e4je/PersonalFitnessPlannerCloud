package com.personalfitnessplanner.data.remote

import com.google.common.truth.Truth.assertThat
import org.junit.Test

class RemoteMappersTest {
    @Test
    fun planUsesServerIntroRulesAndKeepsCompleteRuleAndPrescriptionSnapshot() {
        val dto = PlanVersionDto(
            id = "version-id",
            planId = "plan-id",
            planName = "Server plan",
            weeklyFrequency = 4,
            minRestDays = 2,
            fatigueThreshold = 7,
            initialReducedWeeks = 3,
            initialSetCount = 1,
            rules = mapOf("selection_rule" to "choose_one", "target_rir" to listOf(1, 2)),
            days = listOf(
                PlanDayDto(
                    id = "day-id",
                    planVersionId = "version-id",
                    slots = listOf(
                        PlanSlotDto(
                            id = "slot-id",
                            planDayId = "day-id",
                            options = listOf(
                                PlanSlotOptionDto(
                                    id = "option-id",
                                    planSlotId = "slot-id",
                                    exerciseId = "exercise-id",
                                    setCount = 4,
                                    durationSecondsMin = 30,
                                    durationSecondsMax = 60,
                                    isPerSide = true,
                                    prescriptionText = "每侧保持 30～60 秒",
                                ),
                            ),
                        ),
                    ),
                ),
            ),
        )

        val mapped = RemoteMappers.plan(dto, now = 123L)

        assertThat(mapped.options.single().introWeeks).isEqualTo(3)
        assertThat(mapped.options.single().introSetCount).isEqualTo(1)
        assertThat(mapped.options.single().repMin).isEqualTo(30)
        assertThat(mapped.options.single().repMax).isEqualTo(60)
        assertThat(mapped.options.single().repUnit).isEqualTo("seconds")
        assertThat(mapped.version.snapshotJson).contains("\"weekly_frequency\":4")
        assertThat(mapped.version.snapshotJson).contains("\"min_rest_days\":2")
        assertThat(mapped.version.snapshotJson).contains("\"fatigue_threshold\":7")
        assertThat(mapped.version.snapshotJson).contains("\"initial_reduced_weeks\":3")
        assertThat(mapped.version.snapshotJson).contains("\"initial_set_count\":1")
        assertThat(mapped.version.snapshotJson).contains("\"prescription_text\":\"每侧保持 30～60 秒\"")
        assertThat(PlanRuleSnapshotParser.parse(mapped.version.snapshotJson)).isEqualTo(
            PlanRecommendationRules(
                weeklyLimit = 4,
                minimumRestDays = 2,
                fatigueThreshold = 7,
            ),
        )
    }

    @Test
    fun optionLevelIntroAndPerSidePrescriptionOverrideCompatibilityDefaults() {
        val mapped = RemoteMappers.planSlotOption(
            dto = PlanSlotOptionDto(
                id = "option-id",
                planSlotId = "slot-id",
                exerciseId = "exercise-id",
                setCount = 5,
                introSetCount = 3,
                introWeeks = 4,
                repMin = 8,
                repMax = 12,
                repUnit = "reps",
                isPerSide = true,
            ),
            now = 123L,
        )

        assertThat(mapped.introSetCount).isEqualTo(3)
        assertThat(mapped.introWeeks).isEqualTo(4)
        assertThat(mapped.repUnit).isEqualTo("reps_per_side")
    }

    @Test
    fun ruleParserReadsCanonicalAndEmbeddedLegacySnapshotsWithSafeDefaults() {
        val canonical =
            """{"weekly_strength_target":5,"minimum_rest_days":2,"fatigue_threshold":6}"""
        val embedded = RemoteMappers.plan(
            PlanVersionDto(
                id = "version-id",
                planId = "plan-id",
                snapshotJson = canonical,
            ),
            now = 123L,
        ).version.snapshotJson

        assertThat(PlanRuleSnapshotParser.parse(canonical)).isEqualTo(
            PlanRecommendationRules(weeklyLimit = 5, minimumRestDays = 2, fatigueThreshold = 6),
        )
        assertThat(PlanRuleSnapshotParser.parse(embedded)).isEqualTo(
            PlanRecommendationRules(weeklyLimit = 5, minimumRestDays = 2, fatigueThreshold = 6),
        )
        assertThat(PlanRuleSnapshotParser.parse("not-json")).isEqualTo(PlanRecommendationRules())
    }
}
