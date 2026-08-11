package com.personalfitnessplanner.data.local

import androidx.room.TypeConverter

class Converters {
    @TypeConverter fun fromUnitSystem(value: UnitSystem?): String? = value?.name
    @TypeConverter fun toUnitSystem(value: String?): UnitSystem? = value?.let(UnitSystem::valueOf)

    @TypeConverter fun fromPlanCode(value: PlanCode?): String? = value?.name
    @TypeConverter fun toPlanCode(value: String?): PlanCode? = value?.let(PlanCode::valueOf)

    @TypeConverter fun fromWorkoutStatus(value: WorkoutStatus?): String? = value?.name
    @TypeConverter fun toWorkoutStatus(value: String?): WorkoutStatus? = value?.let(WorkoutStatus::valueOf)

    @TypeConverter fun fromSetQuality(value: SetQuality?): String? = value?.name
    @TypeConverter fun toSetQuality(value: String?): SetQuality? = value?.let(SetQuality::valueOf)

    @TypeConverter fun fromSyncOperation(value: SyncOperation?): String? = value?.name
    @TypeConverter fun toSyncOperation(value: String?): SyncOperation? = value?.let(SyncOperation::valueOf)

    @TypeConverter fun fromOutboxStatus(value: OutboxStatus?): String? = value?.name
    @TypeConverter fun toOutboxStatus(value: String?): OutboxStatus? = value?.let(OutboxStatus::valueOf)

    @TypeConverter fun fromThemeMode(value: ThemeMode?): String? = value?.name
    @TypeConverter fun toThemeMode(value: String?): ThemeMode? = value?.let(ThemeMode::valueOf)
}
