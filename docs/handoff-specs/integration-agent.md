# Agent 4：Codex 总控整合、审计和后续维护规范

## 任务

接收前三个 Agent 分别提供的：

1. Android 完整源码和 APK；
2. Windows 完整源码和 EXE；
3. FastAPI＋MySQL 完整源码。

你的任务是完整读取、构建、测试并合并三套源码，使其真正共用同一 API、同一数据契约、同一计划版本和同步逻辑，并整理成未来 Codex 可以持续修改的统一仓库。

这不是只做代码评审。必须实际修改、运行、构建并交付统一源码和产物。

## 输入

期望收到：

```text
android-source/
windows-source/
backend-source/
```

先盘点：

- 技术版本
- 项目结构
- 构建命令
- 实际接口
- 模型字段
- UUID
- 时间
- 令牌
- 本地数据库
- 同步
- 测试
- 构建产物
- 硬编码密钥
- 已知错误

输出 `docs/source-handoff.md`。

## 统一仓库

```text
PersonalFitnessPlannerCloud/
├─ apps/
│  ├─ android/
│  └─ windows/
├─ services/
│  └─ backend/
├─ contracts/
│  ├─ openapi.yaml
│  ├─ schema-version.json
│  ├─ default-training-plan.json
│  ├─ generated/
│  └─ examples/
├─ infra/
│  ├─ docker-compose.yml
│  └─ mysql/
├─ scripts/
│  ├─ bootstrap-dev.ps1
│  ├─ bootstrap-dev.sh
│  ├─ test-all.ps1
│  ├─ build-all.ps1
│  ├─ generate-clients.ps1
│  └─ package-release.ps1
├─ docs/
├─ .github/workflows/
├─ AGENTS.md
├─ README.md
├─ CHANGELOG.md
└─ VERSION
```

## 权威契约

以后端和产品规范建立：

- `contracts/openapi.yaml`
- `contracts/schema-version.json`
- `contracts/default-training-plan.json`
- 统一错误格式
- 统一枚举
- 统一分页
- 统一 UUID
- 统一时间
- 统一软删除
- 统一版本

优先通过 OpenAPI 生成客户端 API DTO/Client。生成代码与手写领域模型分开，禁止直接手改生成文件。

## 整合工作

### 数据库

验证：

- MySQL 从空库执行迁移
- 默认计划可重复 seed
- Android Room Migration
- Windows SQLite Migration
- 历史训练快照
- UUID
- 软删除
- 游标
- 幂等键
- 计划版本

### 同步

统一：

- 云端权威计划、动作、器械
- 客户端 Outbox
- 幂等上传
- 软删除
- 增量游标
- 完整 bootstrap
- 401 刷新一次
- 刷新失败退出
- 网络恢复重试
- 同步错误可见
- Android 和 Windows 不重复创建训练组

### 功能

Android 必须完成查看、训练、替代动作、提示、计时、离线、历史和同步。

Windows 除核心训练外，必须完成计划编辑、发布、分配、版本历史和管理功能。

后端必须完成认证、计划版本、动作、器械、训练、同步、管理、审计和 OpenAPI。

发现缺失时直接修复代码，不能只写 TODO。

## 默认计划 JSON

把本规范的训练计划整理成机器可读：

```json
{
  "plan_code": "beginner_recomp_ab_v1",
  "name": "小白增肌减脂 A/B 全身计划",
  "cycle": ["A", "B"],
  "weekly_strength_target": 3,
  "minimum_rest_days": 1,
  "adaptation_weeks": 2,
  "days": []
}
```

每个位置至少包含：

```text
slot_code
order
muscle_group
primary_exercise_id
options
sets
rep_min
rep_max
rest_seconds
cues
adaptation_sets
enabled
```

必须通过 JSON Schema 或 Pydantic 校验。

## 共享测试向量

建立：

```text
contracts/examples/recommendation-cases.json
contracts/examples/progression-cases.json
```

至少覆盖：

- 第一次推荐 A
- 完成 A 后推荐 B
- 漏练不破坏顺序
- 昨天训练今天恢复
- 每周最多 3 次
- 疲劳 9 休息
- 前两周降组
- 新版本只影响新训练
- 达到次数上限加重量
- 部分达到保持
- 连续失败降重量
- 疼痛不加重量
- 替代动作不继承重量

