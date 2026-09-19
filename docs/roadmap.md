# DSH-Sharp 路线图

> 固化于 2026-09-19（v0.3.0 开发期）。已实现功能见 README；本文件记录 v0.3.0 的确认范围与其后的规划，按阶段排序。

## 已发布（截至 v0.2.7）

- v0.2.5：恢复维护；DSH 0.1.6-alpha.2 / 0.1.5-rc.2 兼容；事件流探测修复
- v0.2.6：Windows 安装程序（Inno Setup 中文向导，每用户免 UAC）；原生 Toast 点击直达会话；运行状态可视化（标题计数 / 任务栏脉冲 / 托盘实时标记）；外链分流；断链插件自愈；任务栏 CLSID 修复
- v0.2.7：插件链接步骤瞬时失败自动重试（杀软扫描窗口自愈）

## main 已合入、待随 v0.3.0 发布

- 一键备份会话数据 + 导出诊断包（token 脱敏，设置页"数据维护"）
- 失败回合通知区分（`turn/end` reason：error 弹"回合失败"、aborted 静默）
- Ctrl+K 会话切换器（插件侧，纯 DOM）；托盘"打开所在目录"（session/list 的 cwd）；托盘 tooltip 状态（版本/运行数/端口）；web→壳主题桥（`data-ds-theme-source` 官方镜像信号 + `chrome.webview.postMessage`）

## v0.3.0：从能用到顺手的常驻利器（进行中）

| # | 项 | 说明 | 状态 |
| --- | --- | --- | --- |
| 1 | OS 级全局热键 | `Ctrl+Alt+D` 唤起主窗口；`Ctrl+Alt+K` 唤起并呼出会话切换器（经 hash 通道触发插件）；托盘补"新建会话"入口 | 待实施 |
| 2 | 自更新通道加固 | 下载断点续传（HTTP Range，SHA256 校验保持）；GitHub 资产镜像回退（纯直连新机器兜底） | 待实施 |
| 3 | WebView 下载体验 | 视 Avalonia.Controls.WebView 暴露的下载事件而定；至少验证默认下载条行为并留档 | 调查中 |
| 4 | 通知分组 | 完成类 Toast 归入同一 Header，操作中心不再逐条堆叠 | 待实施 |
| 5 | `dshsharp://` 深链 | 安装器注册协议；`dshsharp://session/<id>` 从终端/脚本唤起直达；为进程退出后的通知冷激活铺路 | 待实施 |
| 6 | 切换器浅色适配 | Ctrl+K 面板配色跟随 web 主题（`body[data-ds-dark-theme]`） | 待实施 |
| 7 | README 功能全景刷新 | 安装程序 / 原生通知 / 切换器 / 备份等新能力补进门面 | 待实施 |
| 8 | 版本号单源 | csproj 版本从 `DshSharpCompatibility.ProductVersion` 派生，消除双处手工同步 | 待实施 |
| 9 | ARCHIVE-NOTES 补记 | `turn/end` reason 分类、`data-ds-theme-source` 桥、切换器 hash 通道、深链协议 | 待实施 |

## v0.3.0 之后（按需排期）

- 跨会话全文搜索（经 `session/page` 翻历史；体量最大，候选头牌）
- 多窗口 / 第二会话窗口（WebView2 多实例）
- LAN 手机访问开关（0.1.6 原生能力，默认关 + 风险提示）
- 进程退出后的 Toast 冷激活（协议激活切换，依赖 v0.3.0 #5）
- 热键自定义设置界面（v0.3.0 为固定手势）
- 会话导出（`session.export` RPC → 本地 jsonl）
- WebView 前进/后退/刷新按钮（标题栏）
- npm 镜像源配置（npmmirror 注入 `npm_config_registry`）与官方包预下载预热

## 维护节奏

- DSH 版本跟进：`external/dsh` 参考克隆同步 → 按 ARCHIVE-NOTES 锚点核验 → 白名单更新
- 发版流程：ProductVersion 提升（单源）→ CI 绿 → 双平台 zip + Setup.exe 三资产 Release
