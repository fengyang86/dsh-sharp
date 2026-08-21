# DSH-Sharp

> DeepSeek Harness（DSH）的桌面客户端壳，基于 **.NET 10 + Avalonia** 构建。
> 目标不是重写 WebUI，而是为 DSH WebUI 提供桌面级体验：内嵌浏览器壳 + 自启动 + 系统托盘 + 会话完成通知等。

## 技术栈

| 组件 | 版本 |
| --- | --- |
| .NET | 10.0 |
| Avalonia | 12.1（Fluent 主题，`WindowDecorations` 自绘标题栏） |
| 内嵌 WebView | [Avalonia.Controls.WebView](https://www.nuget.org/packages/Avalonia.Controls.WebView) 12.1（WebView2 / WebKit / WebKitGTK） |
| MVVM | CommunityToolkit.Mvvm |
| 单元测试 | xUnit |

## 功能（桌面壳）

- **内嵌 DSH WebUI**：`NativeWebView` 承载，默认地址 `http://127.0.0.1:3080`（可配置）
- **服务托管**：服务离线时自动拉起本地 DSH 服务——
  - `Npx` 模式（默认）：托管 `npx --yes @deepseek-ai/dsh@latest web --no-open --port <配置端口>`
  - `Source` 模式：在配置的源码路径下托管 `pnpm dsh web`（源码部署）
  - `None` 模式：纯探测不托管；**探测优先**——服务在线即用（无论谁启动的），
    只有客户端自己拉起的进程才在退出时终止（所有权语义）
- **引导页**：未检测到服务时显示引导界面（一键启动 / 重试 / 错误提示）
- **自启动**：注册 HKCU Run 键（`--autostart` 参数启动时静默驻留托盘）
- **系统托盘**：最小化到托盘、托盘菜单（显示主窗口 / 退出）、关闭窗口默认驻留托盘
- **单实例**：命名 Mutex 互斥，第二实例自动唤起已有窗口后退出
- **会话完成通知**：订阅 DSH `/api/events.host` WebSocket 流，检测会话 running→idle 翻转后弹通知横幅并自动唤起窗口；标题来自 `/api/events.mux` 的 `session/title` 事件
- **深色模式跟随**：主题跟随系统（可配置 Light/Dark/System）
- **自定义标题栏**：Avalonia 12 `WindowDecorationProperties` 拖动 + 自绘窗口按钮

## 解决方案结构

```
DSHSharp.slnx
├── src/
│   ├── DSHSharp/              # Avalonia 桌面壳（窗口、托盘、WebView、通知）
│   └── DSHSharp.Core/         # 核心服务层（不依赖 UI）
│       ├── Configuration/     # AppSettings（JSON 持久化）
│       ├── Services/          # 自启动、单实例、设置服务
│       └── Dsh/               # DSH 事件流解析与监控（host/mux WebSocket）
└── tests/
    └── DSHSharp.Core.Tests/   # 单元测试
```

## 构建与运行

要求：.NET SDK 10.0 或更高版本；Windows 需要 WebView2 Runtime（Win10/11 一般自带）。

```bash
# 还原并构建
dotnet build DSHSharp.slnx

# 运行桌面壳
dotnet run --project src/DSHSharp

# 自启动模式（静默驻留托盘）
DSHSharp.exe --autostart

# 运行测试
dotnet test DSHSharp.slnx
```

## DSH 事件协议参考

本客户端直接消费 DSH 的 WebSocket 事件流（无需走 WebUI）：

- `ws://<host>/api/events.host`：全局会话状态，帧 `host/session-status { sessionId, running }`
- `ws://<host>/api/events.mux`：会话事件，帧 `session/event`（含 `session/title`、`turn/end`）

帧格式为 `{ type: 'server-request', rpcId, method, payload }`，连接后服务器直接推送。

## 路线图

- [x] 内嵌 WebUI 壳 + 自启动 + 托盘 + 单实例 + 会话完成通知 + 深色模式跟随 + 自定义标题栏
- [ ] 设置页（服务地址、主题、通知开关）
- [ ] 会话列表/快捷入口（走 `/api/session.list` RPC）
- [ ] 打包与发布（Windows / Linux / macOS）

## 许可

[MIT](LICENSE)

> 应用图标（`src/DSHSharp/Assets/avalonia-logo.ico`）使用 DSH 官方 favicon 图形，
> 来源：[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) `apps/web/public/favicon.svg`，
> 生成脚本见 `tools/gen-icons/`。
