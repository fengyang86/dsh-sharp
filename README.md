# DSH-Sharp

> DeepSeek Harness（DSH）的桌面客户端，基于 **.NET 10 + Avalonia** 构建。

## 技术栈

| 组件 | 版本 |
| --- | --- |
| .NET | 10.0 |
| Avalonia | 12.x（Fluent 主题） |
| MVVM | CommunityToolkit.Mvvm |
| 单元测试 | xUnit |

## 解决方案结构

```
DSHSharp.slnx
├── src/
│   ├── DSHSharp/              # Avalonia 桌面应用（UI、视图模型、视图）
│   └── DSHSharp.Core/         # 核心逻辑库（DSH 服务通信、领域模型，不依赖 UI）
└── tests/
    └── DSHSharp.Core.Tests/   # 核心逻辑单元测试
```

- **DSHSharp**：Avalonia 桌面客户端，采用 MVVM 分层，启用了编译期绑定（Compiled Bindings）。
- **DSHSharp.Core**：与 UI 解耦的业务核心，后续承载 DSH 服务端通信（HTTP/WebSocket）、会话管理等逻辑。
- **DSHSharp.Core.Tests**：核心逻辑的单元测试项目。

## 构建与运行

要求：.NET SDK 10.0 或更高版本。

```bash
# 还原并构建
dotnet build DSHSharp.slnx

# 运行桌面应用
dotnet run --project src/DSHSharp

# 运行测试
dotnet test DSHSharp.slnx
```

## 路线图

- [ ] DSH 服务连接与状态监控
- [ ] 会话 / 任务管理界面
- [ ] 日志与运行状态展示
- [ ] 设置页（服务地址、主题等）
- [ ] 打包与发布（Windows / Linux / macOS）

## 许可

[MIT](LICENSE)
