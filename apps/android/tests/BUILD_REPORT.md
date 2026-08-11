# 最终构建与验收报告

> 历史归档：本报告记录整合前 `01_Android_APK/android` 的原始交付，不是当前统一副本的构建结果。统一副本没有保留这里引用的 `app/build` 报告或 APK，当前状态以根 `docs/test-report.md` 为准。

验收日期：2026-08-09（Asia/Shanghai）

源码基线：整合前 Android 原始交付源码。

## 环境

- Microsoft OpenJDK 21.0.8
- Gradle Wrapper 8.10.2（分发包 SHA-256 已固定）
- Android Gradle Plugin 8.8.2、Kotlin 2.0.21
- Android SDK Platform 35、Build Tools 35.0.0、Platform Tools 37.0.1
- REDMI Android 14 / API 34，ADB 序列号 `emulator-5554`

系统起初没有可用的 Java/Android SDK/Gradle 命令行环境；完整检查和修复记录见 [`../docs/BUILD_ENVIRONMENT.md`](../docs/BUILD_ENVIRONMENT.md)。

## 最终门禁

| 命令/检查 | 最终结果 |
|---|---|
| `gradlew test` | BUILD SUCCESSFUL；Debug 43 项、Release 43 项，均为 0 失败、0 错误、0 跳过 |
| `gradlew lint --max-workers=1` | BUILD SUCCESSFUL；0 错误、10 条非阻塞警告 |
| `gradlew assembleDebug` | BUILD SUCCESSFUL |
| `gradlew assembleRelease` | BUILD SUCCESSFUL |
| `gradlew connectedDebugAndroidTest` | BUILD SUCCESSFUL；5 项 Compose 设备测试全部通过 |
| `adb install -r -t` | Debug APK 安装成功 |
| 清数据后冷启动 | `MainActivity` 启动成功，`TotalTime=953 ms` |
| 本地模式烟测 | 首页显示真实内置计划 v1、周进度 0/3、首次推荐 A；今日页显示 8 个动作；执行页显示杠铃平板卧推及前两周 2 组规则 |
| `AndroidRuntime` 日志扫描 | 未发现崩溃 |

报告位置：

- `app/build/reports/tests/testDebugUnitTest/index.html`
- `app/build/reports/tests/testReleaseUnitTest/index.html`
- `app/build/reports/androidTests/connected/debug/index.html`
- `app/build/reports/lint-results-debug.html`

一次把测试、Lint 和组装并行塞入同一 Gradle 进程时触发了 AGP Lint/Kapt 的任务竞态；按要求分别执行上述命令后全部通过，最终报告和 APK 均来自这些成功任务。

## APK 交付物

| 文件 | 大小（字节） | SHA-256 | 包信息 | 签名状态 |
|---|---:|---|---|---|
| `artifacts/PersonalFitnessPlanner-debug.apk` | 20,162,139 | `4719CB37673016A377B23ABB91999398692A0F33E377640C6BCE52F6E203103B` | `com.personalfitnessplanner.debug`，v1 / `1.0.0-debug` | apksigner 验证通过；APK Signature Scheme v2；Android Debug 证书 |
| `artifacts/PersonalFitnessPlanner-release-unsigned.apk` | 13,360,638 | `CFF2CE24BFE74483B9D4B2797211ABC6B592A212BC8A0E4120769622A3361E14` | `com.personalfitnessplanner`，v1 / `1.0.0` | apksigner 返回 `DOES NOT VERIFY`，符合未签名交付要求 |

两个 APK 均为 `minSdkVersion 26`、`targetSdkVersion 35`、`compileSdkVersion 35`，应用名为“私人健身规划”。Release APK 必须先按 [`../docs/SIGNING.md`](../docs/SIGNING.md) 使用项目方私钥签名，才能对外安装分发。
