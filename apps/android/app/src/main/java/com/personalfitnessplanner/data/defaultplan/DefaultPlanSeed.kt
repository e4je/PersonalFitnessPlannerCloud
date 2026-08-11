package com.personalfitnessplanner.data.defaultplan

import com.personalfitnessplanner.data.local.EquipmentEntity
import com.personalfitnessplanner.data.local.ExerciseAlternativeEntity
import com.personalfitnessplanner.data.local.ExerciseEntity
import com.personalfitnessplanner.data.local.PlanDayEntity
import com.personalfitnessplanner.data.local.PlanSlotEntity
import com.personalfitnessplanner.data.local.PlanSlotOptionEntity
import com.personalfitnessplanner.data.local.PlanVersionEntity
import com.personalfitnessplanner.data.local.TrainingPlanEntity

data class DefaultPlanSeed(
    val plan: TrainingPlanEntity,
    val planVersion: PlanVersionEntity,
    val days: List<PlanDayEntity>,
    val slots: List<PlanSlotEntity>,
    val options: List<PlanSlotOptionEntity>,
    val equipment: List<EquipmentEntity>,
    val exercises: List<ExerciseEntity>,
    val alternatives: List<ExerciseAlternativeEntity>,
)
