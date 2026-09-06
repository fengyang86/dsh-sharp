// @ts-nocheck
import { useEffect, useRef } from 'react'
import { IconFolderOpen16, Menu } from '@deepseek-ai/dsh-client-ui-primitives'

const COPY_SESSION_ITEM = { id: 'copy-session-id', label: '复制会话 ID' }
const OPEN_WORKSPACE_ITEM = {
  id: 'open-workspace',
  label: '在资源管理器中打开',
  icon: <IconFolderOpen16 />,
}

/**
 * 复用 DSH 官方 Menu、sessions 和 workspaces 服务提供会话域右键动作。
 * 当前 DSH 没有行级菜单贡献插槽，因此只通过官方行的语义 ARIA 属性定位。
 */
export function ContextMenuView({
  useStore, actions, openWorkspace, getSessionSnapshot, getWorkspaceItems, features,
}) {
  const menu = useStore(state => state)
  const actionsRef = useRef(actions)
  actionsRef.current = actions

  useEffect(() => {
    const onContextMenu = (event: MouseEvent): void => {
      const target = event.target
      if (!(target instanceof Element)) return

      const workspaceRow = target.closest('[role="treeitem"][aria-expanded]')
      if (workspaceRow instanceof HTMLElement && features.openWorkspace) {
        // DSH 当前没有向插件暴露工作区行 ID。仅在目录名唯一匹配时启用，
        // 同名目录宁可不展示菜单，也不能打开错误的工作区。
        const label = workspaceRow.textContent?.trim() ?? ''
        const matches = getWorkspaceItems().filter(item => workspaceLabel(item.path) === label)
        if (matches.length !== 1) return
        event.preventDefault()
        actionsRef.current.openAt(event.clientX, event.clientY, {
          kind: 'workspace', path: matches[0].path,
        })
        return
      }

      const sessionRow = target.closest('[role="treeitem"][aria-selected]')
      if (!(sessionRow instanceof HTMLElement) || !features.copyId) return
      // 官方行没有将 sessionId 写入 DOM。右键未选中行时先复用它的官方点击，
      // 再从 sessions 服务读取刚刚选中的准确 ID，避免按标题猜测同名会话。
      if (sessionRow.getAttribute('aria-selected') !== 'true') sessionRow.click()
      const sessionId = getSessionSnapshot().current
      if (sessionId === undefined) return
      event.preventDefault()
      actionsRef.current.openAt(event.clientX, event.clientY, {
        kind: 'session', sessionId,
      })
    }

    document.addEventListener('contextmenu', onContextMenu, true)
    return () => { document.removeEventListener('contextmenu', onContextMenu, true) }
  }, [getSessionSnapshot, getWorkspaceItems])

  if (!menu.open || menu.target === null) return null
  const item = menu.target.kind === 'workspace' ? OPEN_WORKSPACE_ITEM : COPY_SESSION_ITEM
  return (
    <Menu
      open
      portal
      side="bottom"
      getAnchorRect={() => new DOMRect(menu.x, menu.y, 0, 0)}
      items={[item]}
      onSelect={() => {
        const target = menu.target
        actions.close()
        if (target.kind === 'workspace') {
          void openWorkspace(target.path).catch((error: unknown) => {
            alert('打开工作区失败: ' + String(error))
          })
        } else {
          void navigator.clipboard.writeText(target.sessionId).catch((error: unknown) => {
            alert('复制会话 ID 失败: ' + String(error))
          })
        }
      }}
      onClose={() => { actions.close() }}
      anchor={<span aria-hidden="true" />}
    />
  )
}

function workspaceLabel(path: string): string {
  const normalized = path.replace(/[\\/]+$/, '')
  const segments = normalized.split(/[\\/]/)
  return segments.at(-1) ?? normalized
}
