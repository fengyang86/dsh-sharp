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
- **内置插件能力开关**：内置插件整体状态与领域能力分层管理；会话插件的 Esc 停止、复制会话 ID、工作区打开和托盘跳转均可独立开关，配置由客户端持久化并通过启动地址传入 Web 插件。
- **Runtime 更新**：新版本先在同级 staging 目录完整安装和校验，再通过事务记录执行目录交换；启动失败可恢复 previous。详见 [决策 0002](decisions/0002-atomic-runtime-updates.md)。

## 1. 版本契约

DSH-Sharp `0.2.2` 支持已验证的 DSH `0.1.0-rc.8`、`0.1.1-rc.2` 和 npm 最新稳定预览版 `0.1.2-rc.1`。官方源码开发版本 `0.1.3-alpha.1` 包含 Session v2 与运行环境变更，暂不兼容。客户端版本、运行服务版本、私有安装版本和 npm 最新版本始终分开建模；更新边界由 `DSHSharp.Core.Compatibility.DshSharpCompatibility` 统一判断。详见 [versioning.md](versioning.md)。

## 2. 解决方案结构

```
DSHSharp.slnx
├── src/
│   ├── DSHSharp/                  # Avalonia 桌面壳（UI + 组装层）
│   │   ├── Program.cs             # 入口：单实例检查 + --autostart
│   │   ├── App.axaml(.cs)         # 全局组装：托盘/监控/托管/通知/配置切换
│   │   ├── Views/
│   │   │   ├── MainWindow         # 主窗口：自定义标题栏 + WebView + 引导页 + 状态栏
│   │   │   ├── SettingsWindow     # 设置：左侧导航四面板（私有 Runtime/插件/偏好/版本）
│   │   │   └── ToastWindow        # 置顶通知小窗口（独立 HWND，规避 WebView2 遮挡）
│   │   ├── ViewModels/            # MainWindowViewModel / SettingsViewModel
│   │   └── Services/              # NotificationSound（Win32 PlaySound）
│   └── DSHSharp.Core/             # 核心服务层（不依赖 UI，可单元测试）
│       ├── Configuration/         # AppSettings（兼容读取旧连接配置）
│       ├── Services/              # 设置持久化 / 自启动 / 单实例
│       └── Dsh/                   # 事件监控 / 服务托管 / HTTP RPC / 帧解析
└── tests/DSHSharp.Core.Tests/     # 55 项单元测试
```

平台图标资源位于 `src/DSHSharp/Assets/Brand` 与 `packaging/`：Avalonia 窗口和托盘使用 PNG，Windows 将多尺寸 ICO 嵌入 EXE，Linux 使用 desktop/hicolor，macOS 使用 ICNS。

## 3. 分层职责

### 2.1 DSHSharp.Core（服务层）

| 模块 | 职责 |
| --- | --- |
| `Configuration/AppSettings` | 设置模型：通用开关 + 窗口状态；历史连接字段仅兼容读取，不参与 Runtime 选择 |
| `Services/AppSettingsService` | settings.json 持久化（%APPDATA%/DSHSharp/） |
| `Services/AutoStartService` | 登录自启动：Windows 注册表 Run 键（`--autostart`） |
| `Services/SingleInstanceService` | 命名 Mutex 单实例 + 命名事件唤起已有窗口 |
| `Dsh/DshAuthSession` | 认证会话：启动 token 换签名 cookie，RPC/事件流/WebView 共享，401 或重启后重交换 |
| `Dsh/DshEventMonitor` | 事件监控：remote.mux 逻辑流（api-session 边沿判定完成）+ HTTP 心跳；旧版 events.mux 自动回退 |
| `Dsh/DshServiceManager` | 私有 Runtime：私有包、私有 `DSH_HOME`、端口回退、进程所有权、失败诊断、孤儿清理（pid 文件 + 命令行兜底扫描） |
| `Dsh/DshApiClient` | HTTP RPC 客户端：session/list / session/page / npm 版本（新旧方法名自动回退） |
| `Dsh/DshFrameParser` | 事件帧解析（新 emit 帧 + 旧 SessionEvent 信封） |
| `Dsh/DshRpcParser` | RPC 响应解析（会话列表/分页记录/npm 版本） |

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

### 3.3 Runtime 更新流程

```
检查兼容范围
 └─ dsh-runtime-staging 独立安装并校验
     ├─ 失败：删除 staging，继续当前版本
     └─ 成功：记录 Prepared
         └─ 当前目录改名 previous（ActiveMoved）
             └─ staging 改名 active（Promoted）
                 ├─ 健康：删除 previous，清理事务记录
                 └─ 失败：交换回 previous 并重启
```

