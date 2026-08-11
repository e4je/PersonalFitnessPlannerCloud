# Agent 3：FastAPI＋MySQL 云端后端开发规范

## 任务

实际开发训练计划云端后端，为 Android APK 和 Windows EXE 提供认证、计划下发、动作和器械更新、训练记录同步、计划管理和审计。

禁止只给接口清单、SQL 片段、教程或伪代码。必须交付完整源码、迁移、Docker、测试、OpenAPI 和部署说明。

## 技术栈

- Python 3.12
- FastAPI
- Pydantic v2
- SQLAlchemy 2
- Alembic
- MySQL 8
- Uvicorn/Gunicorn
- Pytest
- HTTPX
- JWT Access Token＋Refresh Token
- Argon2 或 bcrypt
- Dockerfile
- Docker Compose
- 环境变量和结构化日志

## 必须交付

```text
backend/
├─ app/
│  ├─ main.py
│  ├─ api/
│  ├─ core/
│  ├─ db/
│  ├─ models/
│  ├─ schemas/
│  ├─ services/
│  ├─ repositories/
│  ├─ sync/
│  └─ seed/
├─ alembic/
├─ tests/
├─ contracts/openapi.yaml
├─ scripts/
│  ├─ seed_default_plan.py
│  ├─ create_admin.py
│  ├─ export_openapi.py
│  └─ smoke_test.py
├─ Dockerfile
├─ docker-compose.yml
├─ pyproject.toml
├─ lock 文件
├─ .env.example
├─ README.md
└─ CHANGELOG.md
```

必须提供完整源码，不能只提供 Docker 镜像。

## 数据模型

至少包含：

```text
users
roles
user_roles
refresh_tokens
muscle_groups
equipment
exercises
exercise_cues
exercise_alternatives
training_plans
plan_versions
plan_days
plan_slots
plan_slot_options
plan_assignments
workout_sessions
workout_sets
daily_readiness
cardio_sessions
sync_changes
idempotency_keys
audit_logs
schema_versions
```

要求：

- UUID
- UTC
- 软删除
- 乐观锁版本
- 外键和索引
- 已发布计划不可修改
- 历史训练保存动作和计划快照
- 密码只存哈希
- Refresh Token 可撤销，不明文存储

## 计划版本流程

1. 创建逻辑计划。
2. 创建草稿版本。
3. 添加训练日和位置。
4. 添加首选及替代动作。
5. 校验。
6. 发布为不可变版本。
7. 分配给用户。
8. 客户端下一次新训练使用新版本。
9. 旧训练继续显示旧版本。

## API

### 认证

```text
POST /api/v1/auth/login
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
GET  /api/v1/me
```

### 初始化

```text
GET /api/v1/bootstrap
```

返回用户、权限、当前计划、计划版本、动作库、器械库、最近训练、同步游标、服务器时间、API 版本和 Schema 版本。

### 训练和同步

```text
GET    /api/v1/workout-sessions
GET    /api/v1/workout-sessions/<built-in function id>
POST   /api/v1/workout-sessions
PATCH  /api/v1/workout-sessions/<built-in function id>
DELETE /api/v1/workout-sessions/<built-in function id>
POST   /api/v1/readiness
GET    /api/v1/readiness
POST   /api/v1/cardio-sessions
GET    /api/v1/cardio-sessions
GET    /api/v1/sync/changes?cursor=...
POST   /api/v1/sync/batch
```

训练记录要求接受客户端 UUID、来源 android/windows、计划版本、客户端版本和幂等键，防止重复组。

### 管理

实现统一约定中的管理 API。发布前校验动作、器械、组次、顺序、替代动作、A/B 规则和 JSON 可解析性。

## 同步冲突

- 计划、动作、器械服务器优先
- 客户端不可覆盖已发布计划
- 重复幂等键返回第一次结果
- 版本冲突返回 409 和服务器副本
- 游标过旧返回 `full_resync_required`
- 冲突写入审计日志

## 推荐逻辑数据

后端至少返回：

- 训练日
- 当前 A/B 状态
- 每周最大次数
- 最小休息天数
- 疲劳阈值
- 当前训练周
- 前两周降组规则
- 最近训练和 readiness

可选实现 `/api/v1/recommendation/today`，但必须独立测试。

## 安全

- 生产 HTTPS
- CORS 白名单
- 登录限速
- JWT 短时效
- Refresh Token 轮换
- RBAC
- 参数化查询
- 请求体大小限制
- 日志不输出敏感信息
- 健康检查不泄露配置
- MySQL 默认不暴露公网

## Docker Compose

至少包含 backend 和 mysql，并提供健康检查、数据卷、迁移、seed、管理员创建、备份和恢复命令。

## 测试

至少覆盖：

- 认证
- RBAC
- Bootstrap
- 草稿和发布
- 发布后不可修改
- 新版本和分配
- 默认计划 seed
- 训练幂等
- 重复组
- 软删除
- 增量同步
- 游标失效
- 冲突
- 管理接口
- Alembic
- OpenAPI
- MySQL 容器集成测试

不能只使用 SQLite 模拟全部数据库测试。

## 验收

执行：

```bash
pytest
alembic upgrade head
python scripts/seed_default_plan.py
python scripts/export_openapi.py
docker compose up -d --build
python scripts/smoke_test.py
```

最终报告源码目录、启动命令、API、OpenAPI、迁移版本、管理员创建方式、seed 结果、测试、备份恢复和已知限制。


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
