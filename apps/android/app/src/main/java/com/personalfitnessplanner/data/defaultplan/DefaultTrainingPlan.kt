package com.personalfitnessplanner.data.defaultplan

import androidx.room.withTransaction
import com.personalfitnessplanner.data.local.AppDatabase
import com.personalfitnessplanner.data.local.EquipmentEntity
import com.personalfitnessplanner.data.local.ExerciseAlternativeEntity
import com.personalfitnessplanner.data.local.ExerciseEntity
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.PlanDayEntity
import com.personalfitnessplanner.data.local.PlanSlotEntity
import com.personalfitnessplanner.data.local.PlanSlotOptionEntity
import com.personalfitnessplanner.data.local.PlanVersionEntity
import com.personalfitnessplanner.data.local.TrainingPlanEntity
import com.squareup.moshi.Json
import com.squareup.moshi.JsonClass
import com.squareup.moshi.Moshi
import com.squareup.moshi.kotlin.reflect.KotlinJsonAdapterFactory
import java.time.Instant
import java.util.UUID

/** Builds the bundled plan from the canonical cross-client contract resource. */
object DefaultTrainingPlan {
    private const val RESOURCE_NAME = "default-training-plan.json"
    private val loadedContract: LoadedContract by lazy(LazyThreadSafetyMode.PUBLICATION) {
        val rawJson = DefaultTrainingPlan::class.java.classLoader
            ?.getResourceAsStream(RESOURCE_NAME)
            ?.bufferedReader(Charsets.UTF_8)
            ?.use { it.readText() }
            ?: error("Missing canonical plan resource: /$RESOURCE_NAME")
        val adapter = Moshi.Builder()
            .addLast(KotlinJsonAdapterFactory())
            .build()
            .adapter(DefaultPlanContract::class.java)
        val contract = requireNotNull(adapter.fromJson(rawJson)) {
            "Canonical plan resource is empty"
        }
        validate(contract)
        LoadedContract(rawJson = rawJson, contract = contract)
    }

    val SEED_VERSION: Int get() = loadedContract.contract.version
    val PLAN_ID: String get() = loadedContract.contract.planId
    val VERSION_ID: String get() = loadedContract.contract.planVersionId

    suspend fun seed(database: AppDatabase, nowEpochMillis: Long = System.currentTimeMillis()) {
        val seed = create(nowEpochMillis)
        database.withTransaction {
            database.catalogDao().upsertEquipment(seed.equipment)
            database.catalogDao().upsertExercises(seed.exercises)
            database.catalogDao().upsertAlternatives(seed.alternatives)
            database.planDao().replaceDefaultPlan(seed)
        }
    }

