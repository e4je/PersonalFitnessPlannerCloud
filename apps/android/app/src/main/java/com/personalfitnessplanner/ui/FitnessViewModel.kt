package com.personalfitnessplanner.ui

import android.content.Intent
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.personalfitnessplanner.AppContainer
import com.personalfitnessplanner.BuildConfig
import com.personalfitnessplanner.data.export.WorkoutExportFormat
import com.personalfitnessplanner.data.local.EquipmentEntity
import com.personalfitnessplanner.data.local.ExerciseEntity
import com.personalfitnessplanner.data.local.PlanCode
import com.personalfitnessplanner.data.local.PlanSlotOptionEntity
import com.personalfitnessplanner.data.local.PlanVersionWithDays
import com.personalfitnessplanner.data.local.SetQuality
import com.personalfitnessplanner.data.local.WorkoutSessionEntity
import com.personalfitnessplanner.data.local.WorkoutSessionWithSets
import com.personalfitnessplanner.data.local.WorkoutSetEntity
import com.personalfitnessplanner.data.local.WorkoutStatus
import com.personalfitnessplanner.data.remote.LoginRequestDto
import com.personalfitnessplanner.data.remote.LogoutRequestDto
import com.personalfitnessplanner.data.remote.PlanRecommendationRules
import com.personalfitnessplanner.data.remote.recommendationRules
import com.personalfitnessplanner.data.repository.PendingAccountSwitchException
import com.personalfitnessplanner.data.repository.WorkoutSetInput
import com.personalfitnessplanner.data.security.AuthTokens
import com.personalfitnessplanner.data.settings.AppSettings
import com.personalfitnessplanner.data.settings.normalizedBaseUrl
import com.personalfitnessplanner.data.settings.DarkMode as StoredDarkMode
import com.personalfitnessplanner.data.settings.WeightUnit as StoredWeightUnit
import com.personalfitnessplanner.domain.CompletedWorkout
import com.personalfitnessplanner.domain.DoubleProgressionEngine
import com.personalfitnessplanner.domain.MovementQuality
import com.personalfitnessplanner.domain.ProgressionAction
import com.personalfitnessplanner.domain.ProgressionInput
import com.personalfitnessplanner.domain.ProgressionSet
import com.personalfitnessplanner.domain.RecommendationInput
import com.personalfitnessplanner.domain.RecommendedSession
import com.personalfitnessplanner.domain.TrainingRecommendationEngine
import com.personalfitnessplanner.sync.SyncResult
import com.personalfitnessplanner.ui.model.AlternativeExerciseUi
import com.personalfitnessplanner.ui.model.AppDestination
import com.personalfitnessplanner.ui.model.ExerciseSlotUi
import com.personalfitnessplanner.ui.model.ExportFormat
import com.personalfitnessplanner.ui.model.FitnessAppUiState
import com.personalfitnessplanner.ui.model.HistoryFilterUi
import com.personalfitnessplanner.ui.model.HistorySessionUi
import com.personalfitnessplanner.ui.model.LibraryExerciseUi
import com.personalfitnessplanner.ui.model.OnboardingConfig
import com.personalfitnessplanner.ui.model.SettingsKey
import com.personalfitnessplanner.ui.model.SyncStatus
import com.personalfitnessplanner.ui.model.ThemeMode as UiThemeMode
import com.personalfitnessplanner.ui.model.TodayWorkoutUiState
import com.personalfitnessplanner.ui.model.TrendPointUi
import com.personalfitnessplanner.ui.model.WeightUnit as UiWeightUnit
import com.personalfitnessplanner.ui.model.WorkoutExecutionUiState
import com.personalfitnessplanner.ui.model.WorkoutSetDraft
import com.personalfitnessplanner.ui.model.WorkoutSetUi
import java.io.File
import java.net.URI
import java.time.Duration
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit
import java.time.temporal.TemporalAdjusters
import java.util.Locale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

sealed interface FitnessUiEffect {
    data class Share(val intent: Intent, val title: String) : FitnessUiEffect
    data class Message(val text: String) : FitnessUiEffect
}

/** Connects parameter-only Compose screens to the offline-first repositories. */
class FitnessViewModel(private val container: AppContainer) : ViewModel() {
    private val _uiState = MutableStateFlow(FitnessAppUiState.preview())
    val uiState = _uiState.asStateFlow()

    private val effectChannel = Channel<FitnessUiEffect>(Channel.BUFFERED)
    val effects = effectChannel.receiveAsFlow()

    private var currentSettings = AppSettings()
    private var startupResolved = false
    private var allExercises: List<ExerciseEntity> = emptyList()
    private var allEquipment: List<EquipmentEntity> = emptyList()
    private var historyRecords: List<WorkoutSessionWithSets> = emptyList()
    private var pendingSyncCount = 0
    private var observedAuthenticated = container.tokenStore.read() != null
    private var suppressAuthenticationLossUntilLogin = false
    private val setWriteMutex = Mutex()
    private val completedSetClicks = mutableSetOf<String>()

    val callbacks = FitnessAppCallbacks(
        onNavigate = ::navigate,
        onOnboardingChanged = ::onOnboardingChanged,
        onOnboardingSubmit = ::loginAndContinue,
        onDownloadPlan = ::downloadCloudOverwrite,
        onUseLocalMode = ::useLocalMode,
        onStartWorkout = ::startOrResumeWorkout,
        onMarkRest = { overrideRecommendation("恢复 · 主动恢复", "已手动设为恢复日") },
        onSwitchToCardio = { overrideRecommendation("有氧 · 低强度", "已手动改为有氧") },
        onSync = { synchronize(fullResync = false) },
        onUploadLocal = ::uploadLocal,
        onDownloadCloudOverwrite = ::downloadCloudOverwrite,
        onExerciseStart = { startOrResumeWorkout() },
        onExerciseSkip = ::skipExercise,
        onExerciseSwap = ::swapExercise,
        onSetChanged = ::saveSetDraft,
        onSetComplete = ::completeSet,
        onEditPreviousSet = { completedSetClicks.remove(it) },
        onFinishWorkout = { finishWorkout(early = false) },
        onEndWorkoutEarly = { finishWorkout(early = true) },
        onHistoryFilter = ::applyHistoryFilter,
        onHistoryOpen = ::openHistory,
        onHistoryExport = ::exportHistory,
        onHistoryEdit = ::openHistory,
        onHistoryDelete = ::deleteHistory,
        onExerciseSearch = ::searchExercises,
        onExerciseBodyPartChanged = ::filterBodyPart,
        onExerciseOpen = { emitMessage("动作定义由计划服务器管理；客户端仅保存个人器械备注。") },
        onExerciseNoteSave = ::saveExerciseNote,
        onSettingChanged = ::changeSetting,
        onSettingsExport = ::exportHistory,
        onLocalBackup = ::createBackup,
        onClearCache = ::clearCache,
        onLogout = ::logout,
    )

