package com.personalfitnessplanner.ui

import com.personalfitnessplanner.ui.model.AppDestination
import com.personalfitnessplanner.ui.model.ExportFormat
import com.personalfitnessplanner.ui.model.HistoryFilterUi
import com.personalfitnessplanner.ui.model.OnboardingConfig
import com.personalfitnessplanner.ui.model.SettingsKey
import com.personalfitnessplanner.ui.model.WorkoutSetDraft

data class OnboardingCallbacks(
    val onConfigChanged: (OnboardingConfig) -> Unit = {},
    val onSubmit: (OnboardingConfig) -> Unit = {},
    val onDownloadPlan: () -> Unit = {},
    val onUseLocalMode: () -> Unit = {},
)

data class HomeCallbacks(
    val onStartWorkout: () -> Unit = {},
    val onMarkRest: () -> Unit = {},
    val onSwitchToCardio: () -> Unit = {},
    val onSync: () -> Unit = {},
)

data class TodayCallbacks(
    val onStartWorkout: () -> Unit = {},
    val onExerciseStart: (String) -> Unit = {},
    val onExerciseSkip: (String) -> Unit = {},
    val onExerciseSwap: (String) -> Unit = {},
)

data class WorkoutExecutionCallbacks(
    val onSetChanged: (String, WorkoutSetDraft) -> Unit = { _, _ -> },
    val onSetComplete: (String) -> Unit = {},
    val onEditPreviousSet: (String) -> Unit = {},
    val onFinishWorkout: () -> Unit = {},
    val onEndWorkoutEarly: () -> Unit = {},
)

data class HistoryCallbacks(
    val onFilterChanged: (HistoryFilterUi) -> Unit = {},
    val onOpen: (String) -> Unit = {},
    val onExport: (ExportFormat) -> Unit = {},
    val onEdit: (String) -> Unit = {},
    val onDelete: (String) -> Unit = {},
)

data class ExerciseLibraryCallbacks(
    val onSearch: (String) -> Unit = {},
    val onBodyPartChanged: (String) -> Unit = {},
    val onOpen: (String) -> Unit = {},
    val onNoteSave: (String, String) -> Unit = { _, _ -> },
)

data class SettingsCallbacks(
    val onSettingChanged: (SettingsKey, String) -> Unit = { _, _ -> },
    val onSync: () -> Unit = {},
    val onExport: (ExportFormat) -> Unit = {},
    val onLocalBackup: () -> Unit = {},
    val onClearCache: () -> Unit = {},
    val onLogout: () -> Unit = {},
)

/**
 * Stable UI event surface. A ViewModel can map these callbacks to intents without
 * any UI package depending on Room, Retrofit, or Android services.
 */
data class FitnessAppCallbacks(
    val onNavigate: (AppDestination) -> Unit = {},
    val onOnboardingChanged: (OnboardingConfig) -> Unit = {},
    val onOnboardingSubmit: (OnboardingConfig) -> Unit = {},
    val onDownloadPlan: () -> Unit = {},
    val onUseLocalMode: () -> Unit = {},
    val onStartWorkout: () -> Unit = {},
    val onMarkRest: () -> Unit = {},
    val onSwitchToCardio: () -> Unit = {},
    val onSync: () -> Unit = {},
    val onExerciseStart: (String) -> Unit = {},
    val onExerciseSkip: (String) -> Unit = {},
    val onExerciseSwap: (String) -> Unit = {},
    val onSetChanged: (String, WorkoutSetDraft) -> Unit = { _, _ -> },
    val onSetComplete: (String) -> Unit = {},
    val onEditPreviousSet: (String) -> Unit = {},
    val onFinishWorkout: () -> Unit = {},
    val onEndWorkoutEarly: () -> Unit = {},
    val onHistoryFilter: (HistoryFilterUi) -> Unit = {},
    val onHistoryOpen: (String) -> Unit = {},
    val onHistoryExport: (ExportFormat) -> Unit = {},
    val onHistoryEdit: (String) -> Unit = {},
    val onHistoryDelete: (String) -> Unit = {},
    val onExerciseSearch: (String) -> Unit = {},
    val onExerciseBodyPartChanged: (String) -> Unit = {},
    val onExerciseOpen: (String) -> Unit = {},
    val onExerciseNoteSave: (String, String) -> Unit = { _, _ -> },
    val onSettingChanged: (SettingsKey, String) -> Unit = { _, _ -> },
    val onSettingsExport: (ExportFormat) -> Unit = {},
    val onLocalBackup: () -> Unit = {},
    val onClearCache: () -> Unit = {},
    val onLogout: () -> Unit = {},
)
