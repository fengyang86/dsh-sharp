# DSH-Sharp 架构

> DeepSeek Harness 桌面客户端壳：内嵌 WebUI + 服务托管 + 桌面集成。
> 架构原则：**壳不做业务**——所有 DSH 交互走官方协议（HTTP RPC + WebSocket 事件流），
> UI 层与服务层分离。DSH-Sharp 只托管自身私有 DSH Runtime，不承担外部服务连接器职责。

## 0. 产品边界与扩展主线

- **单一私有 Runtime**：客户端管理私有安装目录、私有 `DSH_HOME`、固定版本、显式更新和进程生命周期。
- **外部边界**：不探测、不连接、不停止用户已启动或源码部署的 DSH；源码仓库属于开发环境。
- **端口归属**：优先 3080；被占用时自动选择备用本机端口，实际地址仅在本次运行中有效。
- **统一命令入口**：快捷键、托盘、窗口按钮和未来插件动作复用同一客户端命令。
- **连接归属**：命令、快捷键和 DSH 增强插件最终都归属于明确的连接与当前会话。
- **插件边界**：客户端是宿主，功能按领域拆分为 DSH 插件；不开放任意 .NET DLL 客户端插件。
- **插件组织**：插件按功能域拆分（会话、工作区、集成等），客户端按内置插件套件统一安装、升级和管理。

## 1. 版本契约

DSH-Sharp `0.2.0` 支持 DSH `>=0.1.0-rc.8 <0.2.0`，已验证 `0.1.0-rc.8` 和 `0.1.1-rc.2`。客户端版本、运行服务版本、私有安装版本和 npm 最新版本始终分开建模；更新边界由 `DSHSharp.Core.Compatibility.DshSharpCompatibility` 统一判断。详见 [versioning.md](versioning.md)。

## 2. 解决方案结构

```
DSHSharp.slnx
├── src/
│   ├── DSHSharp/                  # Avalonia 桌面壳（UI + 组装层）
│   │   ├── Program.cs             # 入口：单实例检查 + --autostart
│   │   ├── App.axaml(.cs)         # 全局组装：托盘/监控/托管/通知/配置切换
│   │   ├── Views/
│   │   │   ├── MainWindow         # 主窗口：自定义标题栏 + WebView + 引导页 + 状态栏
│   │   │   ├── SettingsWindow     # 设置：左侧导航四面板（服务配置/通用/版本/关于）
│   │   │   └── ToastWindow        # 置顶通知小窗口（独立 HWND，规避 WebView2 遮挡）
│   │   ├── ViewModels/            # MainWindowViewModel / SettingsViewModel
│   │   └── Services/              # NotificationSound（Win32 PlaySound）
│   └── DSHSharp.Core/             # 核心服务层（不依赖 UI，可单元测试）
│       ├── Configuration/         # AppSettings / ServiceProfile / ProfileHelper
│       ├── Services/              # 设置持久化 / 自启动 / 单实例
│       └── Dsh/                   # 事件监控 / 服务托管 / HTTP RPC / 帧解析
└── tests/DSHSharp.Core.Tests/     # 34 项单元测试
```

## 3. 分层职责

### 2.1 DSHSharp.Core（服务层）

| 模块 | 职责 |
| --- | --- |
| `Configuration/AppSettings` | 设置模型：通用开关 + 窗口状态；历史连接字段仅兼容读取，不参与 Runtime 选择 |
| `Services/AppSettingsService` | settings.json 持久化（%APPDATA%/DSHSharp/） |
| `Services/AutoStartService` | 登录自启动：Windows 注册表 Run 键（`--autostart`） |
| `Services/SingleInstanceService` | 命名 Mutex 单实例 + 命名事件唤起已有窗口 |
| `Dsh/DshEventMonitor` | 事件监控：mux WebSocket 流（turn/end 完成、session/title）+ HTTP 心跳 |
| `Dsh/DshServiceManager` | 私有 Runtime：私有包、私有 `DSH_HOME`、端口回退、进程所有权、失败诊断 |
| `Dsh/DshApiClient` | HTTP RPC 客户端：session.list / session.history / host.describe / npm 版本 |
| `Dsh/DshFrameParser` | mux 流帧解析（信封格式 {type,seq,time,data}） |
| `Dsh/DshRpcParser` | RPC 响应解析（会话列表/回复文本/版本号） |

### 2.2 DSHSharp（壳层）

