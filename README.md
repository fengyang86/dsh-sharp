# DSH-Sharp

> DeepSeek Harness（DSH）的桌面客户端壳，基于 **.NET 10 + Avalonia** 构建。
> 目标不是重写 WebUI，而是为 DSH WebUI 提供桌面级体验：内嵌浏览器壳 + 服务托管 + 自启动 + 托盘 + 会话完成通知等。

## 功能全景

### 核心壳能力
- **内嵌 DSH WebUI**：`NativeWebView` 承载（WebView2），地址可配置
- **自定义标题栏**：拖动/双击最大化/最小化最大化关闭（图标随状态切换）、窗口边缘拉伸、位置大小记忆
- **系统托盘**：最小化到托盘、动态菜单（显示主窗口 / 设置 / 最近会话 / 本地服务 / 关于 / 退出）
- **单实例**：重复启动自动唤起已有窗口
- **深色模式**：主题跟随系统（可配置 System/Light/Dark）

### 服务管理
- **多服务配置**：配置列表（名称/地址/托管模式/源码路径），新增/复制/删除/设为当前，**切换即时生效**（无需重启）；旧配置自动迁移
- **服务托管三模式**：Npx（官方包）/ Source（源码仓库）/ None（纯探测）
  - 探测优先：服务在线即用（无论谁启动的），绝不重复拉起
  - 所有权语义：仅客户端拉起的服务进程在退出时终止
  - Source 模式环境预检（依赖/pnpm/node）+ pnpm 缺失自动降级 `node --import tsx/esm` 直跑
  - 失败诊断：引导页显示服务日志尾部（`dsh-service.log`）
- **端口纠错**：离线时扫描常见端口（3080/3000/8080/8000），发现服务提示一键切换
- **引导页**：未检测到服务时显示（启动/重试/切换提示/错误详情）
- **自启动**：注册 HKCU Run 键，`--autostart` 静默驻留托盘

### 通知与监控
- **会话完成通知**：订阅 `events.mux` 流，`turn/end` 完成事件 → 置顶 Toast（会话名 + 回复开头预览）+ 系统提示音 + 托盘驻留时自动唤起窗口
- **最近会话**：托盘子菜单列出会话（真实标题 + 运行中标记，60s 自动刷新）
- **服务状态栏**：彩色圆点（在线绿/启动橙/离线灰）+ `配置名 · 地址 · 状态`
- **版本检查与更新**：运行版本（`host.describe`）；npx 模式对比 npm 最新发布版（更新=重启托管拉新）；源码模式对比 git 远端提交（提示 `git pull`）

### 设置页（左侧导航四面板）
- **服务配置**：状态卡片（启动/停止按钮）+ 多配置管理
- **通用**：自启动 / 关闭到托盘 / 启动最小化 / 会话通知 / 提示音 / 主题
- **版本更新**：检查更新 / 更新服务
- **关于**：版本信息

## 技术栈

| 组件 | 版本 |
| --- | --- |
| .NET | 10.0 |
| Avalonia | 12.1（Fluent 主题，`WindowDecorations` 自绘标题栏） |
| 内嵌 WebView | [Avalonia.Controls.WebView](https://www.nuget.org/packages/Avalonia.Controls.WebView) 12.1（WebView2 / WebKit / WebKitGTK） |
| MVVM | CommunityToolkit.Mvvm |
| 单元测试 | xUnit（34 项） |

## 解决方案结构

```
DSHSharp.slnx
├── src/
│   ├── DSHSharp/              # Avalonia 桌面壳（窗口/托盘/WebView/通知/组装）
│   └── DSHSharp.Core/         # 核心服务层（配置/自启动/单实例/事件监控/服务托管/RPC）
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

配置文件：`%APPDATA%/DSHSharp/settings.json`（设置页可视化编辑）。

```json
{
  "WebUrl": "http://127.0.0.1:3080",
  "ManagedMode": "Source",
  "SourcePath": "D:\\deepseek-harness",
  "Profiles": [
    { "Name": "默认配置", "WebUrl": "http://127.0.0.1:3080", "ManagedMode": "Source", "SourcePath": "D:\\deepseek-harness" }
  ],
  "ActiveProfileName": "默认配置",
  "AutoStartEnabled": false,
  "CloseToTray": true,
  "Theme": "System",
  "SessionCompleteNotifications": true,
  "NotificationSoundEnabled": true
}
```

## 文档

- [架构文档](docs/architecture.md) — 分层/模块/关键流程/DSH 协议
- [路线图](docs/roadmap.md) — 多标签、标签拖出独立窗口、系统通知等规划

## 许可

[MIT](LICENSE)

> 应用图标（`src/DSHSharp/Assets/avalonia-logo.ico`）使用 DSH 官方 favicon 图形，
> 来源：[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) `apps/web/public/favicon.svg`，
> 生成脚本见 `tools/gen-icons/`。