    fun create(nowEpochMillis: Long = System.currentTimeMillis()): DefaultPlanSeed {
        val (rawJson, contract) = loadedContract
        val slotContracts = contract.days.flatMap { it.slots }
        val optionOccurrences = slotContracts.flatMap { slot ->
            slot.options.map { option -> OptionOccurrence(slot, option) }
        }

        val equipment = optionOccurrences
            .groupBy { it.option.equipmentId }
            .map { (id, occurrences) ->
                val names = occurrences.map { it.option.equipment }.distinct()
                require(names.size == 1) { "Equipment $id has conflicting names: $names" }
                EquipmentEntity(
                    id = id,
                    name = names.single(),
                    category = equipmentCategory(names.single()),
                    version = contract.version.toLong(),
                    createdAt = nowEpochMillis,
                    updatedAt = nowEpochMillis,
                )
            }

        val exercises = optionOccurrences
            .groupBy { it.option.exerciseId }
            .map { (id, occurrences) ->
                val names = occurrences.map { it.option.exerciseName }.distinct()
                require(names.size == 1) { "Exercise $id has conflicting names: $names" }
                val canonical = occurrences.first()
                ExerciseEntity(
                    id = id,
                    name = names.single(),
                    bodyPart = canonical.slot.muscleGroup,
                    // A globally shared exercise may be prescribed with different machines.
                    // The slot option remains authoritative; avoid inventing one global machine.
                    equipmentId = occurrences.map { it.option.equipmentId }.distinct().singleOrNull(),
                    defaultSets = occurrences.maxOf { it.option.sets },
                    repMin = canonical.option.repMin,
                    repMax = canonical.option.repMax,
                    repUnit = normalizedRepUnit(canonical.option),
                    cues = canonical.slot.cues,
                    commonMistakes = canonical.slot.commonMistakes,
                    definitionVersion = contract.version,
                    version = contract.version.toLong(),
                    createdAt = nowEpochMillis,
                    updatedAt = nowEpochMillis,
                )
            }

        val plan = TrainingPlanEntity(
            id = contract.planId,
            name = contract.name,
            description = contract.description,
            isBuiltIn = true,
            version = contract.version.toLong(),
            createdAt = nowEpochMillis,
            updatedAt = nowEpochMillis,
        )
        val planVersion = PlanVersionEntity(
            id = contract.planVersionId,
            planId = contract.planId,
            versionNumber = contract.version,
            status = contract.status.uppercase(),
            publishedAt = Instant.parse(contract.publishedAt).toEpochMilli(),
            snapshotJson = rawJson,
            version = contract.version.toLong(),
            createdAt = nowEpochMillis,
            updatedAt = nowEpochMillis,
        )

        val days = contract.days.map { day ->
            PlanDayEntity(
                id = day.dayId,
                planVersionId = contract.planVersionId,
                code = PlanCode.valueOf(day.code.uppercase()),
                name = day.name,
                sortOrder = day.order,
                version = contract.version.toLong(),
                createdAt = nowEpochMillis,
                updatedAt = nowEpochMillis,
            )
        }
        val slots = contract.days.flatMap { day ->
            day.slots.map { slot ->
                PlanSlotEntity(
                    id = slot.slotId,
                    planDayId = day.dayId,
                    position = slot.order,
                    bodyPart = slot.muscleGroup,
                    cues = slot.cues,
                    version = contract.version.toLong(),
                    createdAt = nowEpochMillis,
                    updatedAt = nowEpochMillis,
                    deletedAt = if (slot.enabled) null else nowEpochMillis,
                )
            }
        }
        val options = slotContracts.flatMap { slot ->
            slot.options.map { option ->
                PlanSlotOptionEntity(
                    id = option.optionId,
                    planSlotId = slot.slotId,
                    exerciseId = option.exerciseId,
                    equipmentId = option.equipmentId,
                    isPreferred = option.isPrimary,
                    sortOrder = option.order,
                    setCount = option.sets,
                    introSetCount = minOf(
                        slot.adaptationSets ?: contract.adaptationSets,
                        option.sets,
                    ),
                    introWeeks = contract.adaptationWeeks,
                    repMin = option.repMin,
                    repMax = option.repMax,
                    repUnit = normalizedRepUnit(option),
                    rirMin = option.rirMin,
                    rirMax = option.rirMax,
                    version = contract.version.toLong(),
                    createdAt = nowEpochMillis,
                    updatedAt = nowEpochMillis,
                    deletedAt = if (option.enabled) null else nowEpochMillis,
                )
            }
        }
        val alternatives = slotContracts.flatMap { slot ->
            slot.options
                .filterNot { it.isPrimary }
                .map { option ->
                    ExerciseAlternativeEntity(
                        id = option.optionId,
                        exerciseId = slot.primaryExerciseId,
                        alternativeExerciseId = option.exerciseId,
                        sortOrder = option.order,
                        version = contract.version.toLong(),
                        createdAt = nowEpochMillis,
                        updatedAt = nowEpochMillis,
                        deletedAt = if (option.enabled) null else nowEpochMillis,
                    )
                }
        }.distinctBy { it.exerciseId to it.alternativeExerciseId }

        return DefaultPlanSeed(
            plan = plan,
            planVersion = planVersion,
            days = days,
            slots = slots,
            options = options,
            equipment = equipment,
            exercises = exercises,
            alternatives = alternatives,
        )
    }

    private fun normalizedRepUnit(option: DefaultOptionContract): String = when {
        option.perSide && option.repUnit == "reps" -> "reps_per_side"
        else -> option.repUnit
    }

    private fun validate(contract: DefaultPlanContract) {
        require(contract.schemaVersion.isNotBlank()) { "schema_version is required" }
        require(contract.contractVersion.isNotBlank()) { "contract_version is required" }
        require(contract.version > 0) { "version must be positive" }
        require(contract.adaptationWeeks >= 0) { "adaptation_weeks cannot be negative" }
        require(contract.adaptationSets > 0) { "adaptation_sets must be positive" }
        require(contract.targetRir.size == 2 && contract.targetRir[0] <= contract.targetRir[1]) {
            "target_rir must contain an ordered min/max pair"
        }
        uuid(contract.planId, "plan_id")
        uuid(contract.planVersionId, "plan_version_id")
        require(contract.days.map { it.dayId }.distinct().size == contract.days.size) {
            "day_id values must be unique"
        }
        val slotIds = mutableSetOf<String>()
        val optionIds = mutableSetOf<String>()
        contract.days.forEach { day ->
            uuid(day.dayId, "day_id")
            require(runCatching { PlanCode.valueOf(day.code.uppercase()) }.isSuccess) {
                "Unsupported day code: ${day.code}"
            }
            require(day.slots.map { it.order }.distinct().size == day.slots.size) {
                "Day ${day.code} contains duplicate slot orders"
            }
            day.slots.forEach { slot ->
                uuid(slot.slotId, "slot_id")
                require(slotIds.add(slot.slotId)) { "Duplicate slot_id: ${slot.slotId}" }
                require(slot.options.isNotEmpty()) { "Slot ${slot.slotCode} has no options" }
                require(slot.options.count { it.isPrimary } == 1) {
                    "Slot ${slot.slotCode} must have exactly one primary option"
                }
                require(slot.options.first { it.isPrimary }.exerciseId == slot.primaryExerciseId) {
                    "Slot ${slot.slotCode} primary_exercise_id does not match its primary option"
                }
                require(slot.options.map { it.order }.distinct().size == slot.options.size) {
                    "Slot ${slot.slotCode} contains duplicate option orders"
                }
                slot.options.forEach { option ->
                    uuid(option.optionId, "option_id")
                    uuid(option.exerciseId, "exercise_id")
                    uuid(option.equipmentId, "equipment_id")
                    require(optionIds.add(option.optionId)) { "Duplicate option_id: ${option.optionId}" }
                    require(option.sets > 0 && option.repMin > 0 && option.repMax >= option.repMin) {
                        "Invalid prescription for option ${option.optionId}"
                    }
                    require(option.rirMin <= option.rirMax) {
                        "Invalid RIR range for option ${option.optionId}"
                    }
                }
            }
        }
    }

