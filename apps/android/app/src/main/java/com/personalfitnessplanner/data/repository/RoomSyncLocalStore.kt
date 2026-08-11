package com.personalfitnessplanner.data.repository

import androidx.room.withTransaction
import androidx.sqlite.db.SimpleSQLiteQuery
import com.personalfitnessplanner.data.defaultplan.DefaultTrainingPlan
import com.personalfitnessplanner.data.local.AppDatabase
import com.personalfitnessplanner.data.local.PlanAssignmentEntity
import com.personalfitnessplanner.data.local.SyncOperation
import com.personalfitnessplanner.data.local.SyncStateEntity
import com.personalfitnessplanner.data.local.TrainingPlanEntity
import com.personalfitnessplanner.data.local.WorkoutStatus
import com.personalfitnessplanner.data.remote.BootstrapDto
import com.personalfitnessplanner.data.remote.CardioSessionDto
import com.personalfitnessplanner.data.remote.EquipmentDto
import com.personalfitnessplanner.data.remote.ExerciseAlternativeDto
import com.personalfitnessplanner.data.remote.ExerciseDto
import com.personalfitnessplanner.data.remote.PlanAssignmentDto
import com.personalfitnessplanner.data.remote.PlanDayDto
import com.personalfitnessplanner.data.remote.PlanSlotDto
import com.personalfitnessplanner.data.remote.PlanSlotOptionDto
import com.personalfitnessplanner.data.remote.PlanVersionDto
import com.personalfitnessplanner.data.remote.ReadinessDto
import com.personalfitnessplanner.data.remote.RemoteMappers
import com.personalfitnessplanner.data.remote.SyncChangeDto
import com.personalfitnessplanner.data.remote.SyncChangesDto
import com.personalfitnessplanner.data.remote.UserDto
import com.personalfitnessplanner.data.remote.WorkoutSessionDto
import com.personalfitnessplanner.data.remote.WorkoutSetDto
import com.personalfitnessplanner.data.remote.epochMillisOrNull
import com.personalfitnessplanner.sync.OutboxItem
import com.personalfitnessplanner.sync.OutboxOperation
import com.personalfitnessplanner.sync.SyncLocalStore
import com.squareup.moshi.JsonAdapter
import com.squareup.moshi.Moshi
import com.squareup.moshi.Types
import com.squareup.moshi.kotlin.reflect.KotlinJsonAdapterFactory
import java.nio.charset.StandardCharsets
import java.time.Clock
import java.time.ZoneId
import java.time.ZoneOffset
import java.util.UUID

class PendingAccountSwitchException(
    val pendingMutationCount: Int,
) : IllegalStateException(
    "旧账号仍有 $pendingMutationCount 项待同步记录；数据已保留，请先重新登录旧账号完成同步或导出后再切换账号",
)

