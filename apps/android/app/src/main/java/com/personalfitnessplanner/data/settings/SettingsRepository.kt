package com.personalfitnessplanner.data.settings

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.emptyPreferences
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.personalfitnessplanner.BuildConfig
import com.personalfitnessplanner.data.remote.DynamicBaseUrlInterceptor
import java.io.IOException
import java.time.ZoneId
import java.util.Base64
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

enum class WeightUnit { KG, LB }
enum class DarkMode { SYSTEM, LIGHT, DARK }

data class AppSettings(
    val apiBaseUrl: String = normalizedBaseUrl(BuildConfig.DEFAULT_API_BASE_URL),
    val weightUnit: WeightUnit = WeightUnit.KG,
    val timeZone: String = ZoneId.systemDefault().id,
    val trainingDays: Set<Int> = setOf(1, 3, 5),
    val restTimerSeconds: Int = 90,
    val darkMode: DarkMode = DarkMode.SYSTEM,
    val backgroundSyncEnabled: Boolean = true,
    val onboardingComplete: Boolean = false,
    val localMode: Boolean = false,
    /** User-owned notes keyed by the immutable server exercise ID. */
    val exerciseNotes: Map<String, String> = emptyMap(),
)

private val Context.personalFitnessSettingsDataStore by preferencesDataStore(
    name = "personal_fitness_settings",
)

class SettingsRepository internal constructor(
    private val dataStore: DataStore<Preferences>,
    private val defaults: AppSettings,
) {
    constructor(
        context: Context,
        defaults: AppSettings = AppSettings(),
    ) : this(context.applicationContext.personalFitnessSettingsDataStore, defaults)

    val settings: Flow<AppSettings> = dataStore.data
        .catch { error ->
            if (error is IOException) emit(emptyPreferences()) else throw error
        }
        .map(::toSettings)
        .distinctUntilChanged()

    suspend fun current(): AppSettings = settings.first()

    suspend fun setApiBaseUrl(value: String) {
        dataStore.edit { it[Keys.API_BASE_URL] = normalizedBaseUrl(value) }
    }

    suspend fun setWeightUnit(value: WeightUnit) {
        dataStore.edit { it[Keys.WEIGHT_UNIT] = value.name }
    }

    suspend fun setTimeZone(value: String) {
        val normalized = ZoneId.of(value).id
        dataStore.edit { it[Keys.TIME_ZONE] = normalized }
    }

    /** ISO day numbers: Monday=1 through Sunday=7. */
    suspend fun setTrainingDays(value: Set<Int>) {
        require(value.isNotEmpty()) { "At least one training day is required" }
        require(value.all { it in 1..7 }) { "Training days must be in 1..7" }
        dataStore.edit { preferences ->
            preferences[Keys.TRAINING_DAYS] = value.map(Int::toString).toSet()
        }
    }

    suspend fun setRestTimerSeconds(value: Int) {
        require(value in MIN_REST_SECONDS..MAX_REST_SECONDS) {
            "Rest timer must be between $MIN_REST_SECONDS and $MAX_REST_SECONDS seconds"
        }
        dataStore.edit { it[Keys.REST_TIMER_SECONDS] = value }
    }

    suspend fun setDarkMode(value: DarkMode) {
        dataStore.edit { it[Keys.DARK_MODE] = value.name }
    }

    suspend fun setBackgroundSyncEnabled(value: Boolean) {
        dataStore.edit { it[Keys.BACKGROUND_SYNC] = value }
    }

    suspend fun setOnboardingComplete(value: Boolean) {
        dataStore.edit { it[Keys.ONBOARDING_COMPLETE] = value }
    }

    suspend fun setLocalMode(value: Boolean) {
        dataStore.edit { it[Keys.LOCAL_MODE] = value }
    }

    suspend fun setExerciseNote(exerciseId: String, note: String) {
        val normalizedId = exerciseId.trim()
        require(normalizedId.isNotEmpty()) { "Exercise ID must not be blank" }
        val normalizedNote = note.trim()
        dataStore.edit { preferences ->
            val notes = (preferences[Keys.EXERCISE_NOTES]
                ?.let(::decodeExerciseNotes)
                ?: defaults.exerciseNotes).toMutableMap()
            if (normalizedNote.isEmpty()) {
                notes.remove(normalizedId)
            } else {
                notes[normalizedId] = normalizedNote
            }
            if (notes.isEmpty()) {
                preferences.remove(Keys.EXERCISE_NOTES)
            } else {
                preferences[Keys.EXERCISE_NOTES] = encodeExerciseNotes(notes)
            }
        }
    }

    suspend fun reset() {
        dataStore.edit { it.clear() }
    }

    private fun toSettings(preferences: Preferences): AppSettings = AppSettings(
        apiBaseUrl = preferences[Keys.API_BASE_URL]
            ?.let(::safeBaseUrl)
            ?: defaults.apiBaseUrl,
        weightUnit = preferences[Keys.WEIGHT_UNIT]
            ?.let { runCatching { WeightUnit.valueOf(it) }.getOrNull() }
            ?: defaults.weightUnit,
        timeZone = preferences[Keys.TIME_ZONE]
            ?.let { runCatching { ZoneId.of(it).id }.getOrNull() }
            ?: defaults.timeZone,
        trainingDays = preferences[Keys.TRAINING_DAYS]
            ?.mapNotNull { it.toIntOrNull() }
            ?.filter { it in 1..7 }
            ?.toSet()
            ?.takeIf { it.isNotEmpty() }
            ?: defaults.trainingDays,
        restTimerSeconds = preferences[Keys.REST_TIMER_SECONDS]
            ?.coerceIn(MIN_REST_SECONDS, MAX_REST_SECONDS)
            ?: defaults.restTimerSeconds,
        darkMode = preferences[Keys.DARK_MODE]
            ?.let { runCatching { DarkMode.valueOf(it) }.getOrNull() }
            ?: defaults.darkMode,
        backgroundSyncEnabled = preferences[Keys.BACKGROUND_SYNC] ?: defaults.backgroundSyncEnabled,
        onboardingComplete = preferences[Keys.ONBOARDING_COMPLETE] ?: defaults.onboardingComplete,
        localMode = preferences[Keys.LOCAL_MODE] ?: defaults.localMode,
        exerciseNotes = preferences[Keys.EXERCISE_NOTES]
            ?.let(::decodeExerciseNotes)
            ?: defaults.exerciseNotes,
    )

    private fun safeBaseUrl(value: String): String = runCatching { normalizedBaseUrl(value) }
        .getOrDefault(defaults.apiBaseUrl)

    private object Keys {
        val API_BASE_URL = stringPreferencesKey("api_base_url")
        val WEIGHT_UNIT = stringPreferencesKey("weight_unit")
        val TIME_ZONE = stringPreferencesKey("time_zone")
        val TRAINING_DAYS = stringSetPreferencesKey("training_days")
        val REST_TIMER_SECONDS = intPreferencesKey("rest_timer_seconds")
        val DARK_MODE = stringPreferencesKey("dark_mode")
        val BACKGROUND_SYNC = booleanPreferencesKey("background_sync_enabled")
        val ONBOARDING_COMPLETE = booleanPreferencesKey("onboarding_complete")
        val LOCAL_MODE = booleanPreferencesKey("local_mode")
        val EXERCISE_NOTES = stringSetPreferencesKey("exercise_notes")
    }

    companion object {
        const val MIN_REST_SECONDS = 15
        const val MAX_REST_SECONDS = 3_600
    }
}

