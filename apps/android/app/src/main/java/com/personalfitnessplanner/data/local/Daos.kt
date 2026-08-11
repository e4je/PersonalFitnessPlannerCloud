package com.personalfitnessplanner.data.local

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import androidx.room.Update
import com.personalfitnessplanner.data.defaultplan.DefaultPlanSeed
import kotlinx.coroutines.flow.Flow

@Dao
interface UserDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(user: UserEntity)

    @Query("SELECT * FROM users WHERE id = :id AND deleted_at IS NULL LIMIT 1")
    suspend fun getById(id: String): UserEntity?

    @Query(
        """
        SELECT * FROM users
        WHERE deleted_at IS NULL
        ORDER BY CASE WHEN id = :localUserId THEN 1 ELSE 0 END, updated_at DESC
        LIMIT 1
        """,
    )
    suspend fun getCurrent(localUserId: String): UserEntity?

    @Query(
        """
        SELECT * FROM users
        WHERE deleted_at IS NULL
        ORDER BY CASE WHEN id = :localUserId THEN 1 ELSE 0 END, updated_at DESC
        LIMIT 1
        """,
    )
    fun observeCurrent(localUserId: String): Flow<UserEntity?>

    @Query("SELECT * FROM users WHERE deleted_at IS NULL ORDER BY updated_at DESC")
    fun observeAll(): Flow<List<UserEntity>>
}