    init {
        observeAuthentication()
        observeSettings()
        observeCatalog()
        observeHistory()
        observePendingSync()
        viewModelScope.launch {
            runCatching { container.ensureLocalData() }
                .onFailure { emitMessage("初始化本地计划失败：${it.message}") }
            rebuildHistoryAndHome(historyRecords.map { it.session })
            container.fitnessRepository.activeWorkout()?.let(::showWorkout)
        }
    }

    private fun observeAuthentication() = viewModelScope.launch {
        container.tokenStore.tokens.collect { tokens ->
            if (tokens != null) {
                observedAuthenticated = true
                suppressAuthenticationLossUntilLogin = false
            } else if (observedAuthenticated && !suppressAuthenticationLossUntilLogin) {
                observedAuthenticated = false
                container.syncWorkScheduler.cancelAllSync()
                container.settingsRepository.setLocalMode(false)
                container.settingsRepository.setOnboardingComplete(false)
                val old = _uiState.value
                _uiState.value = old.copy(
                    currentDestination = AppDestination.Onboarding,
                    onboarding = old.onboarding.copy(
                        config = old.onboarding.config.copy(password = ""),
                        isSubmitting = false,
                        serverReachable = false,
                        errorMessage = "登录状态已失效，请重新登录。",
                    ),
                    settings = old.settings.copy(accountName = "未登录"),
                )
            } else {
                observedAuthenticated = false
            }
        }
    }

    private fun observeSettings() = viewModelScope.launch {
        container.settingsRepository.settings.collect { settings ->
            currentSettings = settings
            val old = _uiState.value
            val destination = if (!startupResolved) {
                startupResolved = true
                when {
                    !settings.onboardingComplete -> AppDestination.Onboarding
                    container.fitnessRepository.activeWorkout() != null -> AppDestination.WorkoutExecution
                    else -> AppDestination.Home
                }
            } else {
                old.currentDestination
            }
            _uiState.value = old.copy(
                currentDestination = destination,
                onboarding = old.onboarding.copy(
                    config = old.onboarding.config.copy(
                        apiBaseUrl = settings.apiBaseUrl,
                        weightUnit = settings.weightUnit.toUi(),
                        timezone = settings.timeZone,
                        trainingDays = settings.trainingDays.mapTo(linkedSetOf(), ::dayLabel),
                    ),
                ),
                settings = old.settings.copy(
                    apiBaseUrl = settings.apiBaseUrl,
                    accountName = if (container.tokenStore.read() == null) "本地模式" else "已登录账号",
                    timezone = settings.timeZone,
                    weightUnit = settings.weightUnit.toUi(),
                    trainingDays = settings.trainingDays.sorted().joinToString("、", transform = ::dayLabel),
                    restSeconds = settings.restTimerSeconds,
                    themeMode = settings.darkMode.toUi(),
                    autoSync = settings.backgroundSyncEnabled,
                    appVersion = BuildConfig.VERSION_NAME,
                ),
            )
            rebuildLibrary()
        }
    }

    private fun observeCatalog() {
        viewModelScope.launch {
            container.fitnessRepository.observeExercises().collect { exercises ->
                allExercises = exercises
                rebuildLibrary()
                refreshTodayPlan()
            }
        }
        viewModelScope.launch {
            container.database.catalogDao().observeEquipment().collect { equipment ->
                allEquipment = equipment
                refreshTodayPlan()
            }
        }
    }

    private fun observeHistory() = viewModelScope.launch {
        container.fitnessRepository.observeWorkoutHistory().collect { sessions ->
            historyRecords = sessions.mapNotNull { container.fitnessRepository.getWorkout(it.id) }
            rebuildHistoryAndHome(sessions)
        }
    }

    private fun observePendingSync() = viewModelScope.launch {
        container.fitnessRepository.observePendingSyncCount().collect { count ->
            pendingSyncCount = count
            val status = if (count == 0) SyncStatus.Synced else SyncStatus.Offline
            val message = if (count == 0) "本地与云端无待处理项" else "$count 项等待联网同步"
            _uiState.value = _uiState.value.copy(
                home = _uiState.value.home.copy(syncStatus = status, syncMessage = message),
                settings = _uiState.value.settings.copy(syncStatus = status, lastSync = message),
            )
        }
    }

    private fun navigate(destination: AppDestination) {
        _uiState.value = _uiState.value.copy(currentDestination = destination)
    }

    private fun onOnboardingChanged(config: OnboardingConfig) {
        _uiState.value = _uiState.value.copy(
            onboarding = _uiState.value.onboarding.copy(config = config, errorMessage = null),
        )
    }

    private fun loginAndContinue(config: OnboardingConfig) = launchAction {
        _uiState.value = _uiState.value.copy(
            onboarding = _uiState.value.onboarding.copy(isSubmitting = true, errorMessage = null),
        )
        var authenticatedTokens: AuthTokens? = null
        var replacedPreviousServerIdentity = false
        try {
            saveOnboardingConfig(config)
            // No old-account worker may race the identity preflight or the atomic Room switch.
            container.syncWorkScheduler.cancelAllSync()
            val cachedUserId = container.fitnessRepository.currentUserId()
            val response = container.apiClientFactory.apiService.login(
                LoginRequestDto(config.account.trim(), config.password, "Android"),
            )
            val refreshToken = requireNotNull(response.refreshToken?.takeIf(String::isNotBlank)) {
                "服务器未返回刷新令牌"
            }
            val tokens = AuthTokens(
                accessToken = response.accessToken,
                refreshToken = refreshToken,
                expiresAtEpochSeconds = response.expiresAtEpochSeconds
                    ?: response.expiresInSeconds?.let { System.currentTimeMillis() / 1_000 + it },
                tokenType = "Bearer".also {
                    check(response.tokenType.equals("Bearer", ignoreCase = true)) {
                        "Unsupported authentication scheme"
                    }
                },
            )
            // Bootstrap with an explicit transient bearer token. The process-wide store remains on
            // the old identity until Room has either rejected the switch (pending data) or replaced
            // the old server scope and installed this complete bootstrap in one transaction.
            val bootstrap = container.apiClientFactory.preflightBootstrap(tokens)
            container.syncLocalStore.replaceServerOwnedData(bootstrap)
            val serverUserId = bootstrap.user?.id
            replacedPreviousServerIdentity = serverUserId != null && serverUserId != cachedUserId
            if (replacedPreviousServerIdentity) clearAccountScopedMemory()
            container.tokenStore.write(tokens)
            authenticatedTokens = tokens
            observedAuthenticated = true
            suppressAuthenticationLossUntilLogin = false
            _uiState.value = _uiState.value.copy(
                onboarding = _uiState.value.onboarding.copy(
                    config = _uiState.value.onboarding.config.copy(password = ""),
                ),
            )
            container.settingsRepository.setLocalMode(false)
            container.settingsRepository.setOnboardingComplete(true)
            container.ensureLocalData()
            rebuildHistoryAndHome(historyRecords.map { it.session })
            _uiState.value = _uiState.value.copy(
                currentDestination = AppDestination.Home,
                onboarding = _uiState.value.onboarding.copy(
                    isSubmitting = false,
                    serverReachable = true,
                    errorMessage = null,
                ),
            )
            downloadCloudOverwrite()
        } catch (error: Exception) {
            // Login/preflight failures never commit the candidate token, so the old account can
            // still synchronize. Clear only if Room already changed identity and the matching new
            // token failed to become current.
            if (replacedPreviousServerIdentity &&
                container.tokenStore.read()?.accessToken != authenticatedTokens?.accessToken
            ) {
                suppressAuthenticationLossUntilLogin = true
                observedAuthenticated = false
                container.tokenStore.clear()
            }
            val errorMessage = if (error is PendingAccountSwitchException) {
                "无法切换账号：${error.message}。"
            } else {
                "登录失败：${error.message ?: "后端不可达"}。可使用内置计划进入本地模式。"
            }
            _uiState.value = _uiState.value.copy(
                currentDestination = AppDestination.Onboarding,
                onboarding = _uiState.value.onboarding.copy(
                    isSubmitting = false,
                    serverReachable = false,
                    config = _uiState.value.onboarding.config.copy(password = ""),
                    errorMessage = errorMessage,
                ),
            )
        }
    }