三端结果必须一致。

## 安全审计

检查并修复：

- APK/EXE 内 MySQL 密码
- 硬编码 JWT 密钥
- `.env` 提交
- 信任所有证书
- 敏感日志
- 明文 Refresh Token
- 未授权管理 API
- IDOR
- SQL 注入
- CORS
- MySQL 公网暴露
- 弱管理员密码
- Release 调试日志
- 导出隐私

输出 `docs/security-review.md`。

## 构建

后端：

```powershell
docker compose -f infra/docker-compose.yml up -d --build
```

Android：

```powershell
cd apps/android
.\gradlew.bat test lint assembleDebug assembleRelease
```

Windows：

```powershell
dotnet restore apps/windows/PersonalFitnessPlanner.sln
dotnet build apps/windows/PersonalFitnessPlanner.sln -c Release
dotnet test apps/windows/PersonalFitnessPlanner.sln -c Release
.\apps\windows\scripts\publish.ps1
```

总构建：

```powershell
.\scripts\build-all.ps1
```

产物：

```text
artifacts/
├─ android/
├─ windows/
├─ backend/
├─ contracts/
└─ checksums/
```

生成 SHA-256。

## 端到端测试

至少实际验证：

1. 启动 MySQL 和后端。
2. 创建管理员和普通用户。
3. seed 默认计划。
4. Android 和 Windows 登录。
5. 两端看到同一计划版本。
6. Android 离线完成 A。
7. 恢复联网上传。
8. Windows 看到记录。
9. Windows 创建并发布新版本。
10. Android 拉取新版本。
11. 旧训练仍显示旧版本。
12. 新训练使用新版本。
13. 重复同步不生成重复组。
14. 重启服务后数据仍在。
15. 客户端恢复未完成训练。
16. Windows EXE 无管理员权限可运行。
17. APK 可安装启动。

无法真机验证时必须明确标注，不得虚报。

## CI

建立：

- 后端 lint/type/pytest/MySQL 集成测试
- OpenAPI 差异检查
- Android test/lint/assembleDebug
- Windows build/test/publish
- seed 校验
- 共享测试向量
- 构建产物上传
- 禁止上传密钥

## AGENTS.md

根目录创建 `AGENTS.md`，说明：

- 仓库结构
- 模块职责
- 不可破坏的 API
- 数据权威
- 计划版本
- 同步
- 构建和测试
- 修改训练计划的位置
- OpenAPI 生成
- 客户端生成
- 数据库迁移
- 密钥规则
- 修改后必须执行的检查

## 修改原则

- 先建立可构建基线
- 小步修改
- 不删除可用功能
- 不盲目替换技术栈
- 三个项目保持独立可构建
- 不直接改生产数据库
- 不覆盖已发布计划
- 不重写历史训练
- 不把 SQL 放 UI
- 不把密钥放源码
- 不用模拟成功替代真实测试

## 最终交付

```text
统一源码仓库
Android 源码和 APK
Windows 源码和 EXE
后端源码和 Docker
MySQL 迁移
OpenAPI
默认计划 JSON
测试报告
端到端报告
安全审计
构建脚本
AGENTS.md
README
CHANGELOG
版本号
SHA-256
```

最终报告：

```text
统一仓库路径
Android APK 路径
Windows EXE 路径
后端启动命令
MySQL 数据卷
OpenAPI 路径
默认计划版本
测试通过项
未通过项
未验证项
已知限制
```


## 统一架构约定

整个系统由三部分组成：

1. Android APK：手机查看今日训练、动作要点、替代动作、记录每组重量和次数，并离线缓存。
2. Windows EXE：电脑端查看训练、记录训练、维护个人动作和计划，并可发布计划到云端。
3. 云端后端：FastAPI REST API＋MySQL，负责用户、动作库、训练计划版本、计划下发、训练记录同步和审计。

客户端不得直连 MySQL，只能通过 HTTPS REST API 访问后端。

统一仓库最终结构：

