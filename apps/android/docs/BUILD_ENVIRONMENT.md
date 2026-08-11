# Build environment audit

> Historical environment record for the pre-integration Android handoff. Paths and successful build claims below are not evidence for the current unified checkout; use `PersonalFitnessPlannerCloud/apps/android` and the root build guide for the next run.

Audit date: 2026-08-09 (Asia/Shanghai)

Initial state:

- Android Studio 2026.1.3 was installed, but its SDK path was empty.
- `JAVA_HOME`, `ANDROID_HOME`, `ANDROID_SDK_ROOT`, and `GRADLE_HOME` were unset.
- `java`, `gradle`, `adb`, and `sdkmanager` were not available through `PATH`.
- No Android SDK existed at the usual user path.

Remediation and verification:

- Reused Microsoft OpenJDK 21.0.8 at `C:\Program Files\Android\openjdk\jdk-21.0.8`.
- Installed official Android command-line tools 15859902 after checking SHA-256 `90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a`.
- Accepted SDK licenses and installed Platform 35, Build Tools 35.0.0, and Platform Tools 37.0.1.
- Installed Gradle 8.10.2 after checking SHA-256 `31c55713e40233a8303827ceb42ca48a47267a0ad4bab9177123121e71524c26` and generated a checked-in Wrapper with the same checksum pin.
- `gradlew test`, `gradlew lint`, `gradlew assembleDebug`, `gradlew assembleRelease`, and `gradlew connectedDebugAndroidTest` completed successfully in the final acceptance baseline.
- A REDMI Android 14 / API 34 device was visible to ADB as `emulator-5554`; five device tests, Debug APK installation, and a 953 ms cold start all passed.

## Resolved paths

- Canonical Android SDK: `%LOCALAPPDATA%\Android\Sdk`. `local.properties` points here; this is distinct from the project tree.
- The workspace-local `.android-sdk` directory was used only while bootstrapping the environment. It is ignored and is not the canonical SDK used by the final build.
- Workspace Gradle user home/cache: `<workspace>\.gradle-user-home` (ignored).
- ASCII Gradle-cache junction: `%USERPROFILE%\.gradle-pfp` → `<workspace>\.gradle-user-home`.
- ASCII project junction: `%USERPROFILE%\pfp-workspace` → `<workspace>`.

The ASCII junctions avoid Windows/JVM tooling failures caused by non-ASCII project and cache paths. They contain no second source checkout and may be recreated locally. A representative acceptance shell was:

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'
$env:ANDROID_SDK_ROOT = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$env:GRADLE_USER_HOME = Join-Path $env:USERPROFILE '.gradle-pfp'
Set-Location '<workspace>\PersonalFitnessPlannerCloud\apps\android'
.\gradlew.bat test
```

The standard SDK, Gradle distribution/cache, and generated `local.properties` are machine-local prerequisites and are excluded from source control. The checked-in Wrapper pins Gradle 8.10.2 and its SHA-256; the source tree does not depend on a globally installed `gradle` executable.