    private fun useLocalMode() = launchAction {
        val config = _uiState.value.onboarding.config
        var releasedServerIdentity = false
        try {
            container.syncWorkScheduler.cancelAllSync()
            container.syncLocalStore.releaseServerIdentityForLocalMode()
            releasedServerIdentity = true
            // Persist device preferences only after the old account is safely releasable. A
            // pending-data rejection must not clear its token or mutate its settings.
            saveOnboardingConfig(config)
            clearAccountScopedMemory()
            suppressAuthenticationLossUntilLogin = true
            observedAuthenticated = false
            container.tokenStore.clear()
            container.settingsRepository.setLocalMode(true)
            container.settingsRepository.setOnboardingComplete(true)
            container.ensureLocalData()
            rebuildHistoryAndHome(emptyList())
            _uiState.value = _uiState.value.copy(currentDestination = AppDestination.Home)
        } catch (error: Exception) {
            if (releasedServerIdentity) {
                suppressAuthenticationLossUntilLogin = true
                observedAuthenticated = false
                container.tokenStore.clear()
            }
            val message = if (error is PendingAccountSwitchException) {
                "无法进入本地模式：${error.message}。"
            } else {
                "进入本地模式失败：${error.message ?: "本地数据不可用"}。"
            }
            _uiState.value = _uiState.value.copy(
                currentDestination = AppDestination.Onboarding,
                onboarding = _uiState.value.onboarding.copy(
                    isSubmitting = false,
                    config = _uiState.value.onboarding.config.copy(password = ""),
                    errorMessage = message,
                ),
            )
        }
    }

    /** Removes account-derived in-memory state before any different identity can reach Home. */
    private fun clearAccountScopedMemory() {
        historyRecords = emptyList()
        allExercises = emptyList()
        allEquipment = emptyList()
        pendingSyncCount = 0
        completedSetClicks.clear()
        val state = _uiState.value
        _uiState.value = state.copy(
            home = state.home.copy(
                recommendation = "正在加载",
                recommendationReason = "",
                planName = "暂无训练计划",
                planVersion = "--",
                completedThisWeek = 0,
                daysSinceLastWorkout = 0,
                nextWorkout = "",
                fatigueScore = 0,
                syncStatus = SyncStatus.Offline,
                syncMessage = "等待账号数据",
                hasActiveWorkout = false,
            ),
            today = state.today.copy(
                workoutLabel = "暂无训练",
                planName = "暂无训练计划",
                planVersion = "--",
                weekNote = "",
                estimatedMinutes = 0,
                exercises = emptyList(),
            ),
            execution = state.execution.copy(
                sessionId = "",
                workoutLabel = "",
                exercisePosition = "",
                exerciseName = "",
                equipment = "",
                target = "",
                cue = "",
                setupNote = "",
                elapsedTime = "00:00",
                restSecondsRemaining = 0,
                isResting = false,
                autosaveMessage = "",
                sets = emptyList(),
            ),
            history = state.history.copy(
                summary = "暂无训练记录",
                trendExercise = "暂无已完成正式组",
                trend = emptyList(),
                sessions = emptyList(),
            ),
            library = state.library.copy(exercises = emptyList()),
            settings = state.settings.copy(
                accountName = "未登录",
                syncStatus = SyncStatus.Offline,
                lastSync = "尚未同步",
            ),
        )
    }

    private suspend fun saveOnboardingConfig(config: OnboardingConfig) {
        setApiBaseUrlSafely(config.apiBaseUrl)
        container.settingsRepository.setWeightUnit(config.weightUnit.toStored())
        container.settingsRepository.setTimeZone(config.timezone)
        container.settingsRepository.setTrainingDays(config.trainingDays.mapNotNull(::dayNumber).toSet().ifEmpty { setOf(1, 3, 5) })
    }

    private fun startOrResumeWorkout() = launchAction {
        runCatching {
            container.ensureLocalData()
            val requested = if (_uiState.value.today.workoutLabel.startsWith("训练 B")) PlanCode.B else PlanCode.A
            val choices = _uiState.value.today.exercises
            val skippedSlotIds = choices
                .filter { it.status == "已跳过" }
                .mapTo(linkedSetOf()) { it.id }
            val exerciseSelections = choices
                .filterNot { it.id in skippedSlotIds }
                .associate { it.id to it.selectedExerciseId }
            container.fitnessRepository.startOrResumeWorkout(
                requestedDay = requested,
                localDate = LocalDate.now(ZoneId.of(currentSettings.timeZone)),
                timezone = currentSettings.timeZone,
                exerciseSelections = exerciseSelections,
                skippedSlotIds = skippedSlotIds,
            )
        }.onSuccess { workout ->
            showWorkout(workout)
        }.onFailure { emitMessage("无法开始训练：${it.message}") }
    }

    private fun saveSetDraft(setId: String, draft: WorkoutSetDraft) = launchAction {
        setWriteMutex.withLock {
            val sessionId = _uiState.value.execution.sessionId
            runCatching {
                container.fitnessRepository.saveSet(sessionId, setId, draft.toInput())
            }.onSuccess(::showWorkout)
                .onFailure { emitMessage("自动保存失败：${it.message}") }
        }
    }

