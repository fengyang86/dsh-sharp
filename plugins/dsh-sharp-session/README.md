# dsh-sharp-session

DSH-Sharp 会话域插件。快捷键、会话完成通知和会话跳转统一运行在网页上下文，不依赖 DSH-Sharp、Avalonia、WebView2 或平台键盘消息。

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

DSH-Sharp 的官方包托管模式会自动把发布目录中的插件链接到 `web` 配置。源码模式、纯探测模式和远程服务由环境所有者显式安装，客户端不会修改外部 DSH 环境。

插件的 bundle 补丁只负责把自身加入 DSH Loader；浏览器端通过 DSH 公共 `sessions` 服务读取当前会话，并调用公开的 `session.cancel()`。插件卸载或热重载时会同步移除文档级键盘监听器。

工作区打开动作使用 DSH 官方 `workspaces.openPath(path)` 服务；其底层 `host.openPath` 会按平台调用 Windows `Invoke-Item`、macOS `open` 或 Linux `xdg-open`。当前 DSH 尚未提供工作区行级菜单贡献插槽，因此插件通过工作区行的语义 ARIA 属性定位，并在官方 `shell.overlay` 插槽中渲染官方 `Menu`。
