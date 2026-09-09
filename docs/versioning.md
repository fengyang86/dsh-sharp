# 版本与兼容性

DSH-Sharp 与 DSH 是两个独立发布物，不能把客户端版本当成服务版本。

## 当前契约

| 发布物 | 当前版本 | 说明 |
| --- | --- | --- |
| DSH-Sharp 客户端 | `0.2.3` | 桌面壳、服务托管与插件宿主、客户端自更新 |
| DSH | 仅已验证版本 | 客户端声明的支持范围 |

已验证 DSH 版本：`0.1.0-rc.8`、`0.1.1-rc.2`、`0.1.2-rc.1`、`0.1.5-alpha.2`。验证覆盖 token→cookie 浏览器认证、HTTP RPC（`session/list`、`session/page`）与 `remote.mux` 事件流；旧版方法名（`session.list`、`session.history`）与 `events.mux` 事件流在运行时自动回退。0.1.5-alpha.2 的 bin.js 依赖 `import.meta.main` 自分发（Node < 23 恒为 undefined），客户端通过显式调用 `runCli()` 的 wrapper 启动。Session 数据格式 V3 由 host 端自动迁移，旧会话对客户端透明。中间版本（`0.1.3-alpha.x`、`0.1.5-alpha.1`）未验证、不在支持范围内。

## 设置页显示

设置页将信息分成两个独立区域：DSH-Sharp 客户端只显示客户端版本和客户端更新策略；DSH 运行时显示私有安装版本、实际监听地址、npm 最新版本、支持范围和兼容状态。DSH-Sharp 只管理私有运行时，不连接源码、远程或外部运行的 DSH。兼容性依据是私有安装的 `@deepseek-ai/dsh` 精确包版本（新版 DSH 已移除 `host.describe`，服务实例不再自报版本）。

## 更新规则

首次安装后固定私有目录中的精确 DSH 版本。显式更新时先查询 npm：`latest` 标签在支持范围内则取之，否则回退到客户端最新已验证版本（官方常把新发布挂在 `alpha`/`next` 标签下，`latest` 可能滞后）；再使用版本白名单判断目标版本是否可安装。Runtime 与其 `DSH_HOME` 数据目录分离；更新不得清除会话、插件、设置或凭据。未来发布新客户端时，先更新兼容契约和验证矩阵，再开放新的 DSH 范围。

### 客户端自更新（0.2.3 起）

DSH-Sharp 从 GitHub Releases 检查自身更新（仅正式版，不含 pre-release）。发现新版本后自动在后台预下载升级包（显示百分比进度，SHA256 校验后解压）；下载就绪后用户点击"升级并重启"，由 staging 中的新 EXE 以 `--apply-update` 模式接管：等旧实例退出 → 覆盖安装目录 → 重启。网络不可达时静默降级为可手动重试；"自动检查客户端更新"可在设置页关闭。用户数据（设置、私有 Runtime、会话）均在 `%APPDATA%/DSHSharp`，不受客户端更新影响。