    private fun completeSet(setId: String) = launchAction {
        setWriteMutex.withLock {
            if (!completedSetClicks.add(setId)) return@withLock
            val execution = _uiState.value.execution
            val draft = execution.sets.firstOrNull { it.id == setId }?.draft ?: return@withLock
            runCatching {
                container.fitnessRepository.completeSet(execution.sessionId, setId, draft.toInput())
            }.onSuccess { workout ->
                showWorkout(workout)
                container.restTimerScheduler.start(
                    durationSeconds = currentSettings.restTimerSeconds,
                    timerId = setId,
                    title = "休息结束",
                    message = "下一组可以开始了",
                )
                _uiState.value = _uiState.value.copy(
                    execution = _uiState.value.execution.copy(
                        isResting = true,
                        restSecondsRemaining = currentSettings.restTimerSeconds,
                        autosaveMessage = "已自动保存；休息计时已启动",
                    ),
                )
            }.onFailure {
                completedSetClicks.remove(setId)
                emitMessage("完成组失败：${it.message}")
            }
        }
    }

    private fun finishWorkout(early: Boolean) = launchAction {
        val sessionId = _uiState.value.execution.sessionId
        runCatching {
            if (early) container.fitnessRepository.endWorkoutEarly(sessionId)
            else container.fitnessRepository.finishWorkout(sessionId)
        }.onSuccess {
            completedSetClicks.clear()
            container.restTimerScheduler.cancelAll()
            emitMessage(if (early) "训练已中途结束并保存" else "训练完成，记录已保存")
        }.onFailure { emitMessage("保存训练失败：${it.message}") }
    }

    private fun showWorkout(workout: WorkoutSessionWithSets) {
        val groups = workout.sets.groupBy { it.planSlotId ?: it.exerciseId }.values.toList()
        val activeIndex = groups.indexOfFirst { group -> group.any { !it.completed } }
            .let { if (it < 0) (groups.lastIndex).coerceAtLeast(0) else it }
        val group = groups.getOrNull(activeIndex).orEmpty()
        val exercise = allExercises.firstOrNull { it.id == group.firstOrNull()?.exerciseId }
        val elapsed = Duration.ofMillis((System.currentTimeMillis() - workout.session.startedAt).coerceAtLeast(0))
        _uiState.value = _uiState.value.copy(
            execution = WorkoutExecutionUiState(
                sessionId = workout.session.id,
                workoutLabel = "训练 ${workout.session.planDayCode?.name ?: "自定义"}",
                exercisePosition = "动作 ${activeIndex + 1} / ${groups.size.coerceAtLeast(1)}",
                exerciseName = exercise?.name ?: "训练动作",
                equipment = "器械编号 ${group.firstOrNull()?.equipmentId?.take(8) ?: "待填写"}",
                target = "${group.size} 组 · ${exercise?.repMin ?: 8}–${exercise?.repMax ?: 12} 次",
                cue = exercise?.cues ?: "动作稳定，保留 2～3 次余力。",
                setupNote = "可在动作库保存个人座椅、角度和器械备注",
                elapsedTime = "%02d:%02d".format(elapsed.toMinutes(), elapsed.seconds % 60),
                restSecondsRemaining = 0,
                isResting = false,
                autosaveMessage = "已自动保存到本机",
                sets = group.sortedBy { it.setNumber }.map { set ->
                    WorkoutSetUi(
                        id = set.id,
                        number = set.setNumber,
                        draft = WorkoutSetDraft(
                            weight = set.weightKg?.let { value -> if (value % 1.0 == 0.0) value.toInt().toString() else value.toString() }.orEmpty(),
                            reps = set.reps?.toString().orEmpty(),
                            isWarmup = set.isWarmup,
                            rir = set.rir ?: 2,
                            quality = set.quality.toUiQuality(),
                            pain = if (set.pain) 1 else 0,
                            note = set.notes.orEmpty(),
                        ),
                        completed = set.completed,
                        isEditable = true,
                    )
                },
            ),
        )
    }

    private suspend fun rebuildHistoryAndHome(sessions: List<WorkoutSessionEntity>) {
        val active = sessions.firstOrNull { it.status == WorkoutStatus.IN_PROGRESS }
        val completed = sessions.filter { it.status == WorkoutStatus.COMPLETED && it.planDayCode != null }
        val today = LocalDate.now(ZoneId.of(currentSettings.timeZone))
        val currentUserId = container.fitnessRepository.currentUserId()
        val currentPlan = container.fitnessRepository.currentPlan()
        val planRules = currentPlan?.planVersion?.recommendationRules()
            ?: PlanRecommendationRules()
        val fatigueScore = container.database.readinessDao()
            .forDate(
                currentUserId,
                today.toString(),
            )
            ?.fatigueScore
            ?: _uiState.value.home.fatigueScore
        val recommendation = TrainingRecommendationEngine.recommend(
            RecommendationInput(
                today = today,
                completedWorkouts = completed.mapNotNull { session ->
                    runCatching {
                        CompletedWorkout(LocalDate.parse(session.localDate), checkNotNull(session.planDayCode), session.isFullBody)
                    }.getOrNull()
                },
                fatigueScore = fatigueScore,
                weeklyLimit = planRules.weeklyLimit,
                minimumRestDays = planRules.minimumRestDays,
                fatigueThreshold = planRules.fatigueThreshold,
            ),
        )
        val weekStart = today.with(TemporalAdjusters.previousOrSame(java.time.DayOfWeek.MONDAY))
        val completedThisWeek = completed.count { runCatching { !LocalDate.parse(it.localDate).isBefore(weekStart) }.getOrDefault(false) }
        val lastDate = completed.firstOrNull()?.localDate?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
        val recommendationText = when (recommendation.session) {
            RecommendedSession.A -> "A · 胸部优先"
            RecommendedSession.B -> "B · 背部优先"
            RecommendedSession.RECOVERY -> "恢复 · 主动恢复"
            RecommendedSession.CARDIO -> "有氧 · 低强度"
            RecommendedSession.REST -> "休息"
        }
        val state = _uiState.value
        _uiState.value = state.copy(
            home = state.home.copy(
                dateText = today.format(DateTimeFormatter.ofPattern("M 月 d 日 · EEEE", Locale.CHINA)),
                recommendation = recommendationText,
                recommendationReason = recommendation.reason.name.toReasonText(),
                planName = currentPlan?.let { planName(it.planVersion.planId) } ?: "暂无训练计划",
                planVersion = currentPlan?.let { "v${it.planVersion.versionNumber}" } ?: "--",
                fatigueScore = fatigueScore,
                completedThisWeek = completedThisWeek,
                daysSinceLastWorkout = lastDate?.let { ChronoUnit.DAYS.between(it, today).toInt() } ?: 0,
                nextWorkout = "${recommendation.nextStrengthDay.name} · 下一次力量训练",
                hasActiveWorkout = active != null,
            ),
            today = currentPlan?.let { buildToday(it, recommendation.nextStrengthDay, today) }
                ?: state.today.copy(exercises = emptyList(), planName = "暂无训练计划", planVersion = "--"),
            history = buildHistoryState(
                filter = state.history.filter,
                today = today,
                totalCompleted = completed.size,
                completedThisWeek = completedThisWeek,
            ),
        )
    }

