package com.personalfitnessplanner.ui.model

import androidx.compose.runtime.Immutable

enum class AppDestination(val label: String) {
    Onboarding("开始设置"),
    Home("首页"),
    Today("今日训练"),
    WorkoutExecution("训练执行"),
    History("历史"),
    ExerciseLibrary("动作库"),
    Settings("设置"),
}

enum class WeightUnit(val label: String) { Kilogram("公斤"), Pound("磅") }
enum class SyncStatus { Synced, Syncing, Offline, Failed }
enum class ThemeMode(val label: String) { System("跟随系统"), Light("浅色"), Dark("深色") }
enum class ExportFormat { CSV, JSON }
enum class SettingsKey {
    ApiBaseUrl, Timezone, WeightUnit, TrainingDays, RestSeconds, ThemeMode,
    AutoSync, LocalBackup, ClearCache,
}

@Immutable
data class OnboardingConfig(
    val apiBaseUrl: String = "https://api.example.com/",
    val account: String = "",
    val password: String = "",
    val weightUnit: WeightUnit = WeightUnit.Kilogram,
    val timezone: String = "Asia/Shanghai",
    val trainingDays: Set<String> = setOf("周一", "周三", "周五"),
)

@Immutable
data class OnboardingUiState(
    val config: OnboardingConfig = OnboardingConfig(),
    val step: Int = 1,
    val totalSteps: Int = 3,
    val isSubmitting: Boolean = false,
    val serverReachable: Boolean? = null,
    val errorMessage: String? = null,
)

@Immutable
data class HomeUiState(
    val dateText: String = "8 月 9 日 · 星期日",
    val greeting: String = "下午好，准备好了吗？",
    val recommendation: String = "A · 胸部优先",
    val recommendationReason: String = "距离上次力量训练 2 天，状态适合训练",
    val planName: String = "全身增肌减脂计划",
    val planVersion: String = "v3 · 第 2 周",
    val completedThisWeek: Int = 2,
    val weeklyLimit: Int = 3,
    val daysSinceLastWorkout: Int = 2,
    val nextWorkout: String = "B · 背部优先",
    val fatigueScore: Int = 4,
    val syncStatus: SyncStatus = SyncStatus.Synced,
    val syncMessage: String = "今天 14:32 已同步",
    val hasActiveWorkout: Boolean = false,
)

@Immutable
data class AlternativeExerciseUi(
    val id: String,
    val name: String,
    val equipment: String,
)

@Immutable
data class ExerciseSlotUi(
    val id: String,
    val order: Int,
    val bodyPart: String,
    val exerciseName: String,
    val equipment: String,
    val sets: Int,
    val reps: String,
    val alternatives: List<AlternativeExerciseUi>,
    val cue: String,
    val previousPerformance: String,
    val suggestedWeight: String,
    val setupNote: String,
    /** Exact exercise UUID currently selected for this plan slot. */
    val selectedExerciseId: String = id,
    val status: String = "待开始",
)

