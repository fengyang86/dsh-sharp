# DSH-Sharp 架构

> DeepSeek Harness 桌面客户端壳：内嵌 WebUI + 服务托管 + 桌面集成。
> 架构原则：**壳不做业务**——所有 DSH 交互走官方协议（HTTP RPC + WebSocket 事件流），
> UI 层与服务层分离，连接（Connection）为一等公民，为多标签多连接预留。

## 0. 产品边界与扩展主线

- **官方包模式**：客户端管理私有安装目录、固定版本、显式更新和进程生命周期。
- **源码模式**：客户端只连接、启动和停止；源码版本、Git、依赖和构建由开发者管理。
- **纯探测模式**：客户端只连接已有服务，不管理服务端环境。
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
| `Configuration/AppSettings` | 设置模型：通用开关 + 窗口状态 + 多配置（Profiles） |
| `Configuration/ServiceProfile` | 一个服务连接配置：名称/地址/托管模式/源码路径 |
| `Configuration/ProfileHelper` | 配置迁移与激活（纯函数）：旧版配置 → 默认 Profile；激活同步顶层字段 |
| `Services/AppSettingsService` | settings.json 持久化（%APPDATA%/DSHSharp/） |
| `Services/AutoStartService` | 登录自启动：Windows 注册表 Run 键（`--autostart`） |
| `Services/SingleInstanceService` | 命名 Mutex 单实例 + 命名事件唤起已有窗口 |
| `Dsh/DshEventMonitor` | 事件监控：mux WebSocket 流（turn/end 完成、session/title）+ HTTP 心跳 |
| `Dsh/DshServiceManager` | 服务托管：探测优先、Npx/Source/None 三模式、进程所有权、失败诊断 |
| `Dsh/DshApiClient` | HTTP RPC 客户端：session.list / session.history / host.describe / npm 版本 |
| `Dsh/DshFrameParser` | mux 流帧解析（信封格式 {type,seq,time,data}） |
| `Dsh/DshRpcParser` | RPC 响应解析（会话列表/回复文本/版本号） |

### 2.2 DSHSharp（壳层）

| 模块 | 职责 |
| --- | --- |
| `App` | 唯一组装点：创建监控/托管/API 客户端，托盘菜单，通知分发，端口纠错，配置切换 |
| `MainWindow` | 自定义标题栏（拖动/双击/按钮状态）、NativeWebView、引导页、状态栏（圆点着色） |
| `SettingsWindow` | 左侧导航：服务配置（多配置管理）/ 通用 / 版本更新 / 关于 |
| `ToastWindow` | 右下角置顶通知（独立窗口，规避 WebView2 原生表面 airspace） |

## 4. 关键流程

### 3.1 启动流程

```
Program.Main
 ├─ 单实例检查（非首实例 → 通知唤起 → 退出）
 ├─ 解析 --autostart
 └─ Avalonia 启动
     └─ App.OnFrameworkInitializationCompleted
         ├─ 加载设置 → Profile 迁移（EnsureDefault + ApplyActive + 落盘）
         ├─ 应用主题 / 自启动状态同步
         ├─ 主窗口（恢复窗口状态）
         ├─ 托盘（动态菜单：显示/设置/最近会话/本地服务/关于/退出）
         ├─ 服务管理器 + 事件监控 + 会话列表定时刷新（60s）
         └─ 心跳探测 → 在线/离线分发
```

### 3.2 服务托管流程（探测优先 + 所有权）

```
心跳发现离线
 └─ HandleOfflineAsync
     ├─ 扫描常见端口（3080/3000/8080/8000）
     │   ├─ 发现其他端口有服务 → 引导页提示切换（不托管）
     │   └─ 未发现 → 按托管模式启动：
     │       ├─ Npx：首次将官方包安装到私有目录并固定版本，随后直接运行 dsh.cmd
     │       ├─ Source：pnpm dsh web / node --import tsx/esm apps/cli/src/bin.ts（pnpm 缺失自动降级）
     │       └─ None：不托管
     ├─ 环境预检（路径/node_modules/pnpm/node）
     ├─ 就绪轮询（Npx 120s / Source 180s）
     └─ 失败诊断：引导页显示服务日志尾部
所有权语义：仅客户端 spawn 的进程在退出/停止时被杀（Process.Kill 进程树）
```

### 3.3 会话完成通知流程

```
mux 流帧（session/event, turn/end, reason.kind=completed）
 └─ 信封解析（data.reason.kind）
     └─ SessionCompleted 事件（后台线程）
         └─ 异步取回复预览（session.history → 最后 assistant/message 文本）
             └─ UI 线程：Toast（会话名 + 回复开头）+ 系统提示音 + 托盘驻留时唤起窗口
```

### 3.4 配置切换流程

```
设置页"设为当前"/保存
 └─ SettingsViewModel → App.SwitchProfile(name)
     ├─ ProfileHelper：激活 + 同步顶层字段（WebUrl/Mode/SourcePath）
     ├─ 持久化
     └─ SwitchWebUrl：重建事件监控 + 服务管理器 + 重载 WebView（即时生效，无需重启）
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

设置页固定为连接、插件、偏好设置、关于与更新四个一级页面。连接页承担服务状态、配置列表和配置详情；插件页展示并管理随客户端安装的 DSH 增强插件；偏好设置只包含客户端行为；关于与更新区分客户端更新和官方包更新，源码模式不提供源码版本管理。

客户端只提供宿主能力（窗口、生命周期、连接、插件安装和权限）；业务功能下沉到按领域拆分的 DSH 插件。当前内置 `dsh-sharp-session` 会话域插件在 DSH WebUI 网页上下文处理 Esc、右键复制会话 ID，并承载会话完成通知、通知中心和会话跳转，通过公开 `sessions` 服务识别会话并调用 `session.cancel()`。工作区右键打开资源管理器复用 DSH 官方 `workspaces.openPath(path)`，底层通过 `host.openPath` 做 Windows、macOS 和 Linux 平台适配；插件只贡献菜单界面。官方包托管模式在私有运行目录内固定安装 `pnpm`，并用 DSH 官方 `plugin` 命令幂等链接随客户端发布的插件；源码、纯探测和远程环境不由客户端写入插件配置。

## 7. 多连接演进

- 设计上连接（地址+监控+托管+会话列表）已可实例化多次；
- 阶段 2：TabControl 多标签，标签休眠控制 WebView 内存；
- 阶段 3：标签拖出独立窗口（`NativeWebView.BeginReparentingAsync` 官方支持跨窗口移动）。

详见 [roadmap.md](roadmap.md)。
