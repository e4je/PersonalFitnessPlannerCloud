# 端到端验收状态

本轮是源码/契约整合阶段，未启动统一 Docker、未安装新 APK、未运行新 EXE。以下全部保留为后续真实 E2E 清单，状态不得从 mock/unit test 推断。

| # | 场景 | 当前状态 |
|---:|---|---|
| 1 | 启动 MySQL 和后端 | 未验证 |
| 2 | 创建管理员和普通用户 | 未验证；入口已提供 `scripts.create_admin` / `scripts.create_user`，普通用户会自动获得 canonical assignment |
| 3 | 幂等 seed 默认计划 | 单元测试已覆盖空库重入及旧 UUID4 seed 升级；真实 MySQL E2E 未验证 |
| 4 | Android 和 Windows 登录同一后端 | 未验证 |
| 5 | 两端看到同一 plan_version_id/version | 未验证 |
| 6 | Android 离线完成 A | 未验证（统一版本） |
| 7 | 恢复联网上传 | 未验证 |
| 8 | Windows 看到该训练 | 未验证 |
| 9 | Windows 创建并发布新版本 | 未验证 |
| 10 | Android 拉取新版本 | 未验证 |
| 11 | 旧训练仍显示旧版本/快照 | 未验证 |
| 12 | 新训练使用新版本 | 未验证 |
| 13 | 重复同步不生成重复 session/set | 未验证 |
| 14 | 服务重启后 MySQL 数据仍在 | 未验证 |
| 15 | 客户端恢复未完成训练 | 未验证 |
| 16 | Windows EXE 无管理员权限运行 | 未验证（原始 EXE 历史 smoke 不代表新构建） |
| 17 | APK 安装启动 | 未验证（原始 APK 历史真机通过不代表新构建） |

## 环境注意

- Android 生产网络策略强制受信任 HTTPS；本地 Compose 暴露的是内部 HTTP API。真实 Android E2E 需接入可信 TLS 终止域名或配置受控的本地开发 CA，不能用 trust-all 绕过。
- Windows 可对 loopback 使用 HTTP，但跨设备/生产必须 HTTPS。
- E2E 使用 disposable 测试库和测试用户；不得连接生产数据库。