fun sampleAExercises(): List<ExerciseSlotUi> = listOf(
    ExerciseSlotUi(
        id = "bench-press", order = 1, bodyPart = "胸部整体", exerciseName = "杠铃平板卧推",
        equipment = "平板卧推架 · 杠铃", sets = 3, reps = "8–10 次",
        alternatives = listOf(
            AlternativeExerciseUi("smith-bench", "史密斯平板卧推", "史密斯机 · 平凳"),
            AlternativeExerciseUi("dumbbell-bench", "哑铃平板卧推", "哑铃 · 平凳"),
            AlternativeExerciseUi("chest-press", "坐姿推胸", "坐姿推胸机"),
        ),
        cue = "肩胛后缩下沉，胸口打开；手腕中立，肘部与躯干约 30–60°。",
        previousPerformance = "上次 40 kg × 10 / 9 / 8", suggestedWeight = "建议 40 kg",
        setupNote = "卧推架 4 号 · 安全杆 6 档",
    ),
    ExerciseSlotUi(
        id = "lat-pulldown", order = 2, bodyPart = "背部宽度", exerciseName = "高位下拉",
        equipment = "高位下拉机", sets = 3, reps = "8–12 次",
        alternatives = listOf(
            AlternativeExerciseUi("assisted-pullup", "辅助引体向上", "辅助引体机"),
            AlternativeExerciseUi("neutral-pulldown", "对握高位下拉", "高位下拉机 · 对握把"),
        ),
        cue = "胸口微抬，先沉肩再屈肘；肘部向下、向髋部移动。",
        previousPerformance = "上次 32 kg × 12 / 11 / 10", suggestedWeight = "建议 34 kg",
        setupNote = "大腿垫 5 档 · 宽握杆",
    ),
    ExerciseSlotUi(
        id = "leg-press", order = 3, bodyPart = "大腿前侧 · 臀部", exerciseName = "坐姿腿举",
        equipment = "45° 倒蹬机", sets = 3, reps = "8–12 次",
        alternatives = listOf(
            AlternativeExerciseUi("hack-squat", "哈克深蹲", "哈克深蹲机"),
            AlternativeExerciseUi("goblet-squat", "高脚杯深蹲", "哑铃或壶铃"),
        ),
        cue = "脚掌踩稳，膝盖跟随脚尖；腰臀稳定，顶端不要猛烈锁膝。",
        previousPerformance = "上次 80 kg × 12 / 12 / 11", suggestedWeight = "建议 80 kg",
        setupNote = "靠背 3 档 · 双脚肩宽",
    ),
    ExerciseSlotUi(
        id = "seated-leg-curl", order = 4, bodyPart = "大腿后侧", exerciseName = "坐姿腿弯举",
        equipment = "坐姿腿弯举机", sets = 2, reps = "10–15 次",
        alternatives = listOf(
            AlternativeExerciseUi("lying-leg-curl", "俯卧腿弯举", "俯卧腿弯举机"),
            AlternativeExerciseUi("dumbbell-rdl", "哑铃罗马尼亚硬拉", "哑铃"),
        ),
        cue = "器械转轴对齐膝关节，臀部稳定，控制回放。",
        previousPerformance = "上次 25 kg × 14 / 12", suggestedWeight = "建议 25 kg",
        setupNote = "靠背 4 档 · 脚垫 3 档",
    ),
    ExerciseSlotUi(
        id = "lateral-raise", order = 5, bodyPart = "肩部中束", exerciseName = "哑铃侧平举",
        equipment = "哑铃", sets = 2, reps = "12–20 次",
        alternatives = listOf(AlternativeExerciseUi("machine-lateral", "器械侧平举", "侧平举机")),
        cue = "肘部带动，手臂位于身体侧前方；不要耸肩或摆动。",
        previousPerformance = "上次 5 kg × 16 / 14", suggestedWeight = "建议 5 kg",
        setupNote = "站姿 · 双臂同步",
    ),
    ExerciseSlotUi(
        id = "rope-pushdown", order = 6, bodyPart = "肱三头肌", exerciseName = "绳索下压",
        equipment = "龙门架 · 绳索把手", sets = 2, reps = "10–15 次",
        alternatives = listOf(AlternativeExerciseUi("bar-pushdown", "直杆下压", "龙门架 · 短直杆")),
        cue = "手肘固定、肩膀下沉、手腕中立，不要用躯干压重量。",
        previousPerformance = "上次 18 kg × 15 / 13", suggestedWeight = "建议 18 kg",
        setupNote = "滑轮最高位",
    ),
    ExerciseSlotUi(
        id = "calf-raise", order = 7, bodyPart = "小腿", exerciseName = "站姿提踵",
        equipment = "站姿提踵机", sets = 2, reps = "10–15 次",
        alternatives = listOf(AlternativeExerciseUi("legpress-calf", "腿举机提踵", "腿举机")),
        cue = "脚跟充分下降和抬起，顶端停顿，脚踝不要内外翻。",
        previousPerformance = "上次 35 kg × 15 / 15", suggestedWeight = "建议 40 kg",
        setupNote = "肩垫 6 档",
    ),
    ExerciseSlotUi(
        id = "cable-crunch", order = 8, bodyPart = "腹部", exerciseName = "绳索卷腹",
        equipment = "龙门架 · 绳索把手", sets = 2, reps = "10–15 次",
        alternatives = listOf(
            AlternativeExerciseUi("machine-crunch", "器械卷腹", "卷腹机"),
            AlternativeExerciseUi("plank", "平板支撑", "瑜伽垫"),
        ),
        cue = "肋骨向骨盆靠近，骨盆稳定；不要只用手臂或髋屈肌。",
        previousPerformance = "上次 25 kg × 15 / 13", suggestedWeight = "建议 25 kg",
        setupNote = "跪姿 · 距滑轮约半米",
    ),
)

