package com.personalfitnessplanner.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.FitnessCenter
import androidx.compose.material.icons.rounded.History
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.rounded.MenuBook
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import com.personalfitnessplanner.ui.model.AppDestination
import com.personalfitnessplanner.ui.model.FitnessAppUiState
import com.personalfitnessplanner.ui.model.ThemeMode
import com.personalfitnessplanner.ui.screens.ExerciseLibraryScreen
import com.personalfitnessplanner.ui.screens.HistoryScreen
import com.personalfitnessplanner.ui.screens.HomeScreen
import com.personalfitnessplanner.ui.screens.OnboardingScreen
import com.personalfitnessplanner.ui.screens.SettingsScreen
import com.personalfitnessplanner.ui.screens.TodayWorkoutScreen
import com.personalfitnessplanner.ui.screens.WorkoutExecutionScreen
import com.personalfitnessplanner.ui.theme.FitnessPlannerTheme

@Composable
fun FitnessApp(
    state: FitnessAppUiState = FitnessAppUiState.preview(),
    callbacks: FitnessAppCallbacks = FitnessAppCallbacks(),
    modifier: Modifier = Modifier,
) {
    val systemDark = isSystemInDarkTheme()
    val darkTheme = when (state.settings.themeMode) {
        ThemeMode.System -> systemDark
        ThemeMode.Light -> false
        ThemeMode.Dark -> true
    }
    FitnessPlannerTheme(darkTheme = darkTheme) {
        FitnessAppContent(state = state, callbacks = callbacks, modifier = modifier)
    }
}

@Composable
fun FitnessAppContent(
    state: FitnessAppUiState,
    callbacks: FitnessAppCallbacks,
    modifier: Modifier = Modifier,
) {
    var destinationName by rememberSaveable { mutableStateOf(state.currentDestination.name) }
    val destination = remember(destinationName) {
        AppDestination.entries.firstOrNull { it.name == destinationName } ?: AppDestination.Home
    }

    LaunchedEffect(state.currentDestination) {
        destinationName = state.currentDestination.name
    }

    fun navigate(target: AppDestination) {
        destinationName = target.name
        callbacks.onNavigate(target)
    }

    BackHandler(enabled = destination == AppDestination.WorkoutExecution) {
        navigate(AppDestination.Today)
    }

    val showNavigation = destination in TopLevelDestinations
    Scaffold(
        modifier = modifier,
        bottomBar = {
            if (showNavigation) {
                FitnessNavigationBar(selected = destination, onNavigate = ::navigate)
            }
        },
    ) { innerPadding ->
        val screenModifier = Modifier.padding(innerPadding)
        when (destination) {
            AppDestination.Onboarding -> OnboardingScreen(
                state = state.onboarding,
                callbacks = OnboardingCallbacks(
                    onConfigChanged = callbacks.onOnboardingChanged,
                    onSubmit = {
                        callbacks.onOnboardingSubmit(it)
                        navigate(AppDestination.Home)
                    },
                    onDownloadPlan = callbacks.onDownloadPlan,
                    onUseLocalMode = {
                        callbacks.onUseLocalMode()
                        navigate(AppDestination.Home)
                    },
                ),
                modifier = screenModifier,
            )
            AppDestination.Home -> HomeScreen(
                state = state.home,
                callbacks = HomeCallbacks(
                    onStartWorkout = {
                        callbacks.onStartWorkout()
                        navigate(AppDestination.Today)
                    },
                    onMarkRest = callbacks.onMarkRest,
                    onSwitchToCardio = callbacks.onSwitchToCardio,
                    onSync = callbacks.onSync,
                ),
                modifier = screenModifier,
            )
            AppDestination.Today -> TodayWorkoutScreen(
                state = state.today,
                callbacks = TodayCallbacks(
                    onStartWorkout = {
                        callbacks.onStartWorkout()
                        navigate(AppDestination.WorkoutExecution)
                    },
                    onExerciseStart = {
                        callbacks.onExerciseStart(it)
                        navigate(AppDestination.WorkoutExecution)
                    },
                    onExerciseSkip = callbacks.onExerciseSkip,
                    onExerciseSwap = callbacks.onExerciseSwap,
                ),
                modifier = screenModifier,
            )
            AppDestination.WorkoutExecution -> WorkoutExecutionScreen(
                state = state.execution,
                callbacks = WorkoutExecutionCallbacks(
                    onSetChanged = callbacks.onSetChanged,
                    onSetComplete = callbacks.onSetComplete,
                    onEditPreviousSet = callbacks.onEditPreviousSet,
                    onFinishWorkout = {
                        callbacks.onFinishWorkout()
                        navigate(AppDestination.History)
                    },
                    onEndWorkoutEarly = {
                        callbacks.onEndWorkoutEarly()
                        navigate(AppDestination.Today)
                    },
                ),
                modifier = screenModifier,
                onBack = { navigate(AppDestination.Today) },
            )
            AppDestination.History -> HistoryScreen(
                state = state.history,
                callbacks = HistoryCallbacks(
                    onFilterChanged = callbacks.onHistoryFilter,
                    onOpen = callbacks.onHistoryOpen,
                    onExport = callbacks.onHistoryExport,
                    onEdit = callbacks.onHistoryEdit,
                    onDelete = callbacks.onHistoryDelete,
                ),
                modifier = screenModifier,
            )
            AppDestination.ExerciseLibrary -> ExerciseLibraryScreen(
                state = state.library,
                callbacks = ExerciseLibraryCallbacks(
                    onSearch = callbacks.onExerciseSearch,
                    onBodyPartChanged = callbacks.onExerciseBodyPartChanged,
                    onOpen = callbacks.onExerciseOpen,
                    onNoteSave = callbacks.onExerciseNoteSave,
                ),
                modifier = screenModifier,
            )
            AppDestination.Settings -> SettingsScreen(
                state = state.settings,
                callbacks = SettingsCallbacks(
                    onSettingChanged = callbacks.onSettingChanged,
                    onSync = callbacks.onSync,
                    onExport = callbacks.onSettingsExport,
                    onLocalBackup = callbacks.onLocalBackup,
                    onClearCache = callbacks.onClearCache,
                    onLogout = callbacks.onLogout,
                ),
                modifier = screenModifier,
            )
        }
    }
}

private data class NavigationItem(
    val destination: AppDestination,
    val icon: ImageVector,
)

private val NavigationItems = listOf(
    NavigationItem(AppDestination.Home, Icons.Rounded.Home),
    NavigationItem(AppDestination.Today, Icons.Rounded.FitnessCenter),
    NavigationItem(AppDestination.History, Icons.Rounded.History),
    NavigationItem(AppDestination.ExerciseLibrary, Icons.Rounded.MenuBook),
    NavigationItem(AppDestination.Settings, Icons.Rounded.Settings),
)

private val TopLevelDestinations = NavigationItems.map { it.destination }.toSet()

@Composable
private fun FitnessNavigationBar(
    selected: AppDestination,
    onNavigate: (AppDestination) -> Unit,
) {
    NavigationBar {
        NavigationItems.forEach { item ->
            NavigationBarItem(
                selected = selected == item.destination,
                onClick = { onNavigate(item.destination) },
                icon = { Icon(item.icon, contentDescription = null) },
                label = { Text(item.destination.label) },
                alwaysShowLabel = true,
            )
        }
    }
}