    private suspend fun buildToday(
        plan: PlanVersionWithDays,
        code: PlanCode,
        today: LocalDate = LocalDate.now(ZoneId.of(currentSettings.timeZone)),
    ): TodayWorkoutUiState {
        val day = plan.days.firstOrNull { it.day.code == code }
            ?: return TodayWorkoutUiState(
                dateText = "今天 · ${today.format(DateTimeFormatter.ofPattern("M 月 d 日"))}",
                workoutLabel = "训练 ${code.name}",
                planName = planName(plan.planVersion.planId),
                planVersion = "v${plan.planVersion.versionNumber}",
                weekNote = "当前计划中没有 ${code.name} 训练日",
                estimatedMinutes = 0,
                exercises = emptyList(),
            )
        val equipment = allEquipment.associateBy { it.id }
        val exercises = allExercises.associateBy { it.id }
        val assignment = container.database.planDao().activeAssignment(
            container.fitnessRepository.currentUserId(),
        )
        val assignmentWeek = assignment?.startLocalDate
            ?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
            ?.let { start -> (ChronoUnit.DAYS.between(start, today).coerceAtLeast(0) / 7 + 1).toInt() }
            ?: 1
        val slots = day.slots
            .filter { it.slot.deletedAt == null }
            .sortedBy { it.slot.position }
            .mapNotNull { relation ->
            val slot = relation.slot
            val options = relation.options.filter { it.deletedAt == null }.sortedBy { it.sortOrder }
            val preferred = options.firstOrNull { it.isPreferred } ?: options.firstOrNull()
                ?: return@mapNotNull null
            val preferredExercise = exercises[preferred.exerciseId] ?: return@mapNotNull null
            val history = container.fitnessRepository.weightHistory(
                exerciseId = preferred.exerciseId,
                limit = 20,
            )
            val prescribedSets = if (assignmentWeek <= preferred.introWeeks) {
                preferred.introSetCount
            } else {
                preferred.setCount
            }.coerceAtLeast(1)
            ExerciseSlotUi(
                id = slot.id,
                selectedExerciseId = preferred.exerciseId,
                order = slot.position,
                bodyPart = slot.bodyPart,
                exerciseName = preferredExercise.name,
                equipment = equipment[preferred.equipmentId]?.name ?: "自重",
                sets = prescribedSets,
                reps = "${preferred.repMin}–${preferred.repMax} ${if (preferred.repUnit == "seconds") "秒" else "次"}",
                alternatives = options.filterNot { it.id == preferred.id }.map { option ->
                    AlternativeExerciseUi(
                        id = option.exerciseId,
                        name = exercises[option.exerciseId]?.name ?: "替代动作",
                        equipment = equipment[option.equipmentId]?.name ?: "自重",
                    )
                },
                cue = slot.cues,
                previousPerformance = previousPerformance(history),
                suggestedWeight = suggestedWeight(preferred, history),
                setupNote = equipment[preferred.equipmentId]?.let(::equipmentSetup)
                    ?: "自重动作；请记录需要的辅助设置",
            )
        }
        return TodayWorkoutUiState(
            dateText = "今天 · ${today.format(DateTimeFormatter.ofPattern("M 月 d 日"))}",
            workoutLabel = "训练 ${code.name} · ${day.day.name}",
            planName = planName(plan.planVersion.planId),
            planVersion = "v${plan.planVersion.versionNumber}",
            weekNote = "第 $assignmentWeek 周 · ${if (assignmentWeek <= 2) "执行入门组数" else "执行完整组数"}",
            estimatedMinutes = (slots.sumOf { it.sets } * 3).coerceAtLeast(1),
            exercises = slots,
        )
    }

    private suspend fun refreshTodayPlan() {
        val plan = container.fitnessRepository.currentPlan() ?: return
        val code = when {
            _uiState.value.home.nextWorkout.startsWith("B") -> PlanCode.B
            _uiState.value.home.recommendation.startsWith("B") -> PlanCode.B
            else -> PlanCode.A
        }
        _uiState.value = _uiState.value.copy(today = buildToday(plan, code))
    }

    private suspend fun planName(planId: String): String =
        container.database.planDao().planName(planId).orEmpty().ifBlank { "当前训练计划" }

    private fun previousPerformance(history: List<WorkoutSetEntity>): String {
        val latest = history.firstOrNull() ?: return "暂无该动作历史，首训请从轻重量开始"
        val sets = history.takeWhile { it.sessionId == latest.sessionId }
        return sets.joinToString(separator = " / ") { set ->
            val load = set.weightKg?.let(::formatWeight)?.plus(" kg") ?: "自重"
            "$load × ${set.reps ?: set.durationSeconds ?: "--"}"
        }
    }

    private fun suggestedWeight(
        option: PlanSlotOptionEntity,
        history: List<WorkoutSetEntity>,
    ): String {
        val latest = history.firstOrNull()
            ?: return "首训：选择可保留 2–3 RIR 的轻重量"
        val currentWeight = latest.weightKg
            ?: return "自重/时长动作：保持动作质量并逐步增加次数"
        val sessionSets = history.takeWhile { it.sessionId == latest.sessionId }
        val result = DoubleProgressionEngine.recommend(
            ProgressionInput(
                exerciseId = option.exerciseId,
                currentWeightKg = currentWeight,
                minimumIncrementKg = if (currentWeight < 20.0) 1.0 else 2.5,
                repMin = option.repMin.coerceAtLeast(1),
                repMax = option.repMax.coerceAtLeast(option.repMin.coerceAtLeast(1)),
                sets = sessionSets.map { set ->
                    ProgressionSet(
                        reps = set.reps ?: 0,
                        rir = set.rir,
                        quality = when (set.quality) {
                            SetQuality.GOOD -> MovementQuality.GOOD
                            SetQuality.FAIR -> MovementQuality.FAIR
                            SetQuality.POOR -> MovementQuality.POOR
                            null -> null
                        },
                        pain = set.pain,
                        isWarmup = set.isWarmup,
                        completed = set.completed,
                    )
                },
            ),
        )
        val action = when (result.action) {
            ProgressionAction.INCREASE -> "达标，建议加重至"
            ProgressionAction.HOLD -> "建议保持"
            ProgressionAction.DECREASE -> "建议降至"
        }
        return "$action ${formatWeight(result.nextWeightKg)} kg"
    }

    private fun equipmentSetup(equipment: EquipmentEntity): String = listOfNotNull(
        equipment.name,
        equipment.brand?.takeIf(String::isNotBlank),
        equipment.model?.takeIf(String::isNotBlank),
        equipment.notes?.takeIf(String::isNotBlank),
    ).joinToString(" · ")