@Immutable
data class TodayWorkoutUiState(
    val dateText: String = "今天 · 8 月 9 日",
    val workoutLabel: String = "训练 A · 胸部优先",
    val planName: String = "全身增肌减脂计划",
    val planVersion: String = "v3",
    val weekNote: String = "第 2 周 · 前两周每个动作 2 个正式组",
    val estimatedMinutes: Int = 62,
    val exercises: List<ExerciseSlotUi> = sampleAExercises(),
)

@Immutable
data class WorkoutSetDraft(
    val weight: String = "40",
    val reps: String = "10",
    val isWarmup: Boolean = false,
    val rir: Int = 2,
    val quality: String = "良好",
    val pain: Int = 0,
    val note: String = "",
)

@Immutable
data class WorkoutSetUi(
    val id: String,
    val number: Int,
    val draft: WorkoutSetDraft,
    val completed: Boolean = false,
    val isEditable: Boolean = true,
)

@Immutable
data class WorkoutExecutionUiState(
    val sessionId: String = "session-local-001",
    val workoutLabel: String = "训练 A",
    val exercisePosition: String = "动作 1 / 8",
    val exerciseName: String = "杠铃平板卧推",
    val equipment: String = "平板卧推架 · 杠铃",
    val target: String = "3 × 8–10",
    val cue: String = "肩胛后缩下沉，胸口打开；手腕中立。",
    val setupNote: String = "卧推架 4 号 · 安全杆 6 档",
    val elapsedTime: String = "18:24",
    val restSecondsRemaining: Int = 74,
    val isResting: Boolean = true,
    val autosaveMessage: String = "已自动保存到本机",
    val sets: List<WorkoutSetUi> = listOf(
        WorkoutSetUi("set-1", 1, WorkoutSetDraft(weight = "40", reps = "10", rir = 3), completed = true),
        WorkoutSetUi("set-2", 2, WorkoutSetDraft(weight = "40", reps = "9", rir = 2)),
        WorkoutSetUi("set-3", 3, WorkoutSetDraft(weight = "40", reps = "8", rir = 2)),
    ),
)

@Immutable
data class HistoryFilterUi(
    val period: String = "近 30 天",
    val workoutType: String = "全部",
    val exercise: String = "全部动作",
)

@Immutable
data class HistorySessionUi(
    val id: String,
    val date: String,
    val workoutType: String,
    val duration: String,
    val completedSets: Int,
    val totalVolume: String,
    val status: String,
    val syncDetail: String? = null,
)

@Immutable
data class TrendPointUi(val label: String, val value: Float, val displayValue: String)

