# 贡献指南

感谢参与 DSH-Sharp。提交改动前请确认：

1. 使用 .NET 10 SDK，并保持现有 C# 格式、可空引用和中文注释约定。
2. 核心逻辑放在 `src/DSHSharp.Core`，Avalonia 界面只负责展示和组装。
3. DSH 交互优先使用官方 RPC、事件流和插件接口，不修改 WebView 私有实现。
4. 版本兼容改动必须同步更新 `docs/versioning.md`、测试和发布说明。
5. 提交前运行 `dotnet test DSHSharp.slnx`，并检查 `git diff --check`。

提交信息建议使用 `feat:`、`fix:`、`docs:`、`test:` 等约定式前缀。
