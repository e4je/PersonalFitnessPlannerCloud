package com.personalfitnessplanner.data.local

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.TypeConverters

@Database(
    entities = [
        UserEntity::class,
        ExerciseEntity::class,
        EquipmentEntity::class,
        ExerciseAlternativeEntity::class,
        TrainingPlanEntity::class,
        PlanVersionEntity::class,
        PlanDayEntity::class,
        PlanSlotEntity::class,
        PlanSlotOptionEntity::class,
        PlanAssignmentEntity::class,
        WorkoutSessionEntity::class,
        WorkoutSetEntity::class,
        DailyReadinessEntity::class,
        CardioSessionEntity::class,
        SyncOutboxEntity::class,
        SyncStateEntity::class,
        AppSettingsEntity::class,
    ],
    version = 2,
    exportSchema = true,
)
@TypeConverters(Converters::class)
abstract class AppDatabase : RoomDatabase() {
    abstract fun userDao(): UserDao
    abstract fun catalogDao(): CatalogDao
    abstract fun planDao(): PlanDao
    abstract fun workoutDao(): WorkoutDao
    abstract fun readinessDao(): ReadinessDao
    abstract fun cardioDao(): CardioDao
    abstract fun syncDao(): SyncDao
    abstract fun settingsDao(): SettingsDao

    companion object {
        const val DATABASE_NAME = "personal-fitness-planner.db"
        val MIGRATION_1_2 = com.personalfitnessplanner.data.local.MIGRATION_1_2

        @Volatile
        private var instance: AppDatabase? = null

        fun build(context: Context): AppDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    DATABASE_NAME,
                )
                    .addMigrations(MIGRATION_1_2)
                    .build()
                    .also { instance = it }
            }

        internal fun clearInstanceForTests() {
            instance?.close()
            instance = null
        }
    }
}
