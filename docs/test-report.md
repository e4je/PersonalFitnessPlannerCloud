# 测试与校验报告

日期：2026-08-23。本文只把在统一源码上实际执行过的检查标记为通过。

## 本轮实际执行

| 门禁 | 结果 | 说明 |
|---|---|---|
| 默认计划 JSON Schema | 通过 | PowerShell `Test-Json -SchemaFile` |
| 跨契约不变量 | 通过 | 26 个必需 method+path 操作；A/B 16 slots、79 options、66 exercises、52 equipment |
| 三端计划与共享向量快照 | 通过 | 计划逐字一致；8 recommendation cases、5 progression cases |
| OpenAPI 快照 | 通过 | backend 运行时导出与根 `contracts/openapi.yaml` 一致 |
| Secret scan | 通过 | 未发现提交的密钥、令牌、私钥或 `.env` |
| 根 PowerShell / POSIX 脚本语法 | 通过 | PowerShell parser 与 `bash -n` |
| Backend pytest | **97 passed / 1 deselected** | deselected 为未在本地运行的真实 MySQL 8 标记测试；含首次向导、固定库名、凭据不回显、并发锁、配置持久化与 MySQL downgrade 顺序回归 |
| Python 生产依赖审计 | 通过 | 对带 hash 的 `requirements.lock` 执行 `pip-audit --strict --require-hashes --disable-pip`，未发现已知漏洞 |
| Windows xUnit | **91/91** | Release 配置；包括 API 错误脱敏、日志保留、origin、导入/导出与 SQLite 运行时门禁 |
| Windows NuGet 审计 | 通过 | 直接与传递依赖未发现已知漏洞；high/critical 告警已设为还原错误 |
| Android 编译/测试 | 通过 | JDK 21 + SDK 36 + ASCII junction；Debug/Release JVM tests 各 79 项，0 失败/0 跳过；lint 0 error、17 warning |
| 源码树清洁度 | 通过 | 生成目录与本机配置均受 `.gitignore` 约束，`git status` 未出现 APK/EXE、密钥或构建输出 |

Backend 测试使用原始交付的隔离 Python 虚拟环境作为依赖解释器，但工作目录和被测代码均为统一副本 `services/backend`。

## 原始交付历史证据

- Android：Debug/Release JVM tests 各 43；设备 tests 5；lint 0 error；Debug/Release assemble 通过；历史真机安装启动通过。
- Windows：原始交付 xUnit 43/43；历史 publish/smoke 只有模块报告，没有独立持久化 smoke 记录。
- Backend：整合前原工程快速 pytest 为 33 passed / 1 MySQL skipped。

这些数字只说明迁入基线，不代表统一修改后的最终发布验收。

## 后续统一构建必须执行

1. 配置 disposable MySQL 8，运行 Alembic upgrade、canonical seed 重入及 `pytest -m mysql`。
2. Android `test`、`lint`、`assembleDebug`、`assembleRelease` 与 Room migration；避免含中文的 Gradle 分发/临时路径。
3. Windows Release publish、中文空格路径非管理员 EXE smoke 与最终代码签名；89 项测试已在源码整合阶段通过。
4. Docker Compose 构建、启动、重启和命名数据卷持久化。
5. `docs/e2e-report.md` 的 17 项真实跨端验收。
6. 对最终 APK/EXE/镜像生成 SHA-256，并完成签名、SBOM 和二进制安全扫描。

上述项目按用户安排留到后续统一构建阶段，当前不得标记为通过。
