# DSH-Sharp 归档说明与 DSH 协议笔记

> 本文件归档于 2026-09-10,记录项目停止维护的原因，以及开发过程中逆推出来的 DSH 协议知识。
> 这些内容分散在提交历史与当时的排查记录里，集中留档以便后续查考（例如在自己写 DSH 插件、脚本或客户端时）。

## 一、为什么归档

DeepSeek 官方已在 `deepseek-ai/deepseek-harness` 仓库内实现自己的 Electron 桌面端：

- 目录：`apps/desktop`（`@deepseek-ai/dsh-desktop`,Electron 壳）与 `apps/desktop-host`（私有子进程宿主，不发布到 npm）
- 架构决策记录：`.agents/notes/implemented/architecture/2026-08-25-electron-desktop-packaging-and-updates.zh.md`,状态 **implemented**
- 打包链路：electron-builder 面向 `mac-arm64` / `mac-x64` / **`win-x64`**;macOS 走公证，Windows 走 EV 签名（SafeNet Token + self-hosted runner）；`electron-updater` + COS 托管的频道元数据（`latest.yml` / `alpha.yml`）
- 截至归档时（2026-09-10）**尚未面向用户发布**：`dsh-v0.1.5-rc.1` 的 Release 资产数为 0,发布说明未提及桌面端，根 README 无下载入口，CI 无 desktop 打包 workflow（打包依赖生产签名环境）。源码可通过 `pnpm run dev:desktop` 直接运行

DSH-Sharp 作为替代性桌面壳的价值已被官方方案覆盖，因此停止维护。仓库转为只读，源码与历史 Release 保留可访问。

### 与官方桌面端的关键设计差异

| 维度 | DSH-Sharp | 官方桌面端 |
| --- | --- | --- |
| 更新单元 | 壳与 DSH Runtime **解耦**：Runtime 是私有目录中的 npm 包，可独立升级（实测可当天跟进官方新版本，无需重发客户端） | 壳 + dsh seed + Node + pnpm 打包为**一个签名发布单元**；"即使壳代码没有变化，更新 dsh 也必须发布新的 Desktop 版本" |
| 通信 | loopback HTTP + token/cookie 认证 | 不开放监听端口：`dsh-app://` 协议 + 分帧字节管道 + Node IPC |
| 运行时依赖 | 依赖系统 Node 与 npm/pnpm 安装私有包 | 内置 Node 24.17.0 与 pnpm 11.7.0,离线 seed store（16 个确定性 tar 分片） |
| 数据归属 | 私有 `DSH_HOME`（`%APPDATA%\DSHSharp\dsh-home`） | 保留 profile `$DSH_HOME/profiles/desktop`,与 npm 安装的 dsh 共享 `~/.dsh` 产品数据 |
| 桌面集成 | 托盘常驻、自启动、会话完成通知、Esc 停止、托盘会话跳转 | 窗口、单实例锁、插件 GUI、更新与回滚（不覆盖常驻助手类能力） |

---

## 二、DSH 协议笔记

> 适用版本：官方已验证 `0.1.0-rc.8`、`0.1.1-rc.2`、`0.1.2-rc.1`、`0.1.5-alpha.2`、`0.1.5-rc.1`。
> 下列内容均为在本机私有 Runtime 上实测或从官方源码确认得出。

### 1. 浏览器认证：token → cookie（0.1.2 起）

- Runtime 启动后打印：`dsh web: http://127.0.0.1:<port>/?token=<随机令牌>`（先监听端口，后异步输出该行）
- **唯一**的令牌使用方式是根路径交换：`GET /?token=...` → `303 See Other` + `Set-Cookie`
- cookie 特征：名为 `dsh-auth-<哈希>`、`Path=/`、`HttpOnly`、`SameSite=Strict`、默认 `Max-Age=2592000`（30 天）、**绑定规范化 hostname 与 port**（端口变化即失效）、loopback HTTP 所以刻意不带 `Secure`
- 签名密钥是 `ctx.credentials` 中的 grant 记录，本地持久化在 `$DSH_HOME/.credentials.yaml`
- 交换完成后的所有 HTTP RPC 与 WebSocket **只认 cookie**;官方文档明确："HTTP 载体不在根路径交换之外接受 query token,也不接受 Authorization header token"
- 未认证返回 **401**;Host 非 loopback / Origin 与 Host 不符返回 **403**（防 DNS rebinding）

> 踩坑记录：把 token 拼在请求 URL 的 query 上做 RPC（`/api/xxx?token=...`）一律 401;`new Uri(base, "api/...")` 这类相对拼接还会丢弃 base 的 query。
> 另外服务端在 token 交换后会 303 到干净的 `/`,**query 被清空**——需要随页面传递的参数（如"打开指定会话"、功能开关）必须放在 **fragment**,浏览器重定向会保留 fragment。

### 2. HTTP RPC

请求/响应信封：

