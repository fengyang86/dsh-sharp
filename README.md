# DSH-Sharp

> DeepSeek Harness（DSH）的桌面客户端壳，基于 **.NET 10 + Avalonia** 构建。
> 目标不是重写 WebUI，而是为 DSH WebUI 提供桌面级体验：内嵌浏览器壳 + 服务托管 + 自启动 + 托盘 + 会话完成通知等。

## 功能全景

### 核心壳能力
- **内嵌 DSH WebUI**：`NativeWebView` 承载（WebView2），私有 Runtime 就绪后自动加载
- **自定义标题栏**：拖动/双击最大化/最小化最大化关闭（图标随状态切换）、窗口边缘拉伸、位置大小记忆
- **系统托盘**：最小化到托盘、动态菜单（显示主窗口 / 设置 / 私有 Runtime 最近会话 / Runtime 启停 / 关于 / 退出）；不展示或接管外部 DSH 服务
- **单实例**：重复启动自动唤起已有窗口
- **深色模式**：主题跟随系统（可配置 System/Light/Dark）

### 服务管理
- **私有 DSH Runtime**：仅管理 `%APPDATA%/DSHSharp/dsh-runtime` 中固定版本的官方包；不连接或改动外部、远程和源码 DSH
  - 私有 `DSH_HOME`：会话、插件、设置和凭据位于 `%APPDATA%/DSHSharp/dsh-home`，不与命令行 DSH 混用
  - 首选 3080；端口被占用时自动选择备用本机端口，并在运行时详情显示实际地址
  - 失败诊断：Runtime 启动页显示重试与服务日志尾部（`dsh-service.log`）
- **自启动**：注册 HKCU Run 键，`--autostart` 静默驻留托盘

### 通知与监控
- **会话完成通知**：订阅 `events.mux` 流，`turn/end` 完成事件 → 置顶 Toast（会话名 + 回复开头预览）+ 系统提示音 + 托盘驻留时自动唤起窗口
- **最近会话**：托盘子菜单列出会话（真实标题 + 运行中标记，60s 自动刷新）
- **服务状态栏**：彩色圆点（在线绿/启动橙/离线灰）+ 实际 Runtime 地址与状态
- **双版本与兼容更新**：独立显示 DSH-Sharp 客户端版本、DSH Runtime 版本、npm 最新版本和支持范围；Runtime 更新只安装兼容范围内的精确版本

### DSH 增强插件
- **内置会话域插件**：`dsh-sharp-session` 在 WebUI 网页上下文承载 Esc 停止、复制会话 ID 和工作区打开动作，不依赖 Avalonia 或平台键盘消息
- **交互优先级**：对话框、菜单和列表框优先处理 Esc；当前会话空闲时不发出停止请求
- **托管安装**：通过 DSH 官方插件命令幂等安装到私有 `DSH_HOME`

### 设置页（左侧导航四面板）
- **运行时**：私有 Runtime 状态、实际地址和启动/停止操作
- **插件**：随客户端提供的 DSH 增强插件
- **偏好设置**：自启动 / 关闭到托盘 / 启动最小化 / 会话通知 / 提示音 / 主题
- **版本与更新**：客户端版本 / Runtime 版本 / 兼容性检查 / 更新兼容 Runtime

## 技术栈

| 组件 | 版本 |
| --- | --- |
| .NET | 10.0 |
| Avalonia | 12.1（Fluent 主题，`WindowDecorations` 自绘标题栏） |
| 内嵌 WebView | [Avalonia.Controls.WebView](https://www.nuget.org/packages/Avalonia.Controls.WebView) 12.1（WebView2 / WebKit / WebKitGTK） |
| MVVM | CommunityToolkit.Mvvm |
| 单元测试 | xUnit（39 项）+ Vitest（8 项） |

## 解决方案结构

```
DSHSharp.slnx
├── src/
│   ├── DSHSharp/              # Avalonia 桌面壳（窗口/托盘/WebView/通知/组装）
│   └── DSHSharp.Core/         # 核心服务层（配置/自启动/单实例/事件监控/服务托管/RPC）
├── plugins/dsh-sharp-session/  # 随客户端发布的 DSH WebUI 会话域插件
└── tests/DSHSharp.Core.Tests/
```

详细分层与关键流程见 [docs/architecture.md](docs/architecture.md)。

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

## 配置

配置文件：`%APPDATA%/DSHSharp/settings.json`（设置页可视化编辑）。Runtime 数据位于 `%APPDATA%/DSHSharp/dsh-home`。

```json
{
  "AutoStartEnabled": false,
  "CloseToTray": true,
  "Theme": "System",
  "SessionCompleteNotifications": true,
  "NotificationSoundEnabled": true
}
```

## 文档

- [架构文档](docs/architecture.md) — 分层/模块/关键流程/DSH 协议
- [架构决策 0001](docs/decisions/0001-single-private-runtime.md) — 单一私有 Runtime 的所有权边界
- [路线图](docs/roadmap.md) — 系统通知、客户端更新等规划
- [版本与兼容性](docs/versioning.md) — DSH-Sharp 与 DSH 的双版本契约和更新边界
- [贡献指南](CONTRIBUTING.md) · [安全策略](SECURITY.md)

## 许可

[MIT](LICENSE)

> 应用图标（`src/DSHSharp/Assets/avalonia-logo.ico`）使用 DSH 官方 favicon 图形，
> 来源：[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) `apps/web/public/favicon.svg`，
> 生成脚本见 `tools/gen-icons/`。