```text
PersonalFitnessPlannerCloud/
├─ apps/
│  ├─ android/
│  └─ windows/
├─ services/
│  └─ backend/
├─ contracts/
│  ├─ openapi.yaml
│  ├─ schema-version.json
│  ├─ default-training-plan.json
│  └─ examples/
├─ infra/
│  ├─ docker-compose.yml
│  └─ mysql/
├─ scripts/
├─ docs/
├─ AGENTS.md
├─ README.md
└─ CHANGELOG.md
```

### 统一 ID、时间和版本规则

- 所有云端业务对象使用 UUID。
- 客户端本地可以使用自增主键，但必须同时保存服务器 UUID。
- 服务器存储 UTC 时间；API 使用 ISO 8601。
- 训练所属日期另外保存 `local_date`。
- 用户时区保存 IANA 时区名。
- 所有可同步对象至少包含：
  - `id`
  - `version`
  - `created_at`
  - `updated_at`
  - `deleted_at`
- 删除采用软删除。
- 已发布训练计划不可原地修改；修改时创建新版本。
- 历史训练必须保存当时的计划版本和动作快照。

### 同步权威

- 云端是动作库、器械库、训练计划和用户计划分配的权威来源。
- 客户端允许离线缓存。
- 未同步训练记录先保存在客户端 Outbox。
- 计划冲突采用服务器优先。
- 训练记录使用幂等键，避免重复提交。
- 同步必须支持增量游标、断网重试和完整重新同步。

### 最低 API

```text
POST   /api/v1/auth/login
POST   /api/v1/auth/refresh
POST   /api/v1/auth/logout
GET    /api/v1/me
GET    /api/v1/bootstrap
GET    /api/v1/plans/current
GET    /api/v1/plans/{plan_version_id}
GET    /api/v1/exercises
GET    /api/v1/equipment
GET    /api/v1/workout-sessions
POST   /api/v1/workout-sessions
PATCH  /api/v1/workout-sessions/{id}
POST   /api/v1/readiness
GET    /api/v1/sync/changes
POST   /api/v1/sync/batch
```

管理 API：

```text
POST   /api/v1/admin/exercises
PATCH  /api/v1/admin/exercises/{id}
POST   /api/v1/admin/equipment
PATCH  /api/v1/admin/equipment/{id}
POST   /api/v1/admin/plans
POST   /api/v1/admin/plans/{id}/versions
PATCH  /api/v1/admin/plan-versions/{id}
POST   /api/v1/admin/plan-versions/{id}/publish
POST   /api/v1/admin/assignments
GET    /api/v1/admin/audit-logs
GET    /api/v1/admin/sync-status
```



## 默认训练计划

用户情况：

- 28 岁；
- 有约 1 个月健身经历；
- 属于重新开始的小白；
- 目标是增肌减脂；
- 每周 3 次全身力量训练；
- 第一周 A、B、A；
- 第二周 B、A、B；
- 两次力量训练之间默认至少间隔 1 天；
- 前两周所有动作只做 2 个正式组；
- 第三周开始执行完整组数；
- 正式组通常保留 2～3 次余力；
- 每个训练位置只选择首选动作或一个替代动作。

### A 计划：胸部优先

1. 胸部整体
   - 首选：杠铃平板卧推｜平板卧推架＋杠铃＋杠铃片｜3×8～10
   - 替代：史密斯平板卧推｜史密斯机＋平凳｜3×8～12
   - 替代：哑铃平板卧推｜哑铃＋平凳｜3×8～12
   - 替代：坐姿推胸｜坐姿推胸机｜3×8～12
   - 要点：肩胛后缩并适度下沉；胸口打开；手腕中立；肘部与躯干约 30～60°；不要耸肩或让肩膀向前顶。

2. 背部宽度
   - 首选：高位下拉｜高位下拉机｜3×8～12
   - 替代：辅助引体向上｜辅助引体向上机｜3×8～12
   - 替代：对握高位下拉｜高位下拉机＋对握把手｜3×8～12
   - 替代：自重引体向上｜引体向上架｜3 组，保留 1～2 次余力
   - 要点：胸口微抬；先沉肩再屈肘；肘部向下和向髋部移动；不要拉到颈后；不要大幅后仰。