```
POST /api/<domain>/<method>
{ "type": "client-request", "rpcId": "...", "method": "session/list", "payload": { ... } }

{ "type": "server-response", "rpcId": "...", "result": { "ok": true, "value": { ... } } }
{ "type": "server-response", "rpcId": "...", "result": { "ok": false, "error": { "code", "message", "details" } } }
```

- **方法名用斜杠**（`session/list`）,不是点号（0.1.0/0.1.1 时代是 `session.list`）
- **payload 必须包裹 `args`**:
  - 无实参方法：`{ "args": { "_request": {} } }` —— 否则报 `Remote payload must contain exactly one plain-object args field`
  - 有实参方法：`{ "args": { "request": { ... } } }` —— 参数名不匹配会报 `args fields do not match the descriptor: missing "request"`
- 常见错误码：`gateway/arguments-invalid`（形状不符）、`gateway/input-invalid`（字段校验失败）、`gateway/bad-request`、`gateway/service-unavailable`

关键端点（0.1.5 实测）：

| 端点 | payload | 说明 |
| --- | --- | --- |
| `session/list` | `{args:{_request:{}}}` | 会话列表；标题在 `value.items[].projections.values.title`,`projections.asOfSeq` 是分页游标，`running` 为运行态 |
| `session/page` | `{args:{request:{address:{kind:'session',sessionId},throughSeq,maxMessages}}}` | 会话历史；记录在 `value.records[].event`,`throughSeq` **不得越过会话游标**（越界报 `session page through seq N is past cursor M`），取值来自上面摘要的 `projections.asOfSeq`;回复文本在最后的 `assistant/message` 的 `data.message.content[].text` |
| `session/create` | `{args:{request:{cwd/workspaceId/sessionId/agentPreset}}}` | 建会话 |
| `session/rename` | `{args:{request:{sessionId,title}}}` | 改标题（会触发 `api-session/added`） |
| `session/cancel` | 见客户端 `sessions.binding(id).session.cancel()` | 协作式停止当前轮次 |
| `session/openWorkspacePath` | `{args:{request:{path, action?}}}` | 在系统文件管理器中打开路径；`action:'reveal'` 为"定位"语义，省略为"打开"；返回 `{opened:true}` |

- `host.describe` **在 0.1.2 起已不存在**（404）——不要再依赖它获取版本；版本应取自私有安装的 `@deepseek-ai/dsh` 包（`package.json`）
- 旧版（0.1.0/0.1.1）用点号方法名与平铺 payload（`session.history` + `{sessionId,maxMessages}`）,可作为兼容回退

### 3. 事件流 `/api/remote.mux`（0.1.2 起）

旧版 `ws://<host>/api/events.mux` 已被取代。新协议是 Gateway 拥有的**逻辑流**多路复用通道：

1. 建立 WebSocket 到 `/api/remote.mux`（需要 cookie,否则握手 401）
2. 客户端发送开流请求：
   ```json
   { "type": "open", "streamId": "<任意非空字符串>", "endpoint": "$events", "payload": { "args": {} } }
   ```
   合法客户端消息只有 `open` 与 `cancel`（`{"type":"cancel","streamId":...}`）,且 key 必须精确匹配
3. 服务端下行帧：
   ```json
   { "type": "item", "streamId": "...", "value": { ... } }   // 数据
   { "type": "end",  "streamId": "..." }                     // 正常终止
   { "type": "error","streamId": "...", "error": {...} }      // 异常终止
   ```
4. **首项必为 ready**:`{"type":"ready","clientId":"<uuid>","host":{"home":"..."}}`;在此之前不能认为流可用

事件项形态为 `{"type":"emit","event":"<事件名>","args":[...]}`:

| 事件 | args | 用途 |
| --- | --- | --- |
| `api-session/status` | `[sessionId, isRunning]` | 会话运行态翻转 |
| `api-session/added` | `[会话摘要对象]` | 与 `session/list` 的 item 同构（含 `projections.values.title`、`projections.asOfSeq`、`running`） |
| `api-session/removed` | `[sessionId]` | 会话移除 |
| `api-session/activity` | `[sessionId, time]` | 用户消息活动（仅在 `user/message` 且来源为 user 时发出） |
| `commands/change` | `[]` | 命令表变化 |

**会话完成判定**（用于"任务完成"通知）：

- 维护每个会话的 running 状态，`true → false` 的**边沿**视为一轮完成
- 首次观测（此前未知）只记录状态、**不触发**完成——否则会把打开页面时的空闲会话全部误报为"已完成"
- `api-session/status` 与 `api-session/added` 都携带 running,两个来源都要参与边沿检测，避免漏事件
- 旧版回退：`events.mux` 的 `session/event` 信封 `{type,seq,time,data}`,`turn/end` 且 `data.reason.kind === 'completed'` 直接触发完成

### 4. CLI 启动陷阱：`import.meta.main`（0.1.5+）