| 模块 | 职责 |
| --- | --- |
| `App` | 唯一组装点：启动私有 Runtime 后创建监控/API 客户端，托盘菜单和通知分发 |
| `MainWindow` | 自定义标题栏、NativeWebView、Runtime 加载/故障页、状态栏 |
| `SettingsWindow` | 左侧导航：运行时 / 插件 / 偏好设置 / 版本与更新 |
| `ToastWindow` | 右下角置顶通知（独立窗口，规避 WebView2 原生表面 airspace） |

## 4. 关键流程

### 3.1 启动流程

```
Program.Main
 ├─ 单实例检查（非首实例 → 通知唤起 → 退出）
 ├─ 解析 --autostart
 └─ Avalonia 启动
     └─ App.OnFrameworkInitializationCompleted
         ├─ 加载设置（历史连接字段仅兼容读取）
         ├─ 应用主题 / 自启动状态同步
         ├─ 主窗口（恢复窗口状态）
         ├─ 托盘（动态菜单：显示/设置/私有 Runtime 最近会话/Runtime 启停/关于/退出）
         ├─ 服务管理器启动私有 Runtime
         └─ Runtime 健康后创建事件监控、会话刷新和 WebView 导航
```

### 3.2 私有 Runtime 启动流程

```
应用启动
 ├─ 显示 Runtime 准备页，WebView 不导航
 ├─ 确保私有目录中存在固定版本的官方包与内置插件
 ├─ 设置 DSH_HOME=%APPDATA%/DSHSharp/dsh-home
 ├─ 尝试 127.0.0.1:3080；被占用时尝试 3081..3090
 ├─ 轮询实际绑定地址，健康后才加载 WebView
 └─ 失败时显示重试与服务日志尾部
所有权语义：仅客户端 spawn 的私有 Runtime 会在退出/停止时被杀（Process.Kill 进程树）。
外部或源码 DSH 不被探测、连接或停止。
```

### 3.3 会话完成通知流程

```
mux 流帧（session/event, turn/end, reason.kind=completed）
 └─ 信封解析（data.reason.kind）
     └─ SessionCompleted 事件（后台线程）
         └─ 异步取回复预览（session.history → 最后 assistant/message 文本）
             └─ UI 线程：Toast（会话名 + 回复开头）+ 系统提示音 + 托盘驻留时唤起窗口
```

## 5. DSH 官方协议（客户端直接消费）

- **HTTP RPC**：`POST /api/<method>`，请求 `{type:'client-request', rpcId, method, payload}`，
  响应 `{type:'server-response', rpcId, result:{ok, value|error}}`
- **事件流**：`ws://<host>/api/events.mux`（连接后服务端直接推送，无握手请求）
  - 帧：`{type:'server-request', rpcId, method, payload:{type:'session/event', sessionId, event}}`
  - `SessionEvent` 信封：`{type, seq, time, data}`（内容在 `data` 内）
  - 关键事件：`turn/end`（`data.reason.kind='completed'` 表示任务完成）、`session/title`（`data.title`）
- **关键 RPC**：
  - `session.list` → 会话列表（标题在 `projections.values.title`）
  - `session.history` → 会话事件（回复文本在最后 `assistant/message` 的 `data.message.content[].text`）
  - `host.describe` → `version`（运行版本）
  - `session.create` / `session.prompt` → 建会话/发消息
  - `session.cancel` → 协作式停止指定会话当前轮次

## 6. 设置、命令与插件

设置页固定为运行时、插件、偏好设置、版本与更新四个一级页面。运行时页展示私有 Runtime 的状态、实际地址和启动操作；插件页展示并管理私有 `DSH_HOME` 中的 DSH 增强插件；偏好设置只包含客户端行为；版本与更新页严格区分客户端更新和 Runtime 更新。

客户端只提供宿主能力（窗口、生命周期、连接、插件安装和权限）；业务功能下沉到按领域拆分的 DSH 插件。当前内置 `dsh-sharp-session` 会话域插件在 DSH WebUI 网页上下文处理 Esc、右键复制会话 ID，并通过公开 `sessions` 服务调用 `session.cancel()`。工作区右键打开资源管理器复用 DSH 官方 `workspaces.openPath(path)`，底层通过 `host.openPath` 做 Windows、macOS 和 Linux 平台适配；插件只贡献菜单界面。会话完成通知、提示音和桌面窗口唤起由客户端事件监控处理，不属于插件。官方包托管模式在私有运行目录内固定安装 `pnpm`，并用 DSH 官方 `plugin` 命令幂等链接随客户端发布的插件；源码、纯探测和远程环境不由客户端写入插件配置。

单一 Runtime 的详细边界见 [决策 0001](decisions/0001-single-private-runtime.md)。