3. 大腿前侧、臀部
   - 首选：坐姿腿举｜坐姿腿举机或 45° 倒蹬机｜3×8～12
   - 替代：哈克深蹲｜哈克深蹲机｜3×8～12
   - 替代：史密斯深蹲｜史密斯机｜3×8～12
   - 替代：高脚杯深蹲｜哑铃或壶铃｜3×10～15
   - 替代：杠铃深蹲｜深蹲架＋杠铃＋杠铃片｜3×6～10
   - 要点：脚掌踩稳；膝盖跟随脚尖；腰臀稳定；不要骨盆卷起；顶端不要猛烈锁膝。

4. 大腿后侧
   - 首选：坐姿腿弯举｜坐姿腿弯举机｜2×10～15
   - 替代：俯卧腿弯举｜俯卧腿弯举机｜2×10～15
   - 替代：站姿单腿弯举｜站姿单腿弯举机｜每侧 2×10～15
   - 替代：哑铃罗马尼亚硬拉｜哑铃｜2×8～12
   - 替代：史密斯罗马尼亚硬拉｜史密斯机｜2×8～12
   - 要点：器械转轴对齐膝关节；臀部稳定；控制回放；硬拉类保持臀部后移、腰背中立。

5. 肩部中束
   - 首选：哑铃侧平举｜哑铃｜2×12～20
   - 替代：器械侧平举｜侧平举机｜2×12～20
   - 替代：单臂绳索侧平举｜龙门架＋单手把｜每侧 2×12～20
   - 要点：肘部带动；手臂位于身体侧前方；不要耸肩、摆动或强行夹死肩胛。

6. 肱三头肌
   - 首选：绳索下压｜龙门架＋绳索把手｜2×10～15
   - 替代：直杆下压｜龙门架＋短直杆｜2×10～15
   - 替代：单臂绳索下压｜龙门架＋单手把｜每侧 2×10～15
   - 替代：绳索过头臂屈伸｜龙门架＋绳索把手｜2×10～15
   - 替代：三头下压｜三头训练机｜2×10～15
   - 要点：手肘固定；肩膀下沉；手腕中立；不要用躯干压重量。

7. 小腿
   - 首选：站姿提踵｜站姿提踵机｜2×10～15
   - 替代：腿举机提踵｜腿举机｜2×12～20
   - 替代：史密斯站姿提踵｜史密斯机＋垫高踏板｜2×10～15
   - 替代：单腿站姿提踵｜踏板，可手持哑铃｜每侧 2×12～20
   - 要点：前脚掌踩稳；脚跟充分下降和抬起；顶端停顿；脚踝不要内外翻。

8. 腹部
   - 首选：绳索卷腹｜龙门架＋绳索把手｜2×10～15
   - 替代：器械卷腹｜卷腹机｜2×10～15
   - 替代：悬垂屈膝举腿｜单杠或举腿架｜2×8～15
   - 替代：反向卷腹｜瑜伽垫或平凳｜2×12～20
   - 替代：平板支撑｜瑜伽垫｜2×30～60 秒
   - 要点：肋骨向骨盆靠近；骨盆稳定；不要只用手臂或髋屈肌；不要塌腰。

### B 计划：背部优先

1. 中上背、背部厚度
   - 首选：胸托划船｜胸托划船机｜3×8～12
   - 替代：坐姿绳索划船｜龙门架低位滑轮＋划船把手｜3×8～12
   - 替代：坐姿器械划船｜坐姿划船机｜3×8～12
   - 替代：胸托哑铃划船｜哑铃＋上斜训练凳｜3×8～12
   - 替代：单臂哑铃划船｜哑铃＋训练凳｜每侧 3×8～12
   - 替代：杠铃划船｜杠铃＋杠铃片｜3×6～10
   - 要点：脊柱中立；肩胛自然前伸和后缩；肘部向后；不要耸肩或反复摆动躯干。

