# Generated API clients

本目录只存放由 `contracts/openapi.yaml` 生成的客户端代码或生成报告。不要直接编辑生成文件。

当前 Android 与 Windows 仍保留已有的手写领域适配层；`scripts/generate-clients.ps1` 用于生成独立 wire client，随后由适配层映射到本地 Room/SQLite 模型。
