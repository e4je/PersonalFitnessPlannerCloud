package com.personalfitnessplanner.data.local

import androidx.room.Embedded
import androidx.room.Relation

data class PlanSlotWithOptions(
    @Embedded val slot: PlanSlotEntity,
    @Relation(
        parentColumn = "id",
        entityColumn = "plan_slot_id",
        entity = PlanSlotOptionEntity::class,
    )
    val options: List<PlanSlotOptionEntity>,
)

data class PlanDayWithSlots(
    @Embedded val day: PlanDayEntity,
    @Relation(
        parentColumn = "id",
        entityColumn = "plan_day_id",
        entity = PlanSlotEntity::class,
    )
    val slots: List<PlanSlotWithOptions>,
)

data class PlanVersionWithDays(
    @Embedded val planVersion: PlanVersionEntity,
    @Relation(
        parentColumn = "id",
        entityColumn = "plan_version_id",
        entity = PlanDayEntity::class,
    )
    val days: List<PlanDayWithSlots>,
)

data class WorkoutSessionWithSets(
    @Embedded val session: WorkoutSessionEntity,
    @Relation(parentColumn = "id", entityColumn = "session_id")
    val sets: List<WorkoutSetEntity>,
)