    private fun uuid(value: String, field: String) {
        require(runCatching { UUID.fromString(value) }.isSuccess) { "$field is not a UUID: $value" }
    }

    private fun equipmentCategory(name: String): String = when {
        "龙门架" in name || "绳索" in name || "滑轮" in name -> "绳索器械"
        "杠铃" in name || "EZ" in name -> "自由重量"
        "哑铃" in name || "壶铃" in name -> "自由重量"
        "瑜伽垫" in name || "踏板" in name || "单杠" in name -> "自重与附件"
        else -> "固定器械"
    }
}

private data class LoadedContract(
    val rawJson: String,
    val contract: DefaultPlanContract,
)

private data class OptionOccurrence(
    val slot: DefaultSlotContract,
    val option: DefaultOptionContract,
)

@JsonClass(generateAdapter = true)
internal data class DefaultPlanContract(
    @Json(name = "schema_version") val schemaVersion: String,
    @Json(name = "contract_version") val contractVersion: String,
    @Json(name = "plan_code") val planCode: String,
    @Json(name = "plan_id") val planId: String,
    @Json(name = "plan_version_id") val planVersionId: String,
    val version: Int,
    val status: String,
    val name: String,
    val description: String,
    val goal: String,
    val cycle: List<String>,
    @Json(name = "weekly_strength_target") val weeklyStrengthTarget: Int,
    @Json(name = "minimum_rest_days") val minimumRestDays: Int,
    @Json(name = "fatigue_threshold") val fatigueThreshold: Int,
    @Json(name = "adaptation_weeks") val adaptationWeeks: Int,
    @Json(name = "adaptation_sets") val adaptationSets: Int,
    @Json(name = "target_rir") val targetRir: List<Int>,
    @Json(name = "selection_rule") val selectionRule: String,
    @Json(name = "published_at") val publishedAt: String,
    val days: List<DefaultDayContract>,
)

@JsonClass(generateAdapter = true)
internal data class DefaultDayContract(
    @Json(name = "day_id") val dayId: String,
    val code: String,
    val name: String,
    val order: Int,
    val slots: List<DefaultSlotContract>,
)

@JsonClass(generateAdapter = true)
internal data class DefaultSlotContract(
    @Json(name = "slot_id") val slotId: String,
    @Json(name = "slot_code") val slotCode: String,
    val order: Int,
    @Json(name = "muscle_group") val muscleGroup: String,
    @Json(name = "primary_exercise_id") val primaryExerciseId: String,
    val cues: String,
    @Json(name = "common_mistakes") val commonMistakes: String,
    @Json(name = "adaptation_sets") val adaptationSets: Int? = null,
    val enabled: Boolean = true,
    val options: List<DefaultOptionContract>,
)

@JsonClass(generateAdapter = true)
internal data class DefaultOptionContract(
    @Json(name = "option_id") val optionId: String,
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "exercise_name") val exerciseName: String,
    @Json(name = "equipment_id") val equipmentId: String,
    val equipment: String,
    @Json(name = "is_primary") val isPrimary: Boolean,
    val order: Int,
    val sets: Int,
    @Json(name = "rep_min") val repMin: Int,
    @Json(name = "rep_max") val repMax: Int,
    @Json(name = "rep_unit") val repUnit: String,
    @Json(name = "rest_seconds") val restSeconds: Int,
    @Json(name = "rir_min") val rirMin: Int,
    @Json(name = "rir_max") val rirMax: Int,
    @Json(name = "per_side") val perSide: Boolean = false,
    val enabled: Boolean = true,
)