2. 上胸
   - 首选：上斜哑铃卧推｜哑铃＋可调训练凳｜3×8～12
   - 替代：史密斯上斜卧推｜史密斯机＋可调训练凳｜3×8～12
   - 替代：上斜杠铃卧推｜上斜卧推架＋杠铃＋杠铃片｜3×6～10
   - 替代：上斜器械推胸｜上斜推胸机｜3×8～12
   - 替代：低位绳索夹胸｜双滑轮龙门架＋单手把｜3×10～15
   - 要点：凳角约 15～30°；肩胛后缩下沉；胸口打开；手腕中立；肩膀不要前顶。

3. 臀部、大腿后侧
   - 首选：杠铃罗马尼亚硬拉｜杠铃＋杠铃片｜3×8～10
   - 替代：史密斯罗马尼亚硬拉｜史密斯机｜3×8～12
   - 替代：哑铃罗马尼亚硬拉｜哑铃｜3×8～12
   - 替代：杠铃臀推｜杠铃＋平凳＋臀推垫｜3×8～12
   - 替代：史密斯臀推｜史密斯机＋平凳＋臀推垫｜3×8～12
   - 替代：器械臀推｜臀推机或 Glute Drive｜3×8～12
   - 替代：坐姿腿弯举｜坐姿腿弯举机｜3×10～15
   - 要点：核心收紧；膝盖微屈；臀部向后；重量贴近腿；腰背中立；不要过度后仰。

4. 大腿前侧
   - 首选：腿屈伸｜腿屈伸机｜2×10～15
   - 替代：坐姿腿举｜坐姿腿举机｜2×8～12
   - 替代：哈克深蹲｜哈克深蹲机｜2×8～12
   - 替代：史密斯深蹲｜史密斯机｜2×8～12
   - 替代：保加利亚分腿蹲｜哑铃＋训练凳｜每侧 2×8～12
   - 要点：膝关节对齐转轴；腰臀稳定；控制回放；不要猛烈锁膝；膝盖跟随脚尖。

5. 后肩
   - 首选：反向蝴蝶机飞鸟｜反向蝴蝶机｜2×12～20
   - 替代：绳索面拉｜龙门架＋绳索把手｜2×12～20
   - 替代：绳索反向飞鸟｜双滑轮龙门架＋单手把｜2×12～20
   - 替代：俯身哑铃反向飞鸟｜哑铃｜2×12～20
   - 替代：胸托反向飞鸟｜哑铃＋上斜训练凳｜2×12～20
   - 要点：躯干稳定；手肘微屈；后肩带动；不要耸肩或用腰摆动。

6. 肱二头肌
   - 首选：哑铃弯举｜哑铃｜2×10～15
   - 替代：锤式弯举｜哑铃｜2×10～15
   - 替代：EZ 杠弯举｜EZ 弯杆＋杠铃片｜2×8～12
   - 替代：绳索弯举｜龙门架＋短直杆｜2×10～15
   - 替代：牧师凳弯举｜牧师凳＋EZ 杠或哑铃｜2×10～15
   - 替代：器械二头弯举｜二头弯举机｜2×10～15
   - 要点：肩膀下沉；手肘固定；手腕中立；不要后仰、耸肩或摆动。

7. 小腿
   - 首选：坐姿提踵｜坐姿提踵机｜2×12～20
   - 替代：哑铃坐姿提踵｜哑铃＋平凳＋垫高踏板｜2×12～20
   - 替代：腿举机提踵｜腿举机｜2×12～20
   - 替代：单腿站姿提踵｜踏板，可手持哑铃｜每侧 2×12～20
   - 要点：脚掌稳定；脚跟充分下降和抬高；脚踝正直；不要快速弹动。

8. 腹部
   - 首选：器械卷腹｜卷腹机｜2×10～15
   - 替代：绳索卷腹｜龙门架＋绳索把手｜2×10～15
   - 替代：悬垂屈膝举腿｜单杠或举腿架｜2×8～15
   - 替代：反向卷腹｜瑜伽垫或平凳｜2×12～20
   - 替代：死虫式｜瑜伽垫｜每侧 2×8～12
   - 替代：平板支撑｜瑜伽垫｜2×30～60 秒
   - 要点：腹部主动收缩；肋骨下沉；骨盆稳定；不要甩腿或让腰部过度反弓。
