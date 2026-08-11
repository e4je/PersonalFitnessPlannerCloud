package com.personalfitnessplanner.sync

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import java.util.UUID
import java.util.concurrent.TimeUnit

class RestTimerWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : CoroutineWorker(appContext, workerParams) {
    override suspend fun doWork(): Result {
        if (
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(
                applicationContext,
                Manifest.permission.POST_NOTIFICATIONS,
            ) != PackageManager.PERMISSION_GRANTED
        ) {
            return Result.success()
        }

        val notificationManager = applicationContext.getSystemService(NotificationManager::class.java)
        notificationManager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                CHANNEL_NAME,
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = "Notifies when the configured between-set rest is complete"
                enableVibration(true)
            },
        )

        val launchIntent = applicationContext.packageManager
            .getLaunchIntentForPackage(applicationContext.packageName)
        val pendingIntent = launchIntent?.let {
            PendingIntent.getActivity(
                applicationContext,
                0,
                it,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
        }
        val notification = NotificationCompat.Builder(applicationContext, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
            .setContentTitle(inputData.getString(KEY_TITLE) ?: DEFAULT_TITLE)
            .setContentText(inputData.getString(KEY_MESSAGE) ?: DEFAULT_MESSAGE)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .apply { pendingIntent?.let { setContentIntent(it) } }
            .build()
        notificationManager.notify(inputData.getInt(KEY_NOTIFICATION_ID, DEFAULT_NOTIFICATION_ID), notification)
        return Result.success()
    }

    companion object {
        const val CHANNEL_ID = "rest_timer"
        const val CHANNEL_NAME = "Rest timer"
        const val KEY_TITLE = "title"
        const val KEY_MESSAGE = "message"
        const val KEY_NOTIFICATION_ID = "notification_id"
        const val DEFAULT_NOTIFICATION_ID = 7_301
        private const val DEFAULT_TITLE = "Rest complete"
        private const val DEFAULT_MESSAGE = "Your next set is ready."
    }
}

class RestTimerScheduler(
    context: Context,
    private val workManager: WorkManager = WorkManager.getInstance(context.applicationContext),
) {
    fun start(
        durationSeconds: Int,
        timerId: String,
        title: String = "Rest complete",
        message: String = "Your next set is ready.",
        notificationId: Int = RestTimerWorker.DEFAULT_NOTIFICATION_ID,
    ): UUID {
        require(durationSeconds >= 0) { "Rest duration cannot be negative" }
        require(timerId.isNotBlank()) { "timerId cannot be blank" }
        val request = OneTimeWorkRequestBuilder<RestTimerWorker>()
            .setInitialDelay(durationSeconds.toLong(), TimeUnit.SECONDS)
            .setInputData(
                workDataOf(
                    RestTimerWorker.KEY_TITLE to title,
                    RestTimerWorker.KEY_MESSAGE to message,
                    RestTimerWorker.KEY_NOTIFICATION_ID to notificationId,
                ),
            )
            .addTag(TAG_REST_TIMER)
            .build()
        workManager.enqueueUniqueWork(uniqueName(timerId), ExistingWorkPolicy.REPLACE, request)
        return request.id
    }

    fun cancel(timerId: String) {
        workManager.cancelUniqueWork(uniqueName(timerId))
    }

    fun cancelAll() {
        workManager.cancelAllWorkByTag(TAG_REST_TIMER)
    }

    private fun uniqueName(timerId: String): String = "personal_fitness_rest_$timerId"

    companion object {
        const val TAG_REST_TIMER = "personal_fitness_rest_timer"
    }
}