/** Room-backed synchronization boundary. All pull applications and cursor writes are atomic. */
class RoomSyncLocalStore(
    private val database: AppDatabase,
    private val userId: String? = null,
    private val clock: Clock = Clock.systemUTC(),
    moshi: Moshi = defaultRepositoryMoshi(),
) : SyncLocalStore {
    private val payloadAdapter: JsonAdapter<Map<String, Any?>> = moshi.adapter(
        Types.newParameterizedType(
            Map::class.java,
            String::class.java,
            Any::class.java,
        ),
    )
    private val userAdapter = moshi.adapter(UserDto::class.java)
    private val equipmentAdapter = moshi.adapter(EquipmentDto::class.java)
    private val exerciseAdapter = moshi.adapter(ExerciseDto::class.java)
    private val alternativeAdapter = moshi.adapter(ExerciseAlternativeDto::class.java)
    private val planVersionAdapter = moshi.adapter(PlanVersionDto::class.java)
    private val planDayAdapter = moshi.adapter(PlanDayDto::class.java)
    private val planSlotAdapter = moshi.adapter(PlanSlotDto::class.java)
    private val planOptionAdapter = moshi.adapter(PlanSlotOptionDto::class.java)
    private val assignmentAdapter = moshi.adapter(PlanAssignmentDto::class.java)
    private val workoutAdapter = moshi.adapter(WorkoutSessionDto::class.java)
    private val workoutSetAdapter = moshi.adapter(WorkoutSetDto::class.java)
    private val readinessAdapter = moshi.adapter(ReadinessDto::class.java)
    private val cardioAdapter = moshi.adapter(CardioSessionDto::class.java)

    override suspend fun pendingOutbox(limit: Int): List<OutboxItem> {
        require(limit > 0) { "limit must be positive" }
        return database.syncDao().readyItems(clock.millis(), limit).map { item ->
            OutboxItem(
                id = item.id,
                entityType = item.aggregateType,
                entityId = item.aggregateId,
                operation = when (item.operation) {
                    SyncOperation.UPSERT -> OutboxOperation.UPSERT
                    SyncOperation.DELETE -> OutboxOperation.DELETE
                },
                payload = item.payloadJson.takeUnless(String::isBlank)?.let { json ->
                    checkNotNull(payloadAdapter.fromJson(json)) {
                        "Outbox ${item.id} contains an empty payload"
                    }
                },
                idempotencyKey = item.idempotencyKey,
                attemptCount = item.attemptCount,
            )
        }
    }

    override suspend fun markOutboxSynced(ids: List<String>) {
        if (ids.isEmpty()) return
        database.withTransaction {
            ids.distinct().forEach { database.syncDao().acknowledge(it) }
        }
    }

    override suspend fun markOutboxFailed(id: String, message: String, retryable: Boolean) {
        val now = clock.millis()
        database.syncDao().markFailed(
            id = id,
            error = message.take(MAX_ERROR_LENGTH),
            nextAttemptAt = if (retryable) now.saturatingAdd(RETRY_DELAY_MILLIS) else Long.MAX_VALUE,
            updatedAt = now,
        )
    }

    override suspend fun applyIncrementalChanges(changes: SyncChangesDto) {
        database.withTransaction {
            changes.changes.forEach { applyChange(it) }
            if (changes.nextCursor != null || changes.cursor != null) {
                writeCursorInTransaction(changes.nextCursor ?: changes.cursor)
            }
        }
    }

    override suspend fun replaceServerOwnedData(bootstrap: BootstrapDto) {
        val now = clock.millis()
        database.withTransaction {
            val existingUserId = currentUserId()
            val serverUserId = bootstrap.user?.id ?: existingUserId
            if (existingUserId != LocalFitnessRepository.LOCAL_USER_ID &&
                existingUserId != serverUserId
            ) {
                val pendingCount = database.syncDao().pendingMutationCount()
                if (pendingCount > 0) throw PendingAccountSwitchException(pendingCount)
                purgeCachedServerUser(existingUserId)
            }
            bootstrap.user?.let {
                database.userDao().upsert(RemoteMappers.user(it, now))
            }
            if (bootstrap.user != null) {
                reconcileServerUserScope(serverUserId, bootstrap)
                reconcileServerCatalog(bootstrap)
            }
            if (bootstrap.equipment.isNotEmpty()) {
                database.catalogDao().upsertEquipment(bootstrap.equipment.map { RemoteMappers.equipment(it, now) })
            }
            if (bootstrap.exercises.isNotEmpty()) {
                database.catalogDao().upsertExercises(bootstrap.exercises.map { RemoteMappers.exercise(it, now) })
                val alternatives = bootstrap.exercises.flatMap(ExerciseDto::alternatives)
                if (alternatives.isNotEmpty()) {
                    database.catalogDao().upsertAlternatives(
                        alternatives.map { RemoteMappers.alternative(it, now) },
                    )
                }
            }
            bootstrap.serverPlanVersions().forEach { upsertPlanVersion(it, now) }
            if (bootstrap.assignments.isNotEmpty()) {
                bootstrap.assignments.firstOrNull {
                    it.userId == serverUserId && it.isActive && it.deletedAt == null
                }?.let { active ->
                    database.planDao().deactivateOtherAssignments(active.userId, active.id, now)
                }
                database.planDao().upsertAssignments(
                    bootstrap.assignments.map { RemoteMappers.assignment(it, now) },
                )
            }
            if (bootstrap.user != null) {
                ensureCurrentPlanAssignment(serverUserId, bootstrap, now)
            }

            // The bootstrap arrays are the complete server mirror. Reconciliation above removes
            // absent server rows, while pending local aggregates remain untouched until pushed.
            bootstrap.workoutSessions.forEach { dto ->
                if (!hasPendingWorkout(dto.id)) upsertWorkout(dto, now, replaceSets = true)
            }
            bootstrap.readiness.forEach { dto ->
                if (!hasPending(READINESS_TYPE, dto.id)) {
                    database.readinessDao().upsert(RemoteMappers.readiness(dto, now))
                }
            }
            bootstrap.cardioSessions.forEach { dto ->
                if (!hasPending(CARDIO_TYPE, dto.id)) {
                    database.cardioDao().upsert(RemoteMappers.cardio(dto, now))
                }
            }
            writeCursorInTransaction(bootstrap.syncCursor ?: bootstrap.cursor, serverUserId)
        }
    }

    /**
     * Releases an authenticated cache before entering the fixed offline identity. Unsynchronized
     * mutations are never discarded; callers must re-authenticate the owning account first.
     */
    suspend fun releaseServerIdentityForLocalMode() {
        database.withTransaction {
            val existingUserId = currentUserId()
            if (existingUserId == LocalFitnessRepository.LOCAL_USER_ID) return@withTransaction
            checkNotNull(database.userDao().getById(LocalFitnessRepository.LOCAL_USER_ID)) {
                "Offline identity must be initialized before releasing a server account"
            }
            val pendingCount = database.syncDao().pendingMutationCount()
            if (pendingCount > 0) throw PendingAccountSwitchException(pendingCount)
            purgeCachedServerUser(existingUserId)
            reconcileServerCatalog(BootstrapDto())
        }
    }

    override suspend fun readCursor(): String? {
        val effectiveUserId = currentUserId()
        return database.syncDao().state(effectiveUserId, SYNC_SCOPE)?.cursor
    }

    override suspend fun writeCursor(cursor: String?) {
        database.withTransaction { writeCursorInTransaction(cursor) }
    }

    private suspend fun applyChange(change: SyncChangeDto) {
        val type = canonicalType(change.entityType)
        val table = tableFor(type)
        val effectiveId = change.entityId
        if (table != null && !incomingVersionIsCurrentOrNewer(table, effectiveId, change.version)) {
            return
        }
        if (change.operation.equals("DELETE", ignoreCase = true)) {
            applyDelete(type, change, effectiveId)
            return
        }
        val rawPayload = requireNotNull(change.payload) {
            "${change.entityType} upsert ${change.entityId} has no payload"
        }
        val payload = rawPayload.toMutableMap().apply {
            val payloadVersion = (get("version") as? Number)?.toLong() ?: Long.MIN_VALUE
            if (payloadVersion < change.version) put("version", change.version)
        }
        val now = clock.millis()
        when (type) {
            USER_TYPE -> {
                val dto = userAdapter.decode(payload, change)
                val cachedUserId = currentUserId()
                require(
                    cachedUserId == LocalFitnessRepository.LOCAL_USER_ID ||
                        cachedUserId == dto.id,
                ) {
                    "Incremental user ${dto.id} does not match cached user $cachedUserId"
                }
                database.userDao().upsert(RemoteMappers.user(dto, now))
            }

            EQUIPMENT_TYPE -> database.catalogDao().upsertEquipment(
                listOf(RemoteMappers.equipment(equipmentAdapter.decode(payload, change), now)),
            )

            EXERCISE_TYPE -> {
                val dto = exerciseAdapter.decode(payload, change)
                database.catalogDao().upsertExercises(listOf(RemoteMappers.exercise(dto, now)))
                if (dto.alternatives.isNotEmpty()) {
                    database.catalogDao().upsertAlternatives(
                        dto.alternatives.map { RemoteMappers.alternative(it, now) },
                    )
                }
            }

            ALTERNATIVE_TYPE -> database.catalogDao().upsertAlternatives(
                listOf(RemoteMappers.alternative(alternativeAdapter.decode(payload, change), now)),
            )

            TRAINING_PLAN_TYPE -> database.planDao().upsertPlans(
                listOf(payload.toTrainingPlan(change, now)),
            )

            PLAN_VERSION_TYPE -> upsertPlanVersion(planVersionAdapter.decode(payload, change), now)
            PLAN_DAY_TYPE -> {
                val dto = planDayAdapter.decode(payload, change)
                database.planDao().upsertDays(listOf(RemoteMappers.planDay(dto, now)))
                val slots = dto.slots
                if (slots.isNotEmpty()) {
                    database.planDao().upsertSlots(slots.map { RemoteMappers.planSlot(it, now) })
                    val options = slots.flatMap(PlanSlotDto::options)
                    if (options.isNotEmpty()) {
                        database.planDao().upsertOptions(options.map { RemoteMappers.planSlotOption(it, now) })
                    }
                }
            }

            PLAN_SLOT_TYPE -> {
                val dto = planSlotAdapter.decode(payload, change)
                database.planDao().upsertSlots(listOf(RemoteMappers.planSlot(dto, now)))
                if (dto.options.isNotEmpty()) {
                    database.planDao().upsertOptions(dto.options.map { RemoteMappers.planSlotOption(it, now) })
                }
            }

            PLAN_OPTION_TYPE -> database.planDao().upsertOptions(
                listOf(RemoteMappers.planSlotOption(planOptionAdapter.decode(payload, change), now)),
            )

            ASSIGNMENT_TYPE -> upsertAssignment(assignmentAdapter.decode(payload, change), now)

            WORKOUT_TYPE -> {
                if (!hasPendingWorkout(change.entityId)) {
                    upsertWorkout(workoutAdapter.decode(payload, change), now)
                }
            }

            WORKOUT_SET_TYPE -> {
                val dto = workoutSetAdapter.decode(payload, change)
                if (!hasPendingWorkout(dto.sessionId) && !hasPending(WORKOUT_SET_TYPE, dto.id)) {
                    database.workoutDao().upsertSets(listOf(RemoteMappers.workoutSet(dto, now)))
                }
            }

            READINESS_TYPE -> {
                if (!hasPending(READINESS_TYPE, change.entityId)) {
                    database.readinessDao().upsert(
                        RemoteMappers.readiness(readinessAdapter.decode(payload, change), now),
                    )
                }
            }

            CARDIO_TYPE -> {
                if (!hasPending(CARDIO_TYPE, change.entityId)) {
                    database.cardioDao().upsert(
                        RemoteMappers.cardio(cardioAdapter.decode(payload, change), now),
                    )
                }
            }

            else -> Unit // Forward-compatible: a newer server may add an entity this client cannot display.
        }
    }

    private suspend fun applyDelete(type: String, change: SyncChangeDto, effectiveId: String) {
        if (type == WORKOUT_TYPE && hasPendingWorkout(change.entityId)) return
        if (type == WORKOUT_SET_TYPE) {
            val sessionId = change.payload?.get("session_id") as? String
                ?: database.workoutDao().sessionIdForSet(change.entityId)
            if ((sessionId != null && hasPendingWorkout(sessionId)) ||
                hasPending(WORKOUT_SET_TYPE, change.entityId)
            ) return
        }
        if (type == READINESS_TYPE && hasPending(READINESS_TYPE, change.entityId)) return
        if (type == CARDIO_TYPE && hasPending(CARDIO_TYPE, change.entityId)) return

        val table = tableFor(type) ?: return
        val deletedAt = change.changedAt.epochMillisOrNull() ?: clock.millis()
        val statusAssignment = if (type == WORKOUT_TYPE) ", status = '${WorkoutStatus.DELETED.name}'" else ""
        database.openHelper.writableDatabase.execSQL(
            """
            UPDATE $table
            SET deleted_at = ?, updated_at = ?,
                version = CASE WHEN version < ? THEN ? ELSE version END
                $statusAssignment
            WHERE id = ?
              AND version <= ?
            """.trimIndent(),
            arrayOf(deletedAt, deletedAt, change.version, change.version, effectiveId, change.version),
        )
    }

    private fun incomingVersionIsCurrentOrNewer(
        table: String,
        entityId: String,
        incomingVersion: Long,
    ): Boolean {
        val query = SimpleSQLiteQuery(
            "SELECT version FROM $table WHERE id = ? LIMIT 1",
            arrayOf(entityId),
        )
        return database.openHelper.readableDatabase.query(query).use { cursor ->
            !cursor.moveToFirst() || incomingVersion >= cursor.getLong(0)
        }
    }

    private suspend fun upsertPlanVersion(dto: PlanVersionDto, now: Long) {
        val mapped = RemoteMappers.plan(dto, now)
        database.planDao().upsertPlans(listOf(mapped.plan))
        database.planDao().upsertVersions(listOf(mapped.version))
        if (mapped.days.isNotEmpty()) database.planDao().upsertDays(mapped.days)
        if (mapped.slots.isNotEmpty()) database.planDao().upsertSlots(mapped.slots)
        if (mapped.options.isNotEmpty()) database.planDao().upsertOptions(mapped.options)
    }

    private suspend fun upsertWorkout(
        dto: WorkoutSessionDto,
        now: Long,
        replaceSets: Boolean = false,
    ) {
        val mapped = RemoteMappers.workout(dto, now)
        if (replaceSets) {
            val incomingSetIds = mapped.sets.mapTo(hashSetOf()) { it.id }
            idsForParent("workout_sets", "session_id", mapped.session.id)
                .filterNot { it in incomingSetIds }
                .forEach { staleId ->
                    database.openHelper.writableDatabase.execSQL(
                        "DELETE FROM workout_sets WHERE id = ?",
                        arrayOf(staleId),
                    )
                }
        }
        database.workoutDao().insertSessionWithSets(mapped.session, mapped.sets)
    }

    private suspend fun upsertAssignment(dto: PlanAssignmentDto, now: Long) {
        val mapped = RemoteMappers.assignment(dto, now)
        if (mapped.isActive) {
            database.planDao().deactivateOtherAssignments(mapped.userId, mapped.id, now)
        }
        database.planDao().upsertAssignment(mapped)
    }

    private suspend fun hasPendingWorkout(sessionId: String): Boolean =
        hasPending(WORKOUT_TYPE, sessionId) || hasPending("workout_sessions", sessionId)

    private suspend fun hasPending(type: String, id: String): Boolean =
        database.syncDao().hasPendingForAggregate(type, id)

    private suspend fun writeCursorInTransaction(
        cursor: String?,
        scopedUserId: String? = null,
    ) {
        val effectiveUserId = scopedUserId ?: currentUserId()
        val now = clock.millis()
        val existing = database.syncDao().state(effectiveUserId, SYNC_SCOPE)
        database.syncDao().upsertState(
            SyncStateEntity(
                id = existing?.id ?: stableId("sync-state:$effectiveUserId:$SYNC_SCOPE"),
                userId = effectiveUserId,
                scope = SYNC_SCOPE,
                cursor = cursor,
                lastSyncedAt = now,
                fullResyncRequired = false,
                lastError = null,
                version = (existing?.version ?: 0L) + 1L,
                createdAt = existing?.createdAt ?: now,
                updatedAt = now,
            ),
        )
    }

    private suspend fun currentUserId(): String = userId
        ?: database.userDao().getCurrent(LocalFitnessRepository.LOCAL_USER_ID)?.id
        ?: LocalFitnessRepository.LOCAL_USER_ID

    /**
     * Removes only the previous authenticated account. The fixed offline identity and its rows are
     * retained, and the caller has already proved that no unsynchronized mutation would be lost.
     * This runs inside the same Room transaction that installs the new bootstrap, so observers can
     * never see a half-switched server identity.
     */
    private fun purgeCachedServerUser(serverUserId: String) {
        val db = database.openHelper.writableDatabase
        db.execSQL(
            "DELETE FROM workout_sets WHERE session_id IN " +
                "(SELECT id FROM workout_sessions WHERE user_id = ?)",
            arrayOf(serverUserId),
        )
        listOf(
            "workout_sessions",
            "daily_readiness",
            "cardio_sessions",
            "plan_assignments",
            "sync_state",
            "app_settings",
        ).forEach { table ->
            db.execSQL("DELETE FROM $table WHERE user_id = ?", arrayOf(serverUserId))
        }
        db.execSQL("DELETE FROM users WHERE id = ?", arrayOf(serverUserId))
    }

    private suspend fun reconcileServerUserScope(
        serverUserId: String,
        bootstrap: BootstrapDto,
    ) {
        val assignmentIds = bootstrap.assignments.mapTo(hashSetOf()) { it.id }
        if (bootstrap.needsFallbackAssignment(serverUserId)) {
            assignmentIds += fallbackAssignmentId(serverUserId, checkNotNull(bootstrap.currentPlan).id)
        }
        reconcileUserTable(
            table = "plan_assignments",
            serverUserId = serverUserId,
            incomingIds = assignmentIds,
            pendingTypes = setOf(ASSIGNMENT_TYPE, "plan_assignments"),
        )
        reconcileUserTable(
            table = "workout_sessions",
            serverUserId = serverUserId,
            incomingIds = bootstrap.workoutSessions.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(WORKOUT_TYPE, "workout_sessions"),
        ) { sessionId ->
            database.openHelper.writableDatabase.execSQL(
                "DELETE FROM workout_sets WHERE session_id = ?",
                arrayOf(sessionId),
            )
        }
        reconcileUserTable(
            table = "daily_readiness",
            serverUserId = serverUserId,
            incomingIds = bootstrap.readiness.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(READINESS_TYPE, "readiness"),
        )
        reconcileUserTable(
            table = "cardio_sessions",
            serverUserId = serverUserId,
            incomingIds = bootstrap.cardioSessions.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(CARDIO_TYPE, "cardio_sessions"),
        )
        // Keep local-only rows for the fixed offline identity. Server-scoped rows absent from the
        // now-complete bootstrap are removed, while any aggregate with a pending outbox survives.
    }

    private suspend fun reconcileServerCatalog(bootstrap: BootstrapDto) {
        val builtIn = DefaultTrainingPlan.create(nowEpochMillis = 0L)
        val remotePlans = bootstrap.serverPlanVersions()
        deleteMissingRows(
            table = "plan_slot_options",
            incomingIds = remotePlans
                .flatMap { it.days }
                .flatMap { it.slots }
                .flatMap { it.options }
                .mapTo(hashSetOf()) { it.id },
            preservedIds = builtIn.options.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(PLAN_OPTION_TYPE, "plan_slot_options"),
        )
        deleteMissingRows(
            table = "plan_slots",
            incomingIds = remotePlans
                .flatMap { it.days }
                .flatMap { it.slots }
                .mapTo(hashSetOf()) { it.id },
            preservedIds = builtIn.slots.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(PLAN_SLOT_TYPE, "plan_slots"),
        )
        deleteMissingRows(
            table = "plan_days",
            incomingIds = remotePlans.flatMap { it.days }.mapTo(hashSetOf()) { it.id },
            preservedIds = builtIn.days.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(PLAN_DAY_TYPE, "plan_days"),
        )
        deleteMissingRows(
            table = "plan_versions",
            incomingIds = remotePlans.mapTo(hashSetOf()) { it.id },
            preservedIds = hashSetOf(builtIn.planVersion.id),
            pendingTypes = setOf(PLAN_VERSION_TYPE, "plan_versions"),
        )
        deleteMissingRows(
            table = "training_plans",
            incomingIds = remotePlans.mapTo(hashSetOf()) { it.planId },
            preservedIds = hashSetOf(builtIn.plan.id),
            whereClause = "is_built_in = 0",
            pendingTypes = setOf(TRAINING_PLAN_TYPE, "training_plans"),
        )

        deleteMissingRows(
            table = "exercise_alternatives",
            incomingIds = bootstrap.exercises.flatMap(ExerciseDto::alternatives)
                .mapTo(hashSetOf()) { it.id },
            preservedIds = builtIn.alternatives.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(ALTERNATIVE_TYPE, "exercise_alternatives"),
        )
        deleteMissingRows(
            table = "exercises",
            incomingIds = bootstrap.exercises.mapTo(hashSetOf()) { it.id },
            preservedIds = builtIn.exercises.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(EXERCISE_TYPE, "exercises"),
        )
        deleteMissingRows(
            table = "equipment",
            incomingIds = bootstrap.equipment.mapTo(hashSetOf()) { it.id },
            preservedIds = builtIn.equipment.mapTo(hashSetOf()) { it.id },
            pendingTypes = setOf(EQUIPMENT_TYPE, "equipment"),
        )
    }

    private suspend fun deleteMissingRows(
        table: String,
        incomingIds: Set<String>,
        preservedIds: Set<String>,
        whereClause: String? = null,
        pendingTypes: Set<String> = emptySet(),
    ) {
        val sql = buildString {
            append("SELECT id FROM $table")
            if (whereClause != null) append(" WHERE $whereClause")
        }
        val existingIds = database.openHelper.readableDatabase.query(sql).use { cursor ->
            buildList {
                while (cursor.moveToNext()) add(cursor.getString(0))
            }
        }
        existingIds.asSequence()
            .filterNot { it in incomingIds || it in preservedIds }
            .forEach { staleId ->
                if (pendingTypes.any { hasPending(it, staleId) }) return@forEach
                database.openHelper.writableDatabase.execSQL(
                    "DELETE FROM $table WHERE id = ?",
                    arrayOf(staleId),
                )
            }
    }

    private suspend fun ensureCurrentPlanAssignment(
        serverUserId: String,
        bootstrap: BootstrapDto,
        now: Long,
    ) {
        if (!bootstrap.needsFallbackAssignment(serverUserId)) return
        val currentPlan = checkNotNull(bootstrap.currentPlan)
        val fallbackId = fallbackAssignmentId(serverUserId, currentPlan.id)
        val existingActive = database.planDao().activeAssignment(serverUserId)
        if (existingActive?.id == fallbackId && existingActive.planVersionId == currentPlan.id) return

        val timezone: ZoneId = runCatching { ZoneId.of(checkNotNull(bootstrap.user).timezone) }
            .getOrDefault(ZoneOffset.UTC)
        database.planDao().deactivateOtherAssignments(serverUserId, fallbackId, now)
        database.planDao().upsertAssignment(
            PlanAssignmentEntity(
                id = fallbackId,
                userId = serverUserId,
                planVersionId = currentPlan.id,
                startLocalDate = clock.instant().atZone(timezone).toLocalDate().toString(),
                isActive = true,
                version = 1,
                createdAt = now,
                updatedAt = now,
            ),
        )
    }

    private suspend fun reconcileUserTable(
        table: String,
        serverUserId: String,
        incomingIds: Set<String>,
        pendingTypes: Set<String> = emptySet(),
        beforeDelete: (suspend (String) -> Unit)? = null,
    ) {
        idsForParent(table, "user_id", serverUserId)
            .filterNot { it in incomingIds }
            .forEach { staleId ->
                if (pendingTypes.any { hasPending(it, staleId) }) return@forEach
                beforeDelete?.invoke(staleId)
                database.openHelper.writableDatabase.execSQL(
                    "DELETE FROM $table WHERE id = ?",
                    arrayOf(staleId),
                )
            }
    }

    private fun idsForParent(table: String, parentColumn: String, parentId: String): List<String> {
        val query = SimpleSQLiteQuery(
            "SELECT id FROM $table WHERE $parentColumn = ?",
            arrayOf(parentId),
        )
        return database.openHelper.readableDatabase.query(query).use { cursor ->
            buildList {
                while (cursor.moveToNext()) add(cursor.getString(0))
            }
        }
    }

    private fun Map<String, Any?>.toTrainingPlan(
        change: SyncChangeDto,
        now: Long,
    ): TrainingPlanEntity = TrainingPlanEntity(
        id = string("id") ?: change.entityId,
        name = string("name").orEmpty(),
        description = string("description").orEmpty(),
        isBuiltIn = boolean("is_built_in") ?: false,
        version = number("version")?.toLong() ?: change.version,
        createdAt = epochMillis("created_at") ?: now,
        updatedAt = epochMillis("updated_at") ?: now,
        deletedAt = epochMillis("deleted_at"),
    )

    private fun Map<String, Any?>.string(key: String): String? = this[key] as? String

    private fun Map<String, Any?>.boolean(key: String): Boolean? = when (val value = this[key]) {
        is Boolean -> value
        is Number -> value.toInt() != 0
        is String -> value.toBooleanStrictOrNull()
        else -> null
    }

    private fun Map<String, Any?>.number(key: String): Number? = this[key] as? Number

    private fun Map<String, Any?>.epochMillis(key: String): Long? = when (val value = this[key]) {
        is Number -> value.toLong()
        is String -> value.epochMillisOrNull()
        else -> null
    }

    private fun <T> JsonAdapter<T>.decode(payload: Map<String, Any?>, change: SyncChangeDto): T =
        requireNotNull(fromJsonValue(payload)) {
            "Unable to decode ${change.entityType} ${change.entityId}"
        }

    private fun tableFor(type: String): String? = when (type) {
        USER_TYPE -> "users"
        EQUIPMENT_TYPE -> "equipment"
        EXERCISE_TYPE -> "exercises"
        ALTERNATIVE_TYPE -> "exercise_alternatives"
        TRAINING_PLAN_TYPE -> "training_plans"
        PLAN_VERSION_TYPE -> "plan_versions"
        PLAN_DAY_TYPE -> "plan_days"
        PLAN_SLOT_TYPE -> "plan_slots"
        PLAN_OPTION_TYPE -> "plan_slot_options"
        ASSIGNMENT_TYPE -> "plan_assignments"
        WORKOUT_TYPE -> "workout_sessions"
        WORKOUT_SET_TYPE -> "workout_sets"
        READINESS_TYPE -> "daily_readiness"
        CARDIO_TYPE -> "cardio_sessions"
        else -> null
    }

    private fun canonicalType(value: String): String = when (
        value.trim().lowercase().replace('-', '_')
    ) {
        "users" -> USER_TYPE
        "equipments" -> EQUIPMENT_TYPE
        "exercises", "exercise_definition", "exercise_definitions" -> EXERCISE_TYPE
        "exercise_alternatives" -> ALTERNATIVE_TYPE
        "training_plans", "plan" -> TRAINING_PLAN_TYPE
        "plan_versions" -> PLAN_VERSION_TYPE
        "plan_days" -> PLAN_DAY_TYPE
        "plan_slots" -> PLAN_SLOT_TYPE
        "plan_slot_options" -> PLAN_OPTION_TYPE
        "plan_assignments", "assignment", "assignments" -> ASSIGNMENT_TYPE
        "workout_sessions" -> WORKOUT_TYPE
        "workout_sets" -> WORKOUT_SET_TYPE
        "readiness", "daily_readiness_entries" -> READINESS_TYPE
        "cardio", "cardio_sessions" -> CARDIO_TYPE
        else -> value.trim().lowercase().replace('-', '_')
    }

    private fun Long.saturatingAdd(other: Long): Long =
        if (this > Long.MAX_VALUE - other) Long.MAX_VALUE else this + other

    companion object {
        private const val SYNC_SCOPE = "global"
        private const val RETRY_DELAY_MILLIS = 30_000L
        private const val MAX_ERROR_LENGTH = 2_000
        private const val USER_TYPE = "user"
        private const val EQUIPMENT_TYPE = "equipment"
        private const val EXERCISE_TYPE = "exercise"
        private const val ALTERNATIVE_TYPE = "exercise_alternative"
        private const val TRAINING_PLAN_TYPE = "training_plan"
        private const val PLAN_VERSION_TYPE = "plan_version"
        private const val PLAN_DAY_TYPE = "plan_day"
        private const val PLAN_SLOT_TYPE = "plan_slot"
        private const val PLAN_OPTION_TYPE = "plan_slot_option"
        private const val ASSIGNMENT_TYPE = "plan_assignment"
        private const val WORKOUT_TYPE = "workout_session"
        private const val WORKOUT_SET_TYPE = "workout_set"
        private const val READINESS_TYPE = "daily_readiness"
        private const val CARDIO_TYPE = "cardio_session"
    }
}

internal fun defaultRepositoryMoshi(): Moshi = Moshi.Builder()
    .addLast(KotlinJsonAdapterFactory())
    .build()

private fun BootstrapDto.needsFallbackAssignment(serverUserId: String): Boolean =
    currentPlan != null &&
        currentPlan.deletedAt == null &&
        assignments.none {
            it.userId == serverUserId && it.isActive && it.deletedAt == null
        }

private fun BootstrapDto.serverPlanVersions(): List<PlanVersionDto> =
    (planVersions + listOfNotNull(currentPlan)).distinctBy(PlanVersionDto::id)

private fun fallbackAssignmentId(userId: String, planVersionId: String): String =
    stableId("server-fallback-assignment:$userId:$planVersionId")

private fun stableId(value: String): String = UUID.nameUUIDFromBytes(
    value.toByteArray(StandardCharsets.UTF_8),
).toString()
