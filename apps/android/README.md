# Personal Fitness Planner for Android

原生 Kotlin / Jetpack Compose 健身客户端，面向每周三次全身训练。应用可在没有后端时使用内置 A/B 计划完整记录训练；连接兼容的 REST API 后，可通过 Outbox 增量同步。

## 功能

- 首次启动设置 API、账号、单位、IANA 时区和训练日；后端不可达可进入本地模式。
- 首页按已有完成记录、间隔天数和周频率推荐 A、B、恢复或有氧，允许手动覆盖；推荐引擎也支持疲劳评分输入。
- 今日训练展示部位、动作、器械、组次、替代动作、提示、历史负重和建议负重。
- 训练中逐组自动保存重量、次数、热身、RIR、动作质量、疼痛和备注；支持恢复未完成训练、休息通知和中途结束。
- 训练历史真实周期/A-B 筛选、精确动作趋势、详情、软删除与 CSV/JSON 导出。
- 只读动作库与本地个人器械备注；公斤/磅、时区、训练日、深色模式和同步设置。
- Room 版本化计划快照、幂等 Outbox、增量游标、WorkManager 断网重试；Android Keystore 加密令牌。

## 项目结构

```text
android/
├─ app/                         # 应用源码、JVM/设备测试、Room schema
├─ gradle/                      # 已校验的 Gradle Wrapper
├─ docs/                        # 架构、API、签名说明
├─ scripts/                     # Windows 构建与产物复制脚本
├─ tests/                       # 测试索引/验收记录
└─ artifacts/                   # 后续构建生成的 APK（当前为空）
```

## 环境

- JDK 17 或 21（JBR 25 与当前 Gradle/Kotlin 组合不兼容）
- Android SDK Platform 36、匹配的 Build Tools 36.x、Platform Tools
- Android 8.0 / API 26 或更高设备

Android Studio 打开本目录即可。命令行首次构建前复制 `local.properties.example` 为 `local.properties` 并填写 SDK 路径。

## 构建

```powershell
.\gradlew.bat test
.\gradlew.bat lint
.\gradlew.bat assembleDebug
.\gradlew.bat assembleRelease
.\scripts\copy-artifacts.ps1
```

或一次运行：

```powershell
.\scripts\build.ps1 -Task all
```

Release 变体没有签名配置，因此不会读取或泄漏本地密钥。签名流程见 [docs/SIGNING.md](docs/SIGNING.md)。

## API

默认地址为 `https://localhost/`，首次启动时可以修改；也可用 `-PPFP_API_BASE_URL=https://host/` 构建。详见 [docs/API_CONFIGURATION.md](docs/API_CONFIGURATION.md)。客户端只访问 HTTPS REST API，不包含 MySQL 凭据。

## 原始交付历史测试结果

以下结果来自整合前 `01_Android_APK/android` 的 2026-08-09 验收，只用于迁入基线；统一副本已经增加同步、cardio、fallback 和共享向量测试，当前 `app/build` 报告与 APK 均不存在，不能把下表当作本轮重跑结果。

| 门禁 | 结果 | 证据 |
|---|---|---|
| `gradlew test` | 通过 | Debug 43 项、Release 43 项，均为 0 失败/0 跳过；详见 `app/build/reports/tests/` |
| `gradlew connectedDebugAndroidTest` | 通过 | REDMI Android 14 / API 34 上 5 项 Compose 设备测试全部通过 |
| `gradlew lint` | 通过 | 0 错误、10 条非阻塞警告；`app/build/reports/lint-results-debug.html` |
| `gradlew assembleDebug` | 通过 | `app/build/outputs/apk/debug/app-debug.apk` |
| `gradlew assembleRelease` | 通过 | `app/build/outputs/apk/release/app-release-unsigned.apk` |
| 安装与启动 | 通过 | Debug APK 已安装到 `emulator-5554`，冷启动 953 ms，并实际进入本地首页、今日计划和训练执行页 |

完整命令、环境、APK 哈希和签名核验记录见 [tests/BUILD_REPORT.md](tests/BUILD_REPORT.md)，需求到测试的详细映射见 [tests/TEST_MATRIX.md](tests/TEST_MATRIX.md)。

## 已知限制

- 同一 monorepo 的配套 FastAPI 服务位于 `services/backend`；本轮尚未执行两端连接真实 MySQL 后端的互操作 E2E。
- 同步下来的每日状态会参与疲劳推荐；当前界面尚未提供每日状态的本地录入入口。
- Release APK 按要求保持未签名，安装前需用私有上传密钥签名。
- 休息计时通知可能受部分厂商的后台省电策略影响；App 前台倒计时和组记录不受影响。

## 隐私与备份

Android 系统云备份和设备迁移均排除 Room 数据库、DataStore/SharedPreferences、应用私有文件和显式 JSON 备份，因此训练及健康记录、设置和令牌不会被系统备份。需要迁移记录时，由用户在应用内显式导出并自行保管；详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。
