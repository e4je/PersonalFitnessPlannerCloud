package com.personalfitnessplanner.data.remote

import com.personalfitnessplanner.data.local.PlanVersionEntity
import com.squareup.moshi.JsonAdapter
import com.squareup.moshi.Moshi
import com.squareup.moshi.Types
import com.squareup.moshi.kotlin.reflect.KotlinJsonAdapterFactory

data class PlanRecommendationRules(
    val weeklyLimit: Int = DEFAULT_WEEKLY_LIMIT,
    val minimumRestDays: Int = DEFAULT_MINIMUM_REST_DAYS,
    val fatigueThreshold: Int = DEFAULT_FATIGUE_THRESHOLD,
) {
    companion object {
        const val DEFAULT_WEEKLY_LIMIT = 3
        const val DEFAULT_MINIMUM_REST_DAYS = 1
        const val DEFAULT_FATIGUE_THRESHOLD = 8
    }
}

/** Reads both the canonical bundled snapshot and the complete remote DTO snapshot. */
object PlanRuleSnapshotParser {
    private val adapter: JsonAdapter<Map<String, Any?>> by lazy(LazyThreadSafetyMode.PUBLICATION) {
        Moshi.Builder()
            .addLast(KotlinJsonAdapterFactory())
            .build()
            .adapter(
                Types.newParameterizedType(
                    Map::class.java,
                    String::class.java,
                    Any::class.java,
                ),
            )
    }

    fun parse(snapshotJson: String): PlanRecommendationRules {
        val root = snapshotJson.takeIf(String::isNotBlank)
            ?.let { runCatching { adapter.fromJson(it) }.getOrNull() }
            ?: return PlanRecommendationRules()
        val embedded = (root["snapshot_json"] as? String)
            ?.takeIf(String::isNotBlank)
            ?.let { runCatching { adapter.fromJson(it) }.getOrNull() }
        val sources = buildList {
            add(root)
            root.stringKeyMap("rules")?.let(::add)
            if (embedded != null) {
                add(embedded)
                embedded.stringKeyMap("rules")?.let(::add)
            }
        }

        return PlanRecommendationRules(
            weeklyLimit = sources.firstNumber(
                "weekly_frequency",
                "weekly_strength_target",
                "weekly_limit",
            )?.takeIf { it > 0 } ?: PlanRecommendationRules.DEFAULT_WEEKLY_LIMIT,
            minimumRestDays = sources.firstNumber(
                "min_rest_days",
                "minimum_rest_days",
            )?.takeIf { it >= 0 } ?: PlanRecommendationRules.DEFAULT_MINIMUM_REST_DAYS,
            fatigueThreshold = sources.firstNumber("fatigue_threshold")
                ?.takeIf { it in 0..10 }
                ?: PlanRecommendationRules.DEFAULT_FATIGUE_THRESHOLD,
        )
    }

    private fun Map<String, Any?>.stringKeyMap(key: String): Map<String, Any?>? {
        val raw = this[key] as? Map<*, *> ?: return null
        return buildMap {
            raw.forEach { (rawKey, value) ->
                if (rawKey is String) put(rawKey, value)
            }
        }
    }

    private fun List<Map<String, Any?>>.firstNumber(vararg keys: String): Int? =
        asSequence().flatMap { source -> keys.asSequence().map { source[it] } }
            .mapNotNull { value ->
                when (value) {
                    is Number -> value.toInt()
                    is String -> value.toIntOrNull()
                    else -> null
                }
            }
            .firstOrNull()
}

fun PlanVersionEntity.recommendationRules(): PlanRecommendationRules =
    PlanRuleSnapshotParser.parse(snapshotJson)
