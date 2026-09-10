/** 插件所需的最小官方 Connection 服务接口（DSH 0.1.5+ 客户端 RPC carrier）。 */
export interface ShortcutConnection {
  readonly rpc: {
    call(
      channel: string,
      endpoint: string,
      payload: unknown,
      signal?: AbortSignal,
    ): Promise<
      | { readonly ok: true; readonly value: unknown }
      | { readonly ok: false; readonly error: { readonly code: string; readonly message: string } }
    >
  }
}

/**
 * 在系统文件管理器中打开一个工作区目录。
 *
 * DSH 没有客户端 `workspaces.openPath` 服务（该方法只存在于 Host 内部）；
 * 官方浏览器侧入口是 Remote RPC `session/openWorkspacePath`，经 Connection
 * carrier 调用，底层由 Host 按平台适配（Windows 资源管理器 / macOS Finder /
 * Linux 文件管理器）。省略 action 即“打开目录”语义（`reveal` 仅定位）。
 */
export async function openWorkspacePath(connection: ShortcutConnection, path: string): Promise<void> {
  const result = await connection.rpc.call('/api', 'session/openWorkspacePath', {
    args: { request: { path } },
  })
  if (!result.ok) {
    throw new Error(result.error.message || result.error.code)
  }
}