    private fun applyHistoryFilter(filter: HistoryFilterUi) {
        val today = LocalDate.now(ZoneId.of(currentSettings.timeZone))
        val completed = historyRecords.filter { it.session.status == WorkoutStatus.COMPLETED }
        val weekStart = today.with(TemporalAdjusters.previousOrSame(java.time.DayOfWeek.MONDAY))
        val completedThisWeek = completed.count {
            runCatching { !LocalDate.parse(it.session.localDate).isBefore(weekStart) }.getOrDefault(false)
        }
        _uiState.value = _uiState.value.copy(
            history = buildHistoryState(filter, today, completed.size, completedThisWeek),
        )
    }

    private fun buildHistoryState(
        filter: HistoryFilterUi,
        today: LocalDate,
        totalCompleted: Int,
        completedThisWeek: Int,
    ): com.personalfitnessplanner.ui.model.HistoryUiState {
        val selectedExerciseId = filter.exercise.takeUnless { it == "全部动作" }
            ?.let { selected -> allExercises.firstOrNull { it.id == selected || it.name == selected }?.id ?: selected }
        val filtered = filterHistoryRecords(
            records = historyRecords,
            filter = filter,
            today = today,
            exerciseId = selectedExerciseId,
        )
        val trendExerciseId = selectedExerciseId ?: filtered.asSequence()
            .filter { it.session.status == WorkoutStatus.COMPLETED }
            .flatMap { it.sets.asSequence() }
            .filter { it.completed && !it.isWarmup }
            .maxByOrNull { it.completedAt ?: 0L }
            ?.exerciseId
        val trend = trendExerciseId?.let { realTrendPoints(filtered, it) }.orEmpty()
        val trendName = trendExerciseId?.let { id -> allExercises.firstOrNull { it.id == id }?.name }
        return _uiState.value.history.copy(
            filter = filter,
            summary = "筛选后 ${filtered.size} 条 · 共完成 $totalCompleted 次 · 本周 $completedThisWeek 次",
            trendExercise = trendName?.let { "$it · 最近完成组" } ?: "暂无已完成正式组",
            trend = trend,
            sessions = filtered.map(::historySessionUi),
        )
    }

    private fun historySessionUi(record: WorkoutSessionWithSets): HistorySessionUi {
        val volume = record.sets.sumOf { (it.weightKg ?: 0.0) * (it.reps ?: 0) }
        val minutes = record.session.completedAt
            ?.let { (it - record.session.startedAt).coerceAtLeast(0) / 60_000 }
            ?: 0
        return HistorySessionUi(
            id = record.session.id,
            date = record.session.localDate,
            workoutType = record.session.planDayCode?.name ?: "自定义",
            duration = "$minutes 分钟",
            completedSets = record.sets.count { it.completed },
            totalVolume = "%.0f kg".format(volume),
            status = if (pendingSyncCount > 0) "待同步" else "已同步",
            syncDetail = if (pendingSyncCount > 0) "离线记录将在联网后重试" else null,
        )
    }

    private fun rebuildLibrary() {
        val current = _uiState.value.library
        val filtered = allExercises.filter { exercise ->
            (current.query.isBlank() || exercise.name.contains(current.query, ignoreCase = true)) &&
                (current.selectedBodyPart == "全部" || exercise.bodyPart.contains(current.selectedBodyPart))
        }
        _uiState.value = _uiState.value.copy(
            library = current.copy(
                exercises = filtered.map { exercise ->
                    LibraryExerciseUi(
                        id = exercise.id,
                        name = exercise.name,
                        bodyPart = exercise.bodyPart,
                        equipment = exercise.equipmentId?.let { "器械 ${it.take(8)}" } ?: "自重",
                        defaultPrescription = "${exercise.defaultSets} × ${exercise.repMin}–${exercise.repMax}",
                        cue = exercise.cues,
                        commonMistakes = exercise.commonMistakes,
                        alternatives = "查看训练卡中的替代动作",
                        version = "v${exercise.definitionVersion}",
                        personalEquipmentNote = currentSettings.exerciseNotes[exercise.id].orEmpty(),
                    )
                },
            ),
        )
    }

    private fun searchExercises(query: String) {
        _uiState.value = _uiState.value.copy(library = _uiState.value.library.copy(query = query))
        rebuildLibrary()
    }

    private fun filterBodyPart(bodyPart: String) {
        _uiState.value = _uiState.value.copy(library = _uiState.value.library.copy(selectedBodyPart = bodyPart))
        rebuildLibrary()
    }

    private fun saveExerciseNote(exerciseId: String, note: String) = launchAction {
        runCatching { container.settingsRepository.setExerciseNote(exerciseId, note) }
            .onSuccess { emitMessage(if (note.isBlank()) "个人器械备注已清除" else "个人器械备注已保存") }
            .onFailure { emitMessage("保存备注失败：${it.message}") }
    }

    private fun skipExercise(id: String) {
        _uiState.value = _uiState.value.copy(
            today = _uiState.value.today.copy(
                exercises = _uiState.value.today.exercises.map { if (it.id == id) it.copy(status = "已跳过") else it },
            ),
        )
    }

    private fun swapExercise(id: String) = launchAction {
        val item = _uiState.value.today.exercises.firstOrNull { it.id == id } ?: return@launchAction
        val next = item.alternatives.firstOrNull() ?: return@launchAction
        val plan = container.fitnessRepository.currentPlan() ?: return@launchAction
        val option = plan.days.asSequence()
            .flatMap { it.slots.asSequence() }
            .firstOrNull { it.slot.id == id }
            ?.options
            ?.firstOrNull { it.exerciseId == next.id }
            ?: return@launchAction
        val history = container.fitnessRepository.weightHistory(exerciseId = next.id, limit = 20)
        val activeWorkout = container.fitnessRepository.activeWorkout()
        if (activeWorkout?.sets?.any { it.planSlotId == id } == true) {
            runCatching {
                container.fitnessRepository.swapExercise(activeWorkout.session.id, id, next.id)
            }.onSuccess(::showWorkout)
                .onFailure {
                    emitMessage("更换动作失败：${it.message}")
                    return@launchAction
                }
        }
        val previousChoice = item.selectedExerciseId.takeIf(String::isNotBlank)?.let {
            AlternativeExerciseUi(it, item.exerciseName, item.equipment)
        }
        val remaining = item.alternatives.drop(1).toMutableList().apply {
            previousChoice?.let { add(it) }
        }
        val updated = item.copy(
            selectedExerciseId = next.id,
            exerciseName = next.name,
            equipment = next.equipment,
            sets = option.introSetCount.coerceAtLeast(1),
            reps = "${option.repMin}–${option.repMax} ${if (option.repUnit == "seconds") "秒" else "次"}",
            alternatives = remaining,
            previousPerformance = previousPerformance(history),
            suggestedWeight = suggestedWeight(option, history),
            status = "已更换动作",
        )
        _uiState.value = _uiState.value.copy(
            today = _uiState.value.today.copy(
                exercises = _uiState.value.today.exercises.map { if (it.id == id) updated else it },
            ),
        )
    }

