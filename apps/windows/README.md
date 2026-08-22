# Personal Fitness Planner（Windows）

面向 Windows 10/11 x64 的个人健身计划桌面客户端。应用采用 .NET 10、WPF、MVVM 与 SQLite，支持 A/B 训练建议、按动作/器械隔离的重量建议、离线记录、历史快照、计划版本、增量同步和保留本地草稿的完整重新同步；后续发布脚本会生成无需预装 .NET Runtime、无需管理员权限的自包含 EXE，本轮源码整合没有生成新 EXE。

## 环境要求

- Windows 10/11 x64。
- Windows PowerShell 5.1 或 PowerShell 7。
- 源码构建需要 .NET 10 SDK 或满足 `global.json` 前滚规则的更高版本 SDK。
- NuGet 依赖还原只访问项目 `NuGet.Config` 中声明的源；包固定缓存到仓库内 `.packages/`。
- 构建后运行 `artifacts/PersonalFitnessPlanner.exe` 不需要安装 .NET；当前统一副本尚无该文件。

检查环境：

```powershell
dotnet --info
dotnet --list-sdks
```

## 构建、测试和发布

在本目录打开 PowerShell：

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
```

`publish.ps1` 会运行测试，分别发布 `win-x64` 自包含单文件和多文件 fallback，并用带中文和空格的临时数据目录直接执行：

```powershell
.\artifacts\PersonalFitnessPlanner.exe --smoke-test --data-dir "<临时目录>\健身规划 烟雾测试"
```

发布脚本将生成：

- `artifacts/PersonalFitnessPlanner.exe`：可双击运行的主 EXE。
- `artifacts/publish-win-x64/`：自包含多文件 fallback，便于绕过/诊断单文件解压或原生 SQLite 问题。

## 运行和数据

默认本地目录为 `%LOCALAPPDATA%\PersonalFitnessPlanner\`，包含 `fitness.db`、`settings.json`、受 DPAPI 保护的 `auth.dat`、`logs/`、`cache/` 和 `backups/`。也可从设置页修改，或通过命令行指定：

```powershell
.\artifacts\PersonalFitnessPlanner.exe --data-dir "D:\我的数据\健身 规划"
```

应用自动执行 SQLite migration。训练写入本地后进入 Outbox；同步使用客户端幂等键和增量游标，首次同步通过 bootstrap 建立缓存。设置页可分别“上传本地”（只发送 Outbox）或“云端覆盖”（只下载服务器权威缓存）；覆盖前会检查待上传队列并在有未同步记录时拒绝。客户端不直连 MySQL。

API 地址在“设置 → API 地址”中配置，例如 `https://fitness.example.com/`。生产环境仅应使用 HTTPS。管理能力由登录令牌的角色声明决定，不能通过本地界面开关提升权限。

## 文档

- [架构说明](docs/architecture.md)
- [API 与配置](docs/api-configuration.md)
- [数据备份与恢复](docs/data-backup.md)
- [快捷键](docs/keyboard-shortcuts.md)
- [已知限制](docs/known-limitations.md)

## 解决方案结构

```text
src/PersonalFitnessPlanner.App             WPF UI、MVVM 与应用编排
src/PersonalFitnessPlanner.Core            训练推荐、疲劳与重量纯规则
src/PersonalFitnessPlanner.Contracts       REST DTO 与共享契约
src/PersonalFitnessPlanner.Infrastructure  SQLite、同步、导入导出与备份
tests/PersonalFitnessPlanner.Tests          xUnit 自动化测试
```

## 验收范围

自动化测试覆盖 A/B 推荐、疲劳恢复、共享规则向量、计划不可变版本、完整同步、跨账号隔离、SQLite v8 migration、Outbox 幂等、管理员角色声明、历史快照、API 错误脱敏、日志保留、CSV/JSON 导出和中文路径。统一副本当前源码实跑 89/89 与 WPF 0 warning/0 error；Release publish/EXE smoke 留待后续统一构建。
