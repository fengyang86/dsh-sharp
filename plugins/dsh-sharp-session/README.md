# dsh-sharp-session

DSH-Sharp 会话域插件。快捷键和会话/工作区右键动作运行在网页上下文，不依赖 Avalonia、WebView2 或平台键盘消息。

## 功能开关

DSH-Sharp 设置页可分别控制以下能力：Esc 停止当前会话、会话右键复制会话 ID、工作区右键打开资源管理器、托盘最近会话跳转。开关由客户端持久化，重载 Web 页面后生效；未设置时默认全部启用。

会话完成通知、提示音和桌面窗口唤起属于 DSH-Sharp 客户端，不属于本插件。

## 当前快捷键

| 快捷键 | 行为 |
| --- | --- |
| `Esc` | 停止当前正在运行的会话 |
| 会话条目右键 | 复制会话 ID |
| 工作区条目右键 | 调用 DSH 官方 `workspaces.openPath(path)` 在系统文件管理器中打开 |

对话框、菜单和列表框优先处理 `Esc`；按键已被其他界面阻止、当前没有会话或当前会话空闲时，插件不会发出停止请求。

## 本地构建与安装

```powershell
pnpm build
pnpm dsh plugin --profile web add link:<dsh-sharp>/plugins/dsh-sharp-session
```

DSH-Sharp 会自动把发布目录中的插件链接到其私有 Runtime 的 `web` 配置；客户端不会修改外部 DSH 环境。

插件的 bundle 补丁只负责把自身加入 DSH Loader；浏览器端通过 DSH 公共 `sessions` 服务读取当前会话，并调用公开的 `session.cancel()`。插件卸载或热重载时会同步移除文档级键盘监听器。

工作区打开动作经官方 Connection RPC 调用 Host 的 `session/openWorkspacePath`：DSH 没有客户端 `workspaces.openPath` 服务（该能力只存在于 Host 内部），浏览器侧必须走 `ctx.connection.rpc.call('/api', 'session/openWorkspacePath', { args: { request: { path } } })`，底层由 `host` 按平台调用 Windows 资源管理器、macOS Finder 或 Linux 文件管理器。当前 DSH 尚未提供工作区行级菜单贡献插槽或稳定的行标识，插件只会在目录名唯一时显示工作区右键菜单；重名目录不显示该动作，避免误打开错误路径。会话右键未选中行会先调用官方行的选中动作，再从 `sessions` 服务读取精确会话 ID，不按标题猜测。

插件注入 `slots`、`sessions`、`workspaces` 与 `connection` 四个客户端服务；DSH 0.1.5 移除 `ctx.agent` 与 Inbox 类导出，本插件不依赖它们。
