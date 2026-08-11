# Android 验收测试矩阵

> 历史归档：表内“已通过”均指整合前 Android 原始交付。统一副本新增了测试和同步实现，尚未完成最终 Gradle test/lint/assemble；当前状态见根 `docs/test-report.md`。

本矩阵将 [`../../agent.md`](../../agent.md) 的验收要求映射到可重复执行的测试。JVM 测试位于
`app/src/test`，Compose 设备测试位于 `app/src/androidTest`。

| 验收要求 | 测试证据 | 覆盖要点 | 状态 |
|---|---|---|---|
| A/B 推荐 | `TrainingRecommendationEngineTest.firstStrengthSessionDefaultsToA`；`alternatesFromActuallyCompletedDay` | 第一次为 A；按最近一次实际完成的 A/B 交替 | JVM 已通过 |
| 连续训练保护 | `TrainingRecommendationEngineTest.yesterdayFullBodyTriggersRecoveryWithoutLosingNextAB`；`threeCompletedThisWeekTriggersRecovery` | 昨日全身训练转恢复；每周三次上限；恢复不丢失下一次 A/B | JVM 已通过 |
| 高疲劳恢复 | `TrainingRecommendationEngineTest.fatigueEightThroughTenTriggersRecovery`；`manualOverrideWinsOverSafetyDefaults` | 疲劳 8、9、10 均推荐恢复；用户可明确覆盖；同步的每日状态会接入推荐 | JVM 已通过；本地状态录入界面未提供 |
| 替代动作重量隔离 | `DoubleProgressionEngineTest.historiesAreIsolatedByExactAlternativeExerciseId`；`WorkoutDaoTest.latestWeightIsScopedToExactSelectedExercise` | 领域查询和 Room 查询均以精确动作 UUID 隔离，不按训练位置或替代组继承 | JVM 已通过 |
| 离线训练同步 | `SyncCoordinatorTest.offlineFailure_keepsOutboxAndEnqueuesConnectedRetry`；`LocalFitnessRepositoryTest.repeatedSetCompletionIsNoOpAndDoesNotQueueDuplicateMutation` | 网络失败保留 Outbox 并安排重试；本地训练创建及组完成均先持久化为 Outbox | JVM 已通过 |
| 幂等 | `SyncCoordinatorTest.retryUsesSameBatchAndOperationIdempotencyKeys_thenAcceptsDuplicate`；`WorkoutDaoTest.outboxIdempotencyKeyCannotBeQueuedTwice`；`LocalFitnessRepositoryTest.repeatedSetCompletionIsNoOpAndDoesNotQueueDuplicateMutation` | 重试复用批次/操作键；服务端 duplicate 视为成功；数据库唯一约束；重复点击不产生新变更 | JVM 已通过 |
| 计划版本 | `WorkoutDaoTest.sessionKeepsImmutablePlanVersionAndSnapshot`；`DefaultTrainingPlanTest.identifiersAreStableAcrossSeedRuns`；`LocalFitnessRepositoryTest.newPlanAssignmentDoesNotInterruptWorkoutAlreadyInProgress` | 历史训练保存版本和快照；内置版本 ID 稳定；云端新分配不打断进行中训练 | JVM 已通过 |
| Room Migration | `Migration1To2Test.migrationPreservesRowsAndAddsSyncSnapshotAndCursorStructures` | 1→2 原位保留训练；补充计划快照、幂等键、选中动作来源、Outbox、游标和设置表 | JVM 已通过（Robolectric 结构迁移） |
| 登录和刷新令牌 | `ApiClientMockWebServerTest.loginAndExplicitRefresh_useTheMinimumAuthContract`；`authenticated401_refreshesTokenAndRetriesExactlyOnce`；`second401_isReturnedWithoutRefreshLoop` | 登录/刷新合同；401 自动刷新后仅重试一次；避免刷新循环 | JVM 已通过 |
| HTTPS 强制 | `ApiClientMockWebServerTest.productionAndSettingsBaseUrls_rejectHttpIncludingLocalhost` | 生产解析器与设置规范化入口均拒绝 HTTP（包括 localhost）；HTTPS localhost 可用 | JVM 已通过 |
| Compose 关键页面 | `FitnessAppUiTest` 全部测试；`KeyScreensUiTest.bottomNavigationOpensHistoryAndReadOnlyExerciseLibrary` | 首次启动、首页、今日计划、训练执行、设置、深色主题、历史和只读动作库 | 设备 5/5 已通过 |
| 双重渐进 | `DoubleProgressionEngineTest` | 达标加档、未达标保持、半数以上低于下限或连续两次失败降档、疼痛禁止加重 | JVM 已通过 |
| 默认 A/B 计划 | `DefaultTrainingPlanTest` | A/B 各 8 个有序位置；首选与替代；前两周 2 组；器械、组次和动作提示完整 | JVM 已通过 |
| APK 安装启动 | Debug APK 安装到 REDMI Android 14 / API 34（`emulator-5554`）并冷启动 | Manifest、资源、Room 初始化、本地首页、今日计划与执行页 | 已通过 |

## 执行门禁

```powershell
.\gradlew.bat test
.\gradlew.bat connectedDebugAndroidTest
.\gradlew.bat lint
.\gradlew.bat assembleDebug
.\gradlew.bat assembleRelease
```

原始交付基线曾执行全部五条命令：Debug/Release JVM 报告各 43 项、0 失败/0 跳过，Compose 设备测试 5 项全部通过，lint 为 0 错误/10 条非阻塞警告，两个 APK 组装任务通过。这些数字不得外推到统一副本；后续需按根构建手册重新执行。