@Dao
interface CatalogDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertEquipment(equipment: List<EquipmentEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertExercises(exercises: List<ExerciseEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAlternatives(alternatives: List<ExerciseAlternativeEntity>)

    @Query("SELECT * FROM exercises WHERE deleted_at IS NULL ORDER BY body_part, name")
    fun observeExercises(): Flow<List<ExerciseEntity>>

    @Query("SELECT * FROM equipment WHERE deleted_at IS NULL ORDER BY name")
    fun observeEquipment(): Flow<List<EquipmentEntity>>

    @Query(
        """
        SELECT e.* FROM exercises e
        INNER JOIN exercise_alternatives a ON a.alternative_exercise_id = e.id
        WHERE a.exercise_id = :exerciseId
          AND a.deleted_at IS NULL
          AND e.deleted_at IS NULL
        ORDER BY a.sort_order
        """,
    )
    suspend fun alternativesFor(exerciseId: String): List<ExerciseEntity>
}

@Dao
abstract class PlanDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertPlans(plans: List<TrainingPlanEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertVersions(versions: List<PlanVersionEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertDays(days: List<PlanDayEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertSlots(slots: List<PlanSlotEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertOptions(options: List<PlanSlotOptionEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertAssignments(assignments: List<PlanAssignmentEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertAssignment(assignment: PlanAssignmentEntity)

    @Query("SELECT name FROM training_plans WHERE id = :planId AND deleted_at IS NULL LIMIT 1")
    abstract suspend fun planName(planId: String): String?

    @Query(
        """
        SELECT tp.is_built_in FROM training_plans tp
        INNER JOIN plan_versions pv ON pv.plan_id = tp.id
        WHERE pv.id = :planVersionId
          AND pv.deleted_at IS NULL
          AND tp.deleted_at IS NULL
        LIMIT 1
        """,
    )
    abstract suspend fun isBuiltInVersion(planVersionId: String): Boolean?

    @Query(
        """
        UPDATE plan_assignments
        SET is_active = 0, updated_at = :updatedAt, version = version + 1
        WHERE user_id = :userId
          AND id != :activeAssignmentId
          AND is_active = 1
          AND deleted_at IS NULL
        """,
    )
    abstract suspend fun deactivateOtherAssignments(
        userId: String,
        activeAssignmentId: String,
        updatedAt: Long,
    )

    @Transaction
    open suspend fun replaceDefaultPlan(seed: DefaultPlanSeed) {
        upsertPlans(listOf(seed.plan))
        upsertVersions(listOf(seed.planVersion))
        upsertDays(seed.days)
        upsertSlots(seed.slots)
        upsertOptions(seed.options)
    }

    @Transaction
    @Query("SELECT * FROM plan_versions WHERE id = :versionId AND deleted_at IS NULL LIMIT 1")
    abstract suspend fun getVersionWithDays(versionId: String): PlanVersionWithDays?

    @Transaction
    @Query(
        """
        SELECT pv.* FROM plan_versions pv
        INNER JOIN plan_assignments pa ON pa.plan_version_id = pv.id
        WHERE pa.user_id = :userId
          AND pa.is_active = 1
          AND pa.deleted_at IS NULL
          AND pv.deleted_at IS NULL
        ORDER BY pa.updated_at DESC
        LIMIT 1
        """,
    )
    abstract suspend fun currentPlanForUser(userId: String): PlanVersionWithDays?

    @Query(
        """
        SELECT * FROM plan_assignments
        WHERE user_id = :userId
          AND is_active = 1
          AND deleted_at IS NULL
        ORDER BY updated_at DESC
        LIMIT 1
        """,
    )
    abstract suspend fun activeAssignment(userId: String): PlanAssignmentEntity?

    @Query(
        """
        SELECT * FROM plan_slot_options
        WHERE plan_slot_id = :planSlotId
          AND exercise_id = :exerciseId
          AND deleted_at IS NULL
        LIMIT 1
        """,
    )
    abstract suspend fun optionForSlotAndExercise(
        planSlotId: String,
        exerciseId: String,
    ): PlanSlotOptionEntity?
}

@Dao
abstract class WorkoutDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertSession(session: WorkoutSessionEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    abstract suspend fun upsertSets(sets: List<WorkoutSetEntity>)

    @Update
    abstract suspend fun updateSet(set: WorkoutSetEntity)

    @Transaction
    open suspend fun insertSessionWithSets(
        session: WorkoutSessionEntity,
        sets: List<WorkoutSetEntity>,
    ) {
        upsertSession(session)
        upsertSets(sets)
    }

    @Query(
        """
        SELECT * FROM workout_sessions
        WHERE user_id = :userId AND deleted_at IS NULL
        ORDER BY local_date DESC, started_at DESC
        """,
    )
    abstract fun observeSessions(userId: String): Flow<List<WorkoutSessionEntity>>

    @Transaction
    @Query("SELECT * FROM workout_sessions WHERE id = :sessionId AND deleted_at IS NULL LIMIT 1")
    abstract suspend fun getSessionWithSets(sessionId: String): WorkoutSessionWithSets?

    @Query("SELECT session_id FROM workout_sets WHERE id = :setId LIMIT 1")
    abstract suspend fun sessionIdForSet(setId: String): String?

    @Query(
        """
        SELECT * FROM workout_sessions
        WHERE user_id = :userId
          AND status = 'COMPLETED'
          AND deleted_at IS NULL
        ORDER BY completed_at DESC
        LIMIT 1
        """,
    )
    abstract suspend fun latestCompletedSession(userId: String): WorkoutSessionEntity?

    @Transaction
    @Query(
        """
        SELECT * FROM workout_sessions
        WHERE user_id = :userId
          AND status = 'IN_PROGRESS'
          AND deleted_at IS NULL
        ORDER BY started_at DESC
        LIMIT 1
        """,
    )
    abstract suspend fun activeSessionForUser(userId: String): WorkoutSessionWithSets?

    @Query(
        """
        SELECT * FROM workout_sessions
        WHERE user_id = :userId
          AND status = 'COMPLETED'
          AND local_date >= :sinceLocalDate
          AND deleted_at IS NULL
        ORDER BY local_date DESC, completed_at DESC
        """,
    )
    abstract suspend fun completedSessionsSince(
        userId: String,
        sinceLocalDate: String,
    ): List<WorkoutSessionEntity>

    @Query(
        """
        SELECT ws.* FROM workout_sets ws
        INNER JOIN workout_sessions session ON session.id = ws.session_id
        WHERE session.user_id = :userId
          AND ws.exercise_id = :exerciseId
          AND ws.is_warmup = 0
          AND ws.completed = 1
          AND ws.deleted_at IS NULL
          AND session.status = 'COMPLETED'
          AND session.deleted_at IS NULL
        ORDER BY ws.completed_at DESC, session.completed_at DESC, ws.set_number DESC
        LIMIT 1
        """,
    )
    abstract suspend fun latestCompletedWorkingSet(
        userId: String,
        exerciseId: String,
    ): WorkoutSetEntity?

    @Query(
        """
        SELECT ws.* FROM workout_sets ws
        INNER JOIN workout_sessions session ON session.id = ws.session_id
        WHERE session.user_id = :userId
          AND ws.exercise_id = :exerciseId
          AND ws.is_warmup = 0
          AND ws.completed = 1
          AND ws.deleted_at IS NULL
          AND session.status = 'COMPLETED'
          AND session.deleted_at IS NULL
        ORDER BY ws.completed_at DESC
        LIMIT :limit
        """,
    )
    abstract suspend fun weightHistoryForExercise(
        userId: String,
        exerciseId: String,
        limit: Int = 20,
    ): List<WorkoutSetEntity>

    @Query(
        """
        UPDATE workout_sessions
        SET deleted_at = :deletedAt, updated_at = :deletedAt, version = version + 1
        WHERE id = :sessionId
        """,
    )
    abstract suspend fun softDeleteSession(sessionId: String, deletedAt: Long)
}

@Dao
interface ReadinessDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(readiness: DailyReadinessEntity)

    @Query(
        """
        SELECT * FROM daily_readiness
        WHERE user_id = :userId AND local_date = :localDate AND deleted_at IS NULL
        LIMIT 1
        """,
    )
    suspend fun forDate(userId: String, localDate: String): DailyReadinessEntity?
}

@Dao
interface CardioDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(session: CardioSessionEntity)

    @Query(
        """
        SELECT * FROM cardio_sessions
        WHERE user_id = :userId AND deleted_at IS NULL
        ORDER BY local_date DESC, started_at DESC
        """,
    )
    fun observeSessions(userId: String): Flow<List<CardioSessionEntity>>
}

@Dao
interface SyncDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun enqueue(item: SyncOutboxEntity): Long

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertOutbox(item: SyncOutboxEntity)

    @Query(
        """
        SELECT * FROM sync_outbox
        WHERE status IN ('PENDING', 'FAILED')
          AND next_attempt_at <= :now
          AND deleted_at IS NULL
        ORDER BY created_at
        LIMIT :limit
        """,
    )
    suspend fun readyItems(now: Long, limit: Int = 50): List<SyncOutboxEntity>

    @Query(
        """
        UPDATE sync_outbox
        SET status = 'FAILED',
            attempt_count = attempt_count + 1,
            last_error = :error,
            next_attempt_at = :nextAttemptAt,
            updated_at = :updatedAt,
            version = version + 1
        WHERE id = :id
        """,
    )
    suspend fun markFailed(
        id: String,
        error: String,
        nextAttemptAt: Long,
        updatedAt: Long,
    )

    @Query(
        """
        SELECT COUNT(*) FROM sync_outbox
        WHERE status IN ('PENDING', 'IN_FLIGHT', 'FAILED') AND deleted_at IS NULL
        """,
    )
    fun allPendingCount(): Flow<Int>

    @Query(
        """
        SELECT COUNT(*) FROM sync_outbox
        WHERE status IN ('PENDING', 'IN_FLIGHT', 'FAILED') AND deleted_at IS NULL
        """,
    )
    suspend fun pendingMutationCount(): Int

    @Query("DELETE FROM sync_outbox WHERE id = :id")
    suspend fun acknowledge(id: String)

    @Query(
        """
        SELECT EXISTS(
            SELECT 1 FROM sync_outbox
            WHERE aggregate_type = :aggregateType
              AND aggregate_id = :aggregateId
              AND status IN ('PENDING', 'IN_FLIGHT', 'FAILED')
              AND deleted_at IS NULL
        )
        """,
    )
    suspend fun hasPendingForAggregate(
        aggregateType: String,
        aggregateId: String,
    ): Boolean

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertState(state: SyncStateEntity)

    @Query(
        """
        SELECT * FROM sync_state
        WHERE user_id = :userId AND scope = :scope AND deleted_at IS NULL
        LIMIT 1
        """,
    )
    suspend fun state(userId: String, scope: String): SyncStateEntity?
}

@Dao
interface SettingsDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(settings: AppSettingsEntity)

    @Query("SELECT * FROM app_settings WHERE id = :id AND deleted_at IS NULL LIMIT 1")
    fun observe(id: String): Flow<AppSettingsEntity?>
}