@Immutable
data class HistoryUiState(
    val filter: HistoryFilterUi = HistoryFilterUi(),
    val summary: String = "近 30 天完成 9 次 · 训练一致性 82%",
    val trendExercise: String = "杠铃平板卧推 · 最佳正式组",
    val trend: List<TrendPointUi> = listOf(
        TrendPointUi("7/12", 35f, "35 kg × 10"),
        TrendPointUi("7/20", 37.5f, "37.5 kg × 10"),
        TrendPointUi("7/29", 40f, "40 kg × 9"),
        TrendPointUi("8/06", 40f, "40 kg × 10"),
    ),
    val sessions: List<HistorySessionUi> = listOf(
        HistorySessionUi("history-1", "8 月 6 日 · 周四", "A · 胸部优先", "58 分钟", 17, "4,860 kg", "已同步"),
        HistorySessionUi("history-2", "8 月 3 日 · 周一", "B · 背部优先", "64 分钟", 18, "5,240 kg", "待同步", "离线记录，将在联网后重试"),
        HistorySessionUi("history-3", "7 月 31 日 · 周五", "A · 胸部优先", "51 分钟", 16, "4,520 kg", "已同步"),
    ),
)

@Immutable
data class LibraryExerciseUi(
    val id: String,
    val name: String,
    val bodyPart: String,
    val equipment: String,
    val defaultPrescription: String,
    val cue: String,
    val commonMistakes: String,
    val alternatives: String,
    val version: String,
    val personalEquipmentNote: String = "",
)

@Immutable
data class ExerciseLibraryUiState(
    val query: String = "",
    val selectedBodyPart: String = "全部",
    val exercises: List<LibraryExerciseUi> = listOf(
        LibraryExerciseUi("bench-press", "杠铃平板卧推", "胸部", "杠铃 · 平凳", "3 × 8–10", "肩胛后缩下沉，胸口打开。", "耸肩、手腕后折、肩膀前顶", "史密斯卧推、哑铃卧推、坐姿推胸", "v3"),
        LibraryExerciseUi("lat-pulldown", "高位下拉", "背部", "高位下拉机", "3 × 8–12", "先沉肩再屈肘，肘部向髋部移动。", "拉到颈后、大幅后仰", "辅助引体、对握下拉", "v2", "公司健身房用 2 号机"),
        LibraryExerciseUi("leg-press", "坐姿腿举", "腿部", "45° 倒蹬机", "3 × 8–12", "脚掌踩稳，膝盖跟随脚尖。", "骨盆卷起、猛烈锁膝", "哈克深蹲、高脚杯深蹲", "v4"),
        LibraryExerciseUi("lateral-raise", "哑铃侧平举", "肩部", "哑铃", "2 × 12–20", "肘部带动，手臂位于身体侧前方。", "耸肩、摆动借力", "器械侧平举、单臂绳索侧平举", "v1"),
    ),
)

@Immutable
data class SettingsUiState(
    val apiBaseUrl: String = "https://api.example.com/",
    val accountName: String = "fitness@example.com",
    val syncStatus: SyncStatus = SyncStatus.Synced,
    val lastSync: String = "今天 14:32",
    val timezone: String = "Asia/Shanghai",
    val weightUnit: WeightUnit = WeightUnit.Kilogram,
    val trainingDays: String = "周一、周三、周五",
    val restSeconds: Int = 90,
    val themeMode: ThemeMode = ThemeMode.System,
    val autoSync: Boolean = true,
    val cacheSize: String = "18.4 MB",
    val appVersion: String = "1.0.0-debug",
)

@Immutable
data class FitnessAppUiState(
    val currentDestination: AppDestination = AppDestination.Onboarding,
    val onboarding: OnboardingUiState = OnboardingUiState(),
    val home: HomeUiState = HomeUiState(),
    val today: TodayWorkoutUiState = TodayWorkoutUiState(),
    val execution: WorkoutExecutionUiState = WorkoutExecutionUiState(),
    val history: HistoryUiState = HistoryUiState(),
    val library: ExerciseLibraryUiState = ExerciseLibraryUiState(),
    val settings: SettingsUiState = SettingsUiState(),
) {
    companion object {
        fun preview(destination: AppDestination = AppDestination.Onboarding) =
            FitnessAppUiState(currentDestination = destination)
    }
}