应用启动时先处理未完成事务，避免进程在目录交换窗口退出后留下不可用状态。
```

### 3.3 会话完成通知流程

```
remote.mux emit 帧（api-session/status 或 api-session/added）
 └─ running true→false 边沿（首次观测只记录，避免空闲会话误报）
     └─ SessionCompleted 事件（后台线程，携带标题与 asOfSeq 游标）
         └─ 异步取回复预览（session/page + throughSeq → 最后 assistant/message 文本）
             └─ UI 线程：Toast（会话名 + 回复开头）+ 系统提示音 + 托盘驻留时唤起窗口
```

旧版 runtime 回退路径：events.mux 帧 `turn/end` + `reason.kind=completed` 直接触发完成。

## 5. DSH 官方协议（客户端直接消费）

DSH 0.1.2-rc.1 起协议有三处重大变化（客户端已适配，旧版 runtime 自动回退兼容）：

- **浏览器认证（token→cookie）**：每个进程启动生成随机令牌并打印 `dsh web: http://…/?token=…`；
  唯一入口是 `GET /?token=…` → 303 + 绑定 host:port 的签名 cookie（HttpOnly、SameSite=Strict、30 天，
  密钥持久于私有 `DSH_HOME/.credentials.yaml`）。此后所有 HTTP RPC 与 WebSocket 只认 cookie，
  query token 与 Authorization 头均被拒绝；Host 必须 loopback、Origin（若有）必须等于 Host。
  客户端由 `DshAuthSession` 统一兑换：WebView、HTTP RPC 与事件流共享同一 cookie 容器，
  runtime 重启/端口漂移后自动重交换。旧版 runtime（无 token URL）直连，无需兑换。
- **HTTP RPC**：`POST /api/<domain>/<method>`，方法名用斜杠（`session/list`），payload 需
  `{ args: { _request | request } }` 包装（无实参方法用 `_request`，有实参方法用 `request`）。
  `session/page` 的 `throughSeq` 不得越过会话游标（取摘要 `projections.asOfSeq`）；`host.describe` 已移除。
  客户端对旧版点号方法名（`session.list`）与平铺 payload 做运行时回退。
- **事件流**：`ws://<host>/api/events.mux` 已移除，改为 `/api/remote.mux` 逻辑流——
  连接后发送 `{type:'open', streamId, endpoint:'$events', payload:{args:{}}}`，
  服务端先回 `{type:'item', value:{type:'ready', clientId, host}}` 首项，再推送
  `{type:'item', value:{type:'emit', event, args}}` 事件项与 `{type:'end'|'error'}` 终止帧。
  会话完成由 `api-session/status`（args `[sessionId, isRunning]`）与 `api-session/added`
  （args `[会话摘要]`）的 running true→false 边沿判定；旧版信封（`turn/end` + `data.reason.kind`）仅在回退路径解析。
- **关键 RPC**：
  - `session/list` → 会话列表（标题在 `projections.values.title`，`projections.asOfSeq` 是分页游标）
  - `session/page` → 会话历史（`args.request.address.{kind,sessionId}` + `throughSeq`，回复文本在最后 `assistant/message` 的 `records[].event.data.message.content[].text`）
  - `session.create` / `session/prompt` / `session.cancel` → 建会话/发消息/协作停止

## 6. 设置、命令与插件

设置页固定为运行时、插件、偏好设置、版本与更新四个一级页面。运行时页展示私有 Runtime 的状态、实际地址和启动操作；插件页展示并管理私有 `DSH_HOME` 中的 DSH 增强插件；偏好设置只包含客户端行为；版本与更新页严格区分客户端更新和 Runtime 更新。

客户端只提供宿主能力（窗口、生命周期、连接、插件安装和权限）；业务功能下沉到按领域拆分的 DSH 插件。当前内置 `dsh-sharp-session` 会话域插件在 DSH WebUI 网页上下文处理 Esc、右键复制会话 ID，并通过公开 `sessions` 服务调用 `session.cancel()`。工作区右键打开资源管理器复用 DSH 官方 `workspaces.openPath(path)`，底层通过 `host.openPath` 做 Windows、macOS 和 Linux 平台适配；插件只贡献菜单界面。会话完成通知、提示音和桌面窗口唤起由客户端事件监控处理，不属于插件。官方包托管模式在私有运行目录内固定安装 `pnpm`，并用 DSH 官方 `plugin` 命令幂等链接随客户端发布的插件；源码、纯探测和远程环境不由客户端写入插件配置。

单一 Runtime 的详细边界见 [决策 0001](decisions/0001-single-private-runtime.md)。