    private fun openHistory(id: String) = launchAction {
        container.fitnessRepository.getWorkout(id)?.let { workout ->
            showWorkout(workout)
            navigate(AppDestination.WorkoutExecution)
        } ?: emitMessage("训练记录不存在")
    }

    private fun deleteHistory(id: String) = launchAction {
        runCatching { container.fitnessRepository.softDeleteWorkout(id) }
            .onSuccess { emitMessage("训练记录已软删除，可由云端审计恢复") }
            .onFailure { emitMessage("删除失败：${it.message}") }
    }

    private fun exportHistory(format: ExportFormat) = launchAction(Dispatchers.IO) {
        runCatching {
            val target = if (format == ExportFormat.CSV) WorkoutExportFormat.CSV else WorkoutExportFormat.JSON
            val file = container.workoutExportManager.export(historyRecords, target)
            FitnessUiEffect.Share(
                container.workoutExportManager.shareIntent(file, target),
                "分享训练记录",
            )
        }.onSuccess { effectChannel.send(it) }
            .onFailure { emitMessage("导出失败：${it.message}") }
    }

    private fun createBackup() = launchAction(Dispatchers.IO) {
        runCatching { container.workoutExportManager.localBackup(historyRecords) }
            .onSuccess { emitMessage("本地备份已保存：${it.name}") }
            .onFailure { emitMessage("备份失败：${it.message}") }
    }

    private fun clearCache() = launchAction(Dispatchers.IO) {
        val exportDirectory = File(container.applicationContext.cacheDir, "exports")
        exportDirectory.listFiles()?.forEach { file -> if (file.isFile) file.delete() }
        emitMessage("导出缓存已清理；训练记录未删除")
    }

    private fun changeSetting(key: SettingsKey, value: String) = launchAction {
        runCatching {
            when (key) {
                SettingsKey.ApiBaseUrl -> setApiBaseUrlSafely(value)
                SettingsKey.Timezone -> container.settingsRepository.setTimeZone(value)
                SettingsKey.WeightUnit -> container.settingsRepository.setWeightUnit(UiWeightUnit.valueOf(value).toStored())
                SettingsKey.TrainingDays -> container.settingsRepository.setTrainingDays(
                    parseTrainingDayCsv(value),
                )
                SettingsKey.RestSeconds -> container.settingsRepository.setRestTimerSeconds(value.toInt())
                SettingsKey.ThemeMode -> container.settingsRepository.setDarkMode(UiThemeMode.valueOf(value).toStored())
                SettingsKey.AutoSync -> container.settingsRepository.setBackgroundSyncEnabled(value.toBooleanStrict())
                SettingsKey.LocalBackup -> createBackup()
                SettingsKey.ClearCache -> clearCache()
            }
        }.onFailure { emitMessage("设置无效：${it.message}") }
    }

    private suspend fun setApiBaseUrlSafely(value: String) {
        val normalized = normalizedBaseUrl(value)
        if (apiOrigin(currentSettings.apiBaseUrl) != apiOrigin(normalized)) {
            // Clear before persisting the new origin so a process death cannot carry an old
            // origin's bearer token into the next process.
            container.syncWorkScheduler.cancelAllSync()
            container.tokenStore.clear()
            val old = _uiState.value
            _uiState.value = old.copy(
                currentDestination = AppDestination.Onboarding,
                onboarding = old.onboarding.copy(
                    config = old.onboarding.config.copy(apiBaseUrl = normalized, password = ""),
                    isSubmitting = false,
                    serverReachable = false,
                    errorMessage = "服务器地址已更改，请重新登录；旧账号的本地记录会保留到身份核对完成。",
                ),
            )
        }
        container.settingsRepository.setApiBaseUrl(normalized)
    }

    private fun synchronize(fullResync: Boolean) = runSyncOperation(
        message = if (fullResync) "正在重新同步…" else "正在同步…",
    ) {
        if (fullResync) container.syncCoordinator.fullResync() else container.syncCoordinator.manualSync()
    }

    private fun uploadLocal() = runSyncOperation("正在上传本地记录…") {
        container.syncCoordinator.uploadLocal()
    }

    private fun downloadCloudOverwrite() = runSyncOperation("正在下载云端计划…") {
        container.syncCoordinator.downloadCloudOverwrite()
    }

    private fun runSyncOperation(
        message: String,
        operation: suspend () -> SyncResult,
    ) = launchAction {
        if (currentSettings.localMode || container.tokenStore.read() == null) {
            emitMessage("当前为本地模式；登录后可同步云端。")
            return@launchAction
        }
        updateSyncUi(SyncStatus.Syncing, message)
        when (val result = operation()) {
            is SyncResult.Success -> {
                updateSyncUi(SyncStatus.Synced, "已推送 ${result.pushedCount}，拉取 ${result.pulledCount}")
                rebuildHistoryAndHome(historyRecords.map { it.session })
            }
            is SyncResult.RetryableFailure -> updateSyncUi(SyncStatus.Offline, "网络不可用，已安排重试")
            is SyncResult.PermanentFailure -> updateSyncUi(SyncStatus.Failed, result.message)
            is SyncResult.LocalChangesPending -> {
                updateSyncUi(SyncStatus.Failed, "有 ${result.count} 项本地记录待上传")
                emitMessage("云端覆盖已阻止：请先点击“上传本地”，或先导出备份。")
            }
            SyncResult.AlreadyRunning -> emitMessage("同步任务已在运行")
        }
    }

    private fun updateSyncUi(status: SyncStatus, message: String) {
        _uiState.value = _uiState.value.copy(
            home = _uiState.value.home.copy(syncStatus = status, syncMessage = message),
            settings = _uiState.value.settings.copy(syncStatus = status, lastSync = message),
        )
    }

    private fun logout() = launchAction(Dispatchers.IO) {
        val tokens = container.tokenStore.read()
        val authenticated = tokens != null
        if (authenticated && pendingSyncCount > 0) {
            emitMessage("仍有 $pendingSyncCount 项记录未同步；请先同步或导出后再退出账号。")
            return@launchAction
        }
        runCatching {
            if (tokens != null) {
                container.apiClientFactory.apiService.logout(LogoutRequestDto(tokens.refreshToken))
            }
        }
        if (authenticated) {
            // Clear the previous account's cache before dropping its identity token. This ordering
            // keeps a process death from leaving account A's database ready for account B.
            container.database.clearAllTables()
        }
        suppressAuthenticationLossUntilLogin = true
        observedAuthenticated = false
        container.tokenStore.clear()
        container.syncWorkScheduler.cancelAllSync()
        container.settingsRepository.reset()
        if (authenticated) {
            container.ensureLocalData()
        }
        _uiState.value = FitnessAppUiState.preview(AppDestination.Onboarding)
        startupResolved = true
    }

