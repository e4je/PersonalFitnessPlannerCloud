package com.personalfitnessplanner.data.export

import android.content.Context
import android.content.Intent
import androidx.core.content.FileProvider
import com.personalfitnessplanner.data.local.WorkoutSessionWithSets
import java.io.File
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import org.json.JSONArray
import org.json.JSONObject

enum class WorkoutExportFormat(val extension: String, val mimeType: String) {
    CSV("csv", "text/csv"),
    JSON("json", "application/json"),
}

/** Produces shareable, user-owned exports without exposing the Room database file. */
class WorkoutExportManager(private val context: Context) {
    fun export(
        sessions: List<WorkoutSessionWithSets>,
        format: WorkoutExportFormat,
    ): File {
        val directory = File(context.cacheDir, EXPORT_DIRECTORY).apply { mkdirs() }
        val stamp = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss")
            .withZone(ZoneOffset.UTC)
            .format(Instant.now())
        val output = File(directory, "personal-fitness-$stamp.${format.extension}")
        output.writeText(
            when (format) {
                WorkoutExportFormat.CSV -> toCsv(sessions)
                WorkoutExportFormat.JSON -> toJson(sessions).toString(2)
            },
            Charsets.UTF_8,
        )
        return output
    }

    fun shareIntent(file: File, format: WorkoutExportFormat): Intent {
        require(file.parentFile?.canonicalFile == File(context.cacheDir, EXPORT_DIRECTORY).canonicalFile) {
            "Only application-created export files can be shared"
        }
        val uri = FileProvider.getUriForFile(
            context,
            "${context.packageName}.files",
            file,
        )
        return Intent(Intent.ACTION_SEND).apply {
            type = format.mimeType
            putExtra(Intent.EXTRA_STREAM, uri)
            putExtra(Intent.EXTRA_SUBJECT, "私人健身规划训练记录")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
    }

    /** JSON backup intentionally excludes access/refresh tokens and Keystore material. */
    fun localBackup(sessions: List<WorkoutSessionWithSets>): File {
        val directory = File(context.filesDir, BACKUP_DIRECTORY).apply { mkdirs() }
        val output = File(directory, "personal-fitness-backup.json")
        val root = JSONObject()
            .put("schema_version", 2)
            .put("created_at", Instant.now().toString())
            .put("workout_sessions", toJson(sessions))
        output.writeText(root.toString(2), Charsets.UTF_8)
        return output
    }

    private fun toJson(sessions: List<WorkoutSessionWithSets>): JSONArray = JSONArray().apply {
        sessions.forEach { record ->
            val session = record.session
            put(
                JSONObject()
                    .put("id", session.id)
                    .put("plan_version_id", session.planVersionId)
                    .put("plan_day_code", session.planDayCode?.name)
                    .put("local_date", session.localDate)
                    .put("timezone", session.timezone)
                    .put("started_at_epoch_ms", session.startedAt)
                    .put("completed_at_epoch_ms", session.completedAt)
                    .put("status", session.status.name)
                    .put("notes", session.notes)
                    .put("deleted_at_epoch_ms", session.deletedAt)
                    .put("sets", JSONArray().apply {
                        record.sets.sortedBy { it.setNumber }.forEach { set ->
                            put(
                                JSONObject()
                                    .put("id", set.id)
                                    .put("exercise_id", set.exerciseId)
                                    .put("equipment_id", set.equipmentId)
                                    .put("set_number", set.setNumber)
                                    .put("weight_kg", set.weightKg)
                                    .put("reps", set.reps)
                                    .put("duration_seconds", set.durationSeconds)
                                    .put("is_warmup", set.isWarmup)
                                    .put("rir", set.rir)
                                    .put("quality", set.quality?.name)
                                    .put("pain", set.pain)
                                    .put("notes", set.notes)
                                    .put("completed", set.completed)
                                    .put("completed_at_epoch_ms", set.completedAt)
                                    .put("deleted_at_epoch_ms", set.deletedAt)
                            )
                        }
                    }),
            )
        }
    }

    private fun toCsv(sessions: List<WorkoutSessionWithSets>): String = buildString {
        appendLine(
            listOf(
                "session_id", "local_date", "plan", "status", "exercise_id", "set_number",
                "weight_kg", "reps", "warmup", "rir", "quality", "pain", "completed", "notes",
            ).joinToString(","),
        )
        sessions.forEach { record ->
            record.sets.sortedBy { it.setNumber }.forEach { set ->
                appendLine(
                    listOf(
                        record.session.id,
                        record.session.localDate,
                        record.session.planDayCode?.name.orEmpty(),
                        record.session.status.name,
                        set.exerciseId,
                        set.setNumber.toString(),
                        set.weightKg?.toString().orEmpty(),
                        set.reps?.toString().orEmpty(),
                        set.isWarmup.toString(),
                        set.rir?.toString().orEmpty(),
                        set.quality?.name.orEmpty(),
                        set.pain.toString(),
                        set.completed.toString(),
                        set.notes.orEmpty(),
                    ).joinToString(",", transform = ::escapeCsv),
                )
            }
        }
    }

    private fun escapeCsv(value: String): String = if (
        value.any { it == ',' || it == '"' || it == '\n' || it == '\r' }
    ) {
        "\"${value.replace("\"", "\"\"")}\""
    } else {
        value
    }

    private companion object {
        const val EXPORT_DIRECTORY = "exports"
        const val BACKUP_DIRECTORY = "backups"
    }
}
