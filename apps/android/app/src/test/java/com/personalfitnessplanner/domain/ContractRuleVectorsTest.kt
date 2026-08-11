package com.personalfitnessplanner.domain

import com.google.common.truth.Truth.assertThat
import com.personalfitnessplanner.data.local.PlanCode
import com.squareup.moshi.Json
import com.squareup.moshi.JsonClass
import com.squareup.moshi.Moshi
import com.squareup.moshi.kotlin.reflect.KotlinJsonAdapterFactory
import java.time.LocalDate
import org.junit.Test
import org.junit.runner.RunWith
import org.junit.runners.Parameterized

@RunWith(Parameterized::class)
class RecommendationContractVectorTest(
    @Suppress("unused") private val caseId: String,
    private val vector: RecommendationVector,
) {
    @Test
    fun matchesSharedContractVector() {
        val actual = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = LocalDate.parse(checkNotNull(vector.today)),
                completedWorkouts = vector.completedWorkouts.map { workout ->
                    CompletedWorkout(
                        localDate = LocalDate.parse(workout.localDate),
                        planCode = PlanCode.valueOf(workout.planCode),
                        isFullBody = workout.isFullBody,
                    )
                },
                fatigueScore = vector.fatigueScore,
                weeklyLimit = checkNotNull(vector.weeklyLimit),
                minimumRestDays = vector.minimumRestDays ?: 1,
                fatigueThreshold = vector.fatigueThreshold ?: 8,
            ),
        )

        assertThat(actual.session).isEqualTo(
            RecommendedSession.valueOf(checkNotNull(vector.expected.session)),
        )
        assertThat(actual.nextStrengthDay).isEqualTo(PlanCode.valueOf(vector.expected.nextStrengthDay))
        assertThat(actual.reason).isEqualTo(RecommendationReason.valueOf(vector.expected.reason))
    }

    companion object {
        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun vectors(): Collection<Array<Any>> =
            loadVectorDocument<RecommendationVectorDocument>("recommendation-cases.json")
                .cases
                .filter { it.today != null && it.weeklyLimit != null && it.expected.session != null }
                .map { arrayOf<Any>(it.id, it) }
    }
}

@RunWith(Parameterized::class)
class ProgressionContractVectorTest(
    @Suppress("unused") private val caseId: String,
    private val vector: ProgressionVector,
) {
    @Test
    fun matchesSharedContractVector() {
        val input = checkNotNull(vector.input)
        val expected = checkNotNull(vector.expected)
        val actual = DoubleProgressionEngine.recommend(
            ProgressionInput(
                exerciseId = input.exerciseId,
                currentWeightKg = input.currentWeightKg,
                minimumIncrementKg = input.minimumIncrementKg,
                repMin = input.repMin,
                repMax = input.repMax,
                consecutiveFailedSessions = input.consecutiveFailedSessions,
                sets = input.sets.map { set ->
                    ProgressionSet(
                        reps = set.reps,
                        rir = set.rir,
                        quality = set.quality?.let(MovementQuality::valueOf),
                        pain = set.pain,
                        isWarmup = set.isWarmup,
                        completed = set.completed,
                    )
                },
            ),
        )

        assertThat(actual.action).isEqualTo(
            ProgressionAction.valueOf(checkNotNull(expected.action)),
        )
        assertThat(actual.nextWeightKg).isEqualTo(expected.nextWeightKg)
        assertThat(actual.reason).isEqualTo(ProgressionReason.valueOf(expected.reason))
    }

    companion object {
        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun vectors(): Collection<Array<Any>> =
            loadVectorDocument<ProgressionVectorDocument>("progression-cases.json")
                .cases
                .filter { it.input != null && it.expected?.action != null }
                .map { arrayOf<Any>(it.id, it) }
    }
}

@RunWith(Parameterized::class)
class ProgressionHistoryContractVectorTest(
    @Suppress("unused") private val caseId: String,
    private val vector: ProgressionVector,
) {
    @Test
    fun alternativesDoNotInheritPrimaryExerciseWeight() {
        val query = checkNotNull(vector.query)
        val records = vector.history.mapIndexed { index, record ->
            ExerciseWeightRecord(
                exerciseId = record.exerciseId,
                completedAt = index.toLong(),
                weightKg = record.weightKg,
                reps = 0,
            )
        }

        val actual = ExerciseWeightHistory.latestForExercise(query.exerciseId, records)

        assertThat(actual?.weightKg).isEqualTo(vector.expected?.latestWeightKg)
    }

    companion object {
        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun vectors(): Collection<Array<Any>> =
            loadVectorDocument<ProgressionVectorDocument>("progression-cases.json")
                .cases
                .filter { it.history.isNotEmpty() && it.query != null }
                .map { arrayOf<Any>(it.id, it) }
    }
}

@RunWith(Parameterized::class)
class AdaptationContractVectorTest(
    @Suppress("unused") private val caseId: String,
    private val vector: RecommendationVector,
) {
    @Test
    fun matchesSharedContractVector() {
        val actual = PlanLifecycleRules.effectiveSetCount(
            trainingWeek = checkNotNull(vector.trainingWeek),
            prescribedSets = checkNotNull(vector.prescribedSets),
            adaptationWeeks = checkNotNull(vector.adaptationWeeks),
            adaptationSets = checkNotNull(vector.adaptationSets),
        )

        assertThat(actual).isEqualTo(vector.expected.effectiveSets)
    }

    companion object {
        @JvmStatic
        @Parameterized.Parameters(name = "{0}")
        fun vectors(): Collection<Array<Any>> =
            loadVectorDocument<RecommendationVectorDocument>("recommendation-cases.json")
                .cases
                .filter { it.trainingWeek != null && it.expected.effectiveSets != null }
                .map { arrayOf<Any>(it.id, it) }
    }
}