    private fun overrideRecommendation(recommendation: String, reason: String) {
        _uiState.value = _uiState.value.copy(
            home = _uiState.value.home.copy(recommendation = recommendation, recommendationReason = reason),
        )
    }

    private fun emitMessage(message: String) {
        effectChannel.trySend(FitnessUiEffect.Message(message))
    }

    private fun launchAction(
        dispatcher: kotlinx.coroutines.CoroutineDispatcher = Dispatchers.Main.immediate,
        block: suspend () -> Unit,
    ) {
        viewModelScope.launch(dispatcher) { block() }
    }

    companion object {
        fun factory(container: AppContainer): ViewModelProvider.Factory =
            object : ViewModelProvider.Factory {
                @Suppress("UNCHECKED_CAST")
                override fun <T : ViewModel> create(modelClass: Class<T>): T {
                    require(modelClass.isAssignableFrom(FitnessViewModel::class.java))
                    return FitnessViewModel(container) as T
                }
            }
    }
}

internal fun parseTrainingDayCsv(value: String): Set<Int> {
    val days = value.split(',')
        .mapNotNull { it.trim().toIntOrNull() }
        .toSet()
    require(days.isNotEmpty()) { "至少选择一个训练日" }
    require(days.all { it in 1..7 }) { "训练日必须为 1..7" }
    return days
}

private fun apiOrigin(value: String): Triple<String, String, Int> {
    val uri = URI(normalizedBaseUrl(value))
    return Triple(uri.scheme.lowercase(), checkNotNull(uri.host).lowercase(), uri.port.takeIf { it >= 0 } ?: 443)
}

internal fun filterHistoryRecords(
    records: List<WorkoutSessionWithSets>,
    filter: HistoryFilterUi,
    today: LocalDate,
    exerciseId: String? = null,
): List<WorkoutSessionWithSets> {
    val cutoff = when (filter.period) {
        "近 7 天" -> today.minusDays(6)
        "近 30 天" -> today.minusDays(29)
        "近 90 天" -> today.minusDays(89)
        else -> null
    }
    return records.filter { record ->
        val localDate = runCatching { LocalDate.parse(record.session.localDate) }.getOrNull()
            ?: return@filter false
        val inPeriod = cutoff == null || (!localDate.isBefore(cutoff) && !localDate.isAfter(today))
        val matchesType = when (filter.workoutType) {
            "全部" -> true
            "A" -> record.session.planDayCode == PlanCode.A
            "B" -> record.session.planDayCode == PlanCode.B
            else -> false
        }
        val matchesExercise = exerciseId == null || record.sets.any { it.exerciseId == exerciseId }
        inPeriod && matchesType && matchesExercise
    }
}

internal fun realTrendPoints(
    records: List<WorkoutSessionWithSets>,
    exerciseId: String,
    limit: Int = 6,
): List<TrendPointUi> = records.asSequence()
    .filter { it.session.status == WorkoutStatus.COMPLETED }
    .flatMap { record ->
        record.sets.asSequence()
            .filter { set ->
                set.exerciseId == exerciseId && set.completed && !set.isWarmup && set.deletedAt == null
            }
            .map { set -> record to set }
    }
    .sortedByDescending { (record, set) -> set.completedAt ?: record.session.completedAt ?: 0L }
    .take(limit.coerceAtLeast(0))
    .toList()
    .asReversed()
    .mapNotNull { (record, set) ->
        val value = set.weightKg?.toFloat() ?: set.reps?.toFloat() ?: return@mapNotNull null
        val label = runCatching {
            LocalDate.parse(record.session.localDate).format(DateTimeFormatter.ofPattern("M/d"))
        }.getOrDefault(record.session.localDate)
        TrendPointUi(
            label = label,
            value = value,
            displayValue = set.weightKg?.let { "${formatWeight(it)} kg × ${set.reps ?: "--"}" }
                ?: "${set.reps ?: "--"} 次",
        )
    }

private fun formatWeight(value: Double): String = if (value % 1.0 == 0.0) {
    value.toInt().toString()
} else {
    "%.2f".format(Locale.ROOT, value).trimEnd('0').trimEnd('.')
}

private fun WorkoutSetDraft.toInput() = WorkoutSetInput(
    weightKg = weight.trim().takeIf(String::isNotEmpty)?.toDoubleOrNull(),
    reps = reps.trim().takeIf(String::isNotEmpty)?.toIntOrNull(),
    isWarmup = isWarmup,
    rir = rir,
    quality = when (quality) {
        "良好" -> SetQuality.GOOD
        "一般" -> SetQuality.FAIR
        "较差" -> SetQuality.POOR
        else -> null
    },
    pain = pain > 0,
    notes = note,
)

private fun SetQuality?.toUiQuality(): String = when (this) {
    SetQuality.GOOD -> "良好"
    SetQuality.FAIR -> "一般"
    SetQuality.POOR -> "较差"
    null -> "良好"
}

private fun StoredWeightUnit.toUi() = if (this == StoredWeightUnit.KG) UiWeightUnit.Kilogram else UiWeightUnit.Pound
private fun UiWeightUnit.toStored() = if (this == UiWeightUnit.Kilogram) StoredWeightUnit.KG else StoredWeightUnit.LB
private fun StoredDarkMode.toUi() = when (this) {
    StoredDarkMode.SYSTEM -> UiThemeMode.System
    StoredDarkMode.LIGHT -> UiThemeMode.Light
    StoredDarkMode.DARK -> UiThemeMode.Dark
}
private fun UiThemeMode.toStored() = when (this) {
    UiThemeMode.System -> StoredDarkMode.SYSTEM
    UiThemeMode.Light -> StoredDarkMode.LIGHT
    UiThemeMode.Dark -> StoredDarkMode.DARK
}

private fun dayLabel(day: Int): String = listOf("周一", "周二", "周三", "周四", "周五", "周六", "周日")
    .getOrElse(day - 1) { "周一" }

private fun dayNumber(label: String): Int? = mapOf(
    "周一" to 1, "周二" to 2, "周三" to 3, "周四" to 4,
    "周五" to 5, "周六" to 6, "周日" to 7,
)[label]

private fun String.toReasonText(): String = when (this) {
    "HIGH_FATIGUE" -> "疲劳评分较高，建议恢复"
    "WEEKLY_LIMIT_REACHED" -> "本周已达到 3 次力量训练"
    "CONSECUTIVE_FULL_BODY_PROTECTION" -> "昨天刚完成全身训练，今天避免连续训练"
    "FIRST_STRENGTH_SESSION" -> "首次训练默认从 A 开始"
    "ALTERNATE_AFTER_A" -> "上次完成 A，本次自动交替为 B"
    "ALTERNATE_AFTER_B" -> "上次完成 B，本次自动交替为 A"
    else -> "已按用户选择覆盖推荐"
}
