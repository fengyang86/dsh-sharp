# DSH-Sharp 路线图

> 已实现功能见 README。本文件记录已确认但尚未实施的需求与规划，按阶段排序。

## 阶段 2：多标签多连接（已确认）

一个客户端同时承载多个 DSH 服务连接（标签页），如：源码实例（3080）+ npx 官方包实例 + 远程实例并存。

- 连接模型数组化：每个标签一个 `DshConnection`（WebView + 事件监控 + 服务托管 + 会话列表）
- TabControl 标签管理：新建 / 关闭 / 切换
- **标签休眠**：切换时冻结非活跃标签的 WebView，激活才加载（多 WebView2 内存开销大，必须做）
- 通知归属：会话完成通知标注来源服务/标签
- 设置页按连接分组展示（地址 / 托管模式 / 启动状态 / 操作）

## 阶段 3：标签拖出独立窗口（已确认）

- 标签可从主窗口拖出为独立窗口（浏览器式），仍为同一进程
- 技术基础已验证：`Avalonia.Controls.WebView` 提供 `BeginReparentingAsync`（WebView2 跨窗口 re-parent 官方支持）
- 独立窗口生命周期管理、拖回合并、窗口状态记忆

## 待评估（未确认）

- 会话导出（`session.export` RPC → 本地 jsonl）
- Windows 系统通知（通知中心/锁屏，接 WinRT AppNotifications）
- 全局热键（任意程序呼出/隐藏）
- `dsh://` URL 协议唤起
- 客户端自动更新（GitHub Releases）
- WebView 前进/后退/刷新按钮（标题栏）
- 快捷键（Ctrl+R 刷新等）
- **npm 镜像源配置**：npx 慢下载环境可配置 npmmirror 镜像（注入 npm_config_registry）
- **npx 包预下载预热**：服务在线空闲时后台拉取 npx 包，切到 npx 模式时秒级启动
