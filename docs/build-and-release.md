# 统一构建与发布

> 本页同时说明本地统一打包和 GitHub tag 发布。仓库内的 `artifacts/` 仍是本地构建目录；GitHub Release 资产由独立 workflow 生成，不提交回源码。

## 前置条件

- Docker Engine + Compose plugin
- Python 3.12
- JDK 17 或 21、Android SDK Platform/Build Tools 36（本轮确认 JBR 25 与当前 Gradle/Kotlin 组合不兼容）
- .NET 10 SDK、Windows 10/11 x64（Windows publish 必须在 Windows）

先运行 `scripts/bootstrap-dev.ps1 -NoStart` 准备根 `.env` 和后端开发环境。Android 需自行创建未提交的 `apps/android/local.properties`。

## 顺序

```powershell
.\scripts\sync-contracts.ps1
.\scripts\test-all.ps1
.\scripts\build-all.ps1
```

Android test/lint/assemble 应按独立 Gradle 调用顺序执行；不要把 Lint 与 Kapt/assemble 并行塞进同一 Gradle 进程。
在 Windows 上若工作区或 Gradle distribution/cache 路径含中文而导致 test worker 启动失败，先创建仅用于构建的 ASCII 目录联接，并把 `GRADLE_USER_HOME` 指向 ASCII 路径；不要复制出第二份可编辑源码。

Windows 发布会产生 self-contained single-file EXE 和 multi-file fallback，并运行非管理员/中文空格路径 smoke（除非显式跳过）。Release EXE 当前没有组织代码签名，外部分发前必须签名并扫描。

Android Release APK 是 unsigned；必须用项目方私有上传密钥在受控 CI/签名环境签名。密钥不得进入仓库或构建日志。

## GitHub tag 发布

推送 `0.0.2`、`v0.0.2` 这类语义版本 tag 会触发 `.github/workflows/release.yml`。workflow 在 tag 指向的准确提交上重新执行 Android 与 Windows 测试，随后发布：

```text
PersonalFitnessPlanner-<tag>-android-debug.apk
PersonalFitnessPlanner-<tag>-windows-x64.exe
SHA256SUMS.txt
```

已有且位于默认分支历史中的 tag，可从 GitHub Actions 的 `release` workflow 使用 `Run workflow`，输入 tag 后补发或覆盖同名资产。Release job 只在两个平台构建都成功后获得 `contents: write` 权限。

自动发布的是可安装 Debug APK，application ID 为 `com.personalfitnessplanner.debug`。GitHub Runner 生成的调试签名不保证跨构建稳定；后续 APK 若无法覆盖安装，需要先同步/备份个人数据，再卸载旧 Debug APK。需要稳定原地升级时，应配置受保护的固定签名密钥并改为签名 Release APK。

## 产物

`scripts/package-release.ps1` 汇总：

```text
artifacts/
├─ android/
├─ windows/
├─ backend/
├─ contracts/
└─ checksums/SHA256SUMS.txt
```

`backend/` 包含可用 `docker load` 导入的版本化镜像 tar、Dockerfile 与 Compose 文件；仅在显式跳过后端时允许缺少镜像。

打包脚本只清理统一仓库根 `artifacts/` 下的已知子目录；不会删除模块源码。发布前核对版本、签名状态、OpenAPI diff、数据库迁移、E2E 报告和 SHA-256。