fun normalizedBaseUrl(value: String): String =
    DynamicBaseUrlInterceptor.parseBaseUrl(value.trim()).toString()

private const val EXERCISE_NOTE_SEPARATOR = ':'

/** URL-safe Base64 keeps arbitrary Unicode/newline content compatible with Preferences StringSet. */
internal fun encodeExerciseNotes(notes: Map<String, String>): Set<String> = notes.asSequence()
    .filter { (exerciseId, note) -> exerciseId.isNotBlank() && note.isNotBlank() }
    .map { (exerciseId, note) ->
        "${exerciseId.encodeExerciseNotePart()}$EXERCISE_NOTE_SEPARATOR${note.encodeExerciseNotePart()}"
    }
    .toSet()

internal fun decodeExerciseNotes(entries: Set<String>): Map<String, String> = buildMap {
    entries.forEach { entry ->
        val separatorIndex = entry.indexOf(EXERCISE_NOTE_SEPARATOR)
        if (separatorIndex <= 0 || separatorIndex == entry.lastIndex) return@forEach
        val exerciseId = entry.substring(0, separatorIndex).decodeExerciseNotePart() ?: return@forEach
        val note = entry.substring(separatorIndex + 1).decodeExerciseNotePart() ?: return@forEach
        if (exerciseId.isNotBlank() && note.isNotBlank()) put(exerciseId, note)
    }
}

private fun String.encodeExerciseNotePart(): String = Base64.getUrlEncoder()
    .withoutPadding()
    .encodeToString(toByteArray(Charsets.UTF_8))

private fun String.decodeExerciseNotePart(): String? = runCatching {
    Base64.getUrlDecoder().decode(this).toString(Charsets.UTF_8)
}.getOrNull()
