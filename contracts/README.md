# 共享契约

本目录是三端共享数据的唯一权威来源。

- `openapi.yaml`：由 FastAPI 应用导出；客户端 wire DTO 与请求路径必须兼容它。
- `schema-version.json`：API、数据契约与最低客户端版本。
- `default-training-plan.json`：默认 A/B 计划；使用 JSON Schema 校验后再同步到三端随包快照。
- `examples/`：推荐和双重渐进的跨端测试向量。
- `generated/`：OpenAPI 生成代码的落点。生成文件与手写领域模型分开，禁止手改。

运行 `scripts/sync-contracts.ps1` 会先验证权威文件，再更新 Android、Windows 和后端的随包计划快照。CI 会比较 SHA-256，任一快照漂移都会失败。
