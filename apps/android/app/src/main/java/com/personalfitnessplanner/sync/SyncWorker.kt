package com.personalfitnessplanner.sync

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import java.util.UUID
import java.util.concurrent.TimeUnit

/** Implemented by the Application graph so WorkManager uses the same coordinator as the UI. */
interface SyncDependenciesProvider {
    val syncCoordinator: SyncCoordinator
}

class SyncWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : CoroutineWorker(appContext, workerParams) {
    override suspend fun doWork(): Result {
        val coordinator = (applicationContext as? SyncDependenciesProvider)?.syncCoordinator
            ?: return Result.failure(workDataOf(KEY_ERROR to "Sync dependencies are not initialized"))
        val trigger = runCatching {
            SyncTrigger.valueOf(inputData.getString(KEY_TRIGGER) ?: SyncTrigger.RETRY.name)
        }.getOrDefault(SyncTrigger.RETRY)
        val fullResync = inputData.getBoolean(KEY_FULL_RESYNC, false)
        return when (val result = coordinator.sync(trigger, fullResync)) {
            is SyncResult.Success -> Result.success(
                workDataOf(
                    KEY_PUSHED_COUNT to result.pushedCount,
                    KEY_PULLED_COUNT to result.pulledCount,
                ),
            )

            is SyncResult.RetryableFailure -> Result.retry()
            is SyncResult.PermanentFailure -> Result.failure(
                workDataOf(KEY_ERROR to result.message.take(MAX_OUTPUT_MESSAGE_LENGTH)),
            )

            is SyncResult.LocalChangesPending -> Result.failure(
                workDataOf(KEY_ERROR to "${result.count} local changes are waiting to be uploaded"),
            )

            SyncResult.AlreadyRunning -> Result.success()
        }
    }

    companion object {
        const val KEY_TRIGGER = "sync_trigger"
        const val KEY_FULL_RESYNC = "full_resync"
        const val KEY_PUSHED_COUNT = "pushed_count"
        const val KEY_PULLED_COUNT = "pulled_count"
        const val KEY_ERROR = "error"
        private const val MAX_OUTPUT_MESSAGE_LENGTH = 1_000
    }
}

class SyncWorkScheduler(
    context: Context,
    private val workManager: WorkManager = WorkManager.getInstance(context.applicationContext),
) : SyncRetryEnqueuer {
    private val connected = Constraints.Builder()
        .setRequiredNetworkType(NetworkType.CONNECTED)
        .build()

    fun enqueueManual(fullResync: Boolean = false): UUID {
        val trigger = if (fullResync) SyncTrigger.FULL_RESYNC else SyncTrigger.MANUAL
        val request = OneTimeWorkRequestBuilder<SyncWorker>()
            .setConstraints(connected)
            .setInputData(
                workDataOf(
                    SyncWorker.KEY_TRIGGER to trigger.name,
                    SyncWorker.KEY_FULL_RESYNC to fullResync,
                ),
            )
            .addTag(TAG_SYNC)
            .build()
        workManager.enqueueUniqueWork(
            if (fullResync) UNIQUE_FULL_SYNC else UNIQUE_MANUAL_SYNC,
            ExistingWorkPolicy.KEEP,
            request,
        )
        return request.id
    }

    fun configureBackgroundSync(enabled: Boolean, repeatIntervalHours: Long = 6L) {
        if (!enabled) {
            workManager.cancelUniqueWork(UNIQUE_PERIODIC_SYNC)
            return
        }
        require(repeatIntervalHours >= 1L) { "Background sync interval must be positive" }
        val request = PeriodicWorkRequestBuilder<SyncWorker>(repeatIntervalHours, TimeUnit.HOURS)
            .setConstraints(connected)
            .setInputData(workDataOf(SyncWorker.KEY_TRIGGER to SyncTrigger.BACKGROUND.name))
            .addTag(TAG_SYNC)
            .build()
        workManager.enqueueUniquePeriodicWork(
            UNIQUE_PERIODIC_SYNC,
            ExistingPeriodicWorkPolicy.UPDATE,
            request,
        )
    }

    /** Connected constraint keeps offline work queued; backoff covers transport/server failures. */
    override fun enqueueRetry() {
        val request = OneTimeWorkRequestBuilder<SyncWorker>()
            .setConstraints(connected)
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .setInputData(workDataOf(SyncWorker.KEY_TRIGGER to SyncTrigger.RETRY.name))
            .addTag(TAG_SYNC)
            .build()
        workManager.enqueueUniqueWork(UNIQUE_RETRY_SYNC, ExistingWorkPolicy.KEEP, request)
    }

    fun cancelAllSync() {
        workManager.cancelAllWorkByTag(TAG_SYNC)
    }

    companion object {
        const val TAG_SYNC = "personal_fitness_sync"
        const val UNIQUE_MANUAL_SYNC = "personal_fitness_manual_sync"
        const val UNIQUE_FULL_SYNC = "personal_fitness_full_sync"
        const val UNIQUE_RETRY_SYNC = "personal_fitness_retry_sync"
        const val UNIQUE_PERIODIC_SYNC = "personal_fitness_periodic_sync"
    }
}