internal fun recommendationContractVector(id: String): RecommendationVector =
    loadVectorDocument<RecommendationVectorDocument>("recommendation-cases.json")
        .cases
        .single { it.id == id }

private inline fun <reified T> loadVectorDocument(fileName: String): T {
    val json = checkNotNull(
        ContractVectorResource::class.java.classLoader
            ?.getResourceAsStream("contracts/$fileName"),
    ) { "Missing shared contract vector: contracts/$fileName" }
        .bufferedReader(Charsets.UTF_8)
        .use { it.readText() }
    val adapter = Moshi.Builder()
        .addLast(KotlinJsonAdapterFactory())
        .build()
        .adapter(T::class.java)
    return checkNotNull(adapter.fromJson(json)) { "Empty shared contract vector: $fileName" }
}

private object ContractVectorResource

@JsonClass(generateAdapter = false)
data class RecommendationVectorDocument(
    val cases: List<RecommendationVector>,
)

@JsonClass(generateAdapter = false)
data class RecommendationVector(
    val id: String,
    val today: String? = null,
    @Json(name = "completed_workouts") val completedWorkouts: List<RecommendationWorkoutVector> = emptyList(),
    @Json(name = "fatigue_score") val fatigueScore: Int? = null,
    @Json(name = "weekly_limit") val weeklyLimit: Int? = null,
    @Json(name = "min_rest_days") val minimumRestDays: Int? = null,
    @Json(name = "fatigue_threshold") val fatigueThreshold: Int? = null,
    @Json(name = "training_week") val trainingWeek: Int? = null,
    @Json(name = "prescribed_sets") val prescribedSets: Int? = null,
    @Json(name = "adaptation_weeks") val adaptationWeeks: Int? = null,
    @Json(name = "adaptation_sets") val adaptationSets: Int? = null,
    @Json(name = "existing_workout") val existingWorkout: PlanVersionReferenceVector? = null,
    @Json(name = "new_assignment") val newAssignment: PlanVersionReferenceVector? = null,
    val expected: RecommendationExpectedVector = RecommendationExpectedVector(),
)

@JsonClass(generateAdapter = false)
data class RecommendationWorkoutVector(
    @Json(name = "local_date") val localDate: String,
    @Json(name = "plan_code") val planCode: String,
    @Json(name = "is_full_body") val isFullBody: Boolean = true,
)

@JsonClass(generateAdapter = false)
data class RecommendationExpectedVector(
    val session: String? = null,
    @Json(name = "next_strength_day") val nextStrengthDay: String = "A",
    val reason: String = "FIRST_STRENGTH_SESSION",
    @Json(name = "effective_sets") val effectiveSets: Int? = null,
    @Json(name = "existing_workout_plan_version_id") val existingWorkoutPlanVersionId: String? = null,
    @Json(name = "next_workout_plan_version_id") val nextWorkoutPlanVersionId: String? = null,
)

@JsonClass(generateAdapter = false)
data class PlanVersionReferenceVector(
    @Json(name = "plan_version_id") val planVersionId: String,
)

@JsonClass(generateAdapter = false)
data class ProgressionVectorDocument(
    val cases: List<ProgressionVector>,
)

@JsonClass(generateAdapter = false)
data class ProgressionVector(
    val id: String,
    val input: ProgressionInputVector? = null,
    val history: List<ProgressionHistoryVector> = emptyList(),
    val query: ProgressionHistoryQueryVector? = null,
    val expected: ProgressionExpectedVector? = null,
)

@JsonClass(generateAdapter = false)
data class ProgressionInputVector(
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "current_weight_kg") val currentWeightKg: Double,
    @Json(name = "minimum_increment_kg") val minimumIncrementKg: Double,
    @Json(name = "rep_min") val repMin: Int,
    @Json(name = "rep_max") val repMax: Int,
    @Json(name = "consecutive_failed_sessions") val consecutiveFailedSessions: Int = 0,
    val sets: List<ProgressionSetVector>,
)

@JsonClass(generateAdapter = false)
data class ProgressionSetVector(
    val reps: Int,
    val rir: Int? = null,
    val quality: String? = null,
    val pain: Boolean = false,
    @Json(name = "is_warmup") val isWarmup: Boolean = false,
    val completed: Boolean = true,
)

@JsonClass(generateAdapter = false)
data class ProgressionExpectedVector(
    val action: String? = null,
    @Json(name = "next_weight_kg") val nextWeightKg: Double = 0.0,
    val reason: String = "KEEP_BUILDING_REPS",
    @Json(name = "latest_weight_kg") val latestWeightKg: Double? = null,
)

@JsonClass(generateAdapter = false)
data class ProgressionHistoryVector(
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "source_option_id") val sourceOptionId: String? = null,
    @Json(name = "weight_kg") val weightKg: Double,
)

@JsonClass(generateAdapter = false)
data class ProgressionHistoryQueryVector(
    @Json(name = "exercise_id") val exerciseId: String,
    @Json(name = "source_option_id") val sourceOptionId: String? = null,
)