0.1.5 系列的 `lib/bin.js` 结尾是：

```js
if (import.meta.main) await runCli()
export { runCli }
```

`import.meta.main` 是较新的 Node 特性，**Node 22/23 以下为 `undefined`**（实测 Node v22.17.1 返回 undefined,该特性直到 Node 24 才稳定可用）。结果是进程**静默退出、退出码 0、无任何输出**——表现就像"runtime 启动失败但没有任何错误信息"。

绕法：显式调用它导出的 `runCli()`:

```js
// dsh-entry.mjs
import { runCli } from "file:///<...>/node_modules/@deepseek-ai/dsh/lib/bin.js"
await runCli()
```

用 `node dsh-entry.mjs web --no-open --host 127.0.0.1 --port 3080` 启动即可，对已有 main 判定的 Node 24+ 行为一致。

### 5. Web 客户端插件 API（浏览器侧）

插件可注入的客户端服务：

- `ctx.sessions` — `open(id)`、`binding(id)`、`list.getSnapshot()`（含 `current` 与 `byId`）、`search`、`fork`、`refresh` 等
- `ctx.connection` — `rpc.call(channel, endpoint, payload)`;发往 Host 的通用 RPC carrier，浏览器侧等价于上面第 2 节的 HTTP RPC
- `ctx.slots` — Slot 注册（`shell.overlay`、`sidebar`、`main` 等）
- `ctx.workspaces` — `list.getSnapshot()`、`create/rename/delete/archiveSession` 等

**踩坑记录：**

- ⚠️ **没有客户端 `workspaces.openPath`**。该能力只存在于 Host 内部（`openNativePath`/`revealNativePath`）,历史上也从未暴露给客户端。浏览器侧要在文件管理器中打开目录，必须走 RPC `session/openWorkspacePath`
- `sessions.open(id)` 要求目标**已在客户端会话列表里**,否则抛 `sessions.select: unknown session <id>`;整页重载后插件加载早于列表 baseline,**必须先等待目标出现在 `list.getSnapshot().byId` 里再调用**（否则表现为"跳转无反应、停在默认视图"）
- `ctx.connection` 必须显式加入 `inject` 数组才可用
- 0.1.5 变更：移除 `ctx.agent`（调用方需显式传 Agent）;`Inbox` 改为类型接口、不再导出可构造的运行时类；Web 插件面板 API 调整（`conversation` Slot 迁移为 `main` 的 `conversation` key）；`sidebar.panellist` 与 `main` 可用于注册全局面板
- 客户端插件 bundle 由 `/plugins` 前缀路由提供，入口在首页注入的模块清单里（形如 `/plugins/??<pkg>/client.js,...`）

### 6. 会话数据格式与版本

- **0.1.5 起为 Session V3**:系统提示词提升为消息、Assistant 流按 attempt 聚合、旧 PTC 事件与 `code` 预设引用自动迁移；旧日志由 Host 迁移**并保留原文件**
- ⚠️ **升级后的会话不支持降级读取**——升级 Runtime 后若回滚到旧版本，迁移过的会话可能无法被旧版读取
- npm 标签可能滞后：0.1.5 期间 `latest` 仍指向 `0.1.2-rc.1`,新版本挂在 `next`/`alpha` 上。判断"有没有新版本"不能只看 `latest`
- 官方源码开发标签（如 `0.1.3-alpha.x`）与发布线并存，版本管理需用白名单精确匹配，不要用范围判断

### 7. 私有 Runtime 运维要点

- 端口默认 3080,被占用时向上回退；实际监听地址在本次运行中才确定
- 进程所有权：只终止自己 spawn 的 Runtime（`Process.Kill(entireProcessTree: true)`）;**不要探测/连接/停止用户自己的 DSH**
- 孤儿清理：pid 文件之外，建议按**进程命令行**兜底扫描（命令行同时包含私有 `node_modules` 路径与 `bin.js`/自定义入口），这样能区分"自己人"与外部/源码 DSH
- Runtime 升级宜采用 staging → 健康检查 → 目录事务交换（记录 Prepared/ActiveMoved/Promoted 相位）→ 失败回滚的模式，启动时先恢复未完成事务
- WebView2 承载时注意：`about:blank` 预清空再设真实地址可强制触发新导航；离屏/隐藏 WebView 可避免原生表面遮挡引导页

---

## 三、数据与清理

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\DSHSharp\dsh-home` | 私有 DSH_HOME（`sessions` 会话、`profiles` 插件配置、`settings.yaml`） |
| `%APPDATA%\DSHSharp\dsh-runtime` | 私有 DSH Runtime（npm 依赖，可安全删除） |
| `%APPDATA%\DSHSharp\app.log` / `dsh-service.log` | 客户端与服务日志 |

> 私有 `dsh-home` 与官方 dsh 的 `~/.dsh` 是**两个独立的数据根**,会话不会自动继承，需要时手动迁移。
