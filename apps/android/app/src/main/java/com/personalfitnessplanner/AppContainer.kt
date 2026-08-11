package com.personalfitnessplanner

import android.content.Context
import com.personalfitnessplanner.data.export.WorkoutExportManager
import com.personalfitnessplanner.data.local.AppDatabase
import com.personalfitnessplanner.data.local.UnitSystem
import com.personalfitnessplanner.data.remote.ApiClientFactory
import com.personalfitnessplanner.data.repository.LocalFitnessRepository
import com.personalfitnessplanner.data.repository.LocalUserProfile
import com.personalfitnessplanner.data.repository.RoomSyncLocalStore
import com.personalfitnessplanner.data.security.SecureTokenStore
import com.personalfitnessplanner.data.settings.AppSettings
import com.personalfitnessplanner.data.settings.SettingsRepository
import com.personalfitnessplanner.data.settings.WeightUnit
import com.personalfitnessplanner.sync.RestTimerScheduler
import com.personalfitnessplanner.sync.SyncCoordinator
import com.personalfitnessplanner.sync.SyncWorkScheduler
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/** Process-wide dependency graph shared by Compose and WorkManager. */
class AppContainer(context: Context) {
    val applicationContext: Context = context.applicationContext
    val database: AppDatabase = AppDatabase.build(applicationContext)
    val settingsRepository = SettingsRepository(applicationContext)
    val tokenStore = SecureTokenStore(applicationContext)
    val apiClientFactory = ApiClientFactory(
        initialBaseUrl = AppSettings().apiBaseUrl,
        tokenStore = tokenStore,
        isDebug = BuildConfig.DEBUG,
    )
    val fitnessRepository = LocalFitnessRepository(database)
    val syncLocalStore = RoomSyncLocalStore(database)
    val syncWorkScheduler: SyncWorkScheduler by lazy {
        SyncWorkScheduler(applicationContext)
    }
    val syncCoordinator: SyncCoordinator by lazy {
        SyncCoordinator(
            apiService = apiClientFactory.apiService,
            localStore = syncLocalStore,
            retryEnqueuer = runCatching { syncWorkScheduler }.getOrNull(),
        )
    }
    val restTimerScheduler: RestTimerScheduler by lazy {
        RestTimerScheduler(applicationContext)
    }
    val workoutExportManager = WorkoutExportManager(applicationContext)

    private val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val started = AtomicBoolean(false)
    private val persistedBaseUrlLoaded = AtomicBoolean(false)

    fun start() {
        if (!started.compareAndSet(false, true)) return

        applicationScope.launch {
            val settings = settingsRepository.current()
            fitnessRepository.initialize(settings.toLocalProfile())
        }
        applicationScope.launch {
            settingsRepository.settings.collectLatest { settings ->
                // The first emission is the persisted origin that owns any restored token.
                // Later origin changes invalidate that token before another request can use it.
                apiClientFactory.updateBaseUrl(
                    value = settings.apiBaseUrl,
                    clearAuthenticationOnOriginChange = persistedBaseUrlLoaded.getAndSet(true),
                )
                val authenticated = tokenStore.read() != null
                runCatching {
                    syncWorkScheduler.configureBackgroundSync(
                        enabled = settings.backgroundSyncEnabled &&
                            settings.onboardingComplete &&
                            !settings.localMode &&
                            authenticated,
                    )
                }
            }
        }
    }

    suspend fun ensureLocalData(settings: AppSettings) =
        fitnessRepository.initialize(settings.toLocalProfile())

    suspend fun ensureLocalData() = ensureLocalData(settingsRepository.current())
}

private fun AppSettings.toLocalProfile() = LocalUserProfile(
    timezone = timeZone,
    weightUnit = when (weightUnit) {
        WeightUnit.KG -> UnitSystem.KG
        WeightUnit.LB -> UnitSystem.LB
    },
)
