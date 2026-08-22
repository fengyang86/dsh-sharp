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
  useStore, actions, useSessions, useWorkspaces, openWorkspace,
}) {
  const menu = useStore(state => state)
  const sessions = useSessions(state => state)
  const workspaces = useWorkspaces(state => state.items)
  const actionsRef = useRef(actions)
  actionsRef.current = actions

  useEffect(() => {
    const onContextMenu = (event: MouseEvent): void => {
      const target = event.target
      if (!(target instanceof Element)) return

      const workspaceRow = target.closest('[role="treeitem"][aria-expanded]')
      if (workspaceRow instanceof HTMLElement) {
        const text = workspaceRow.textContent?.trim() ?? ''
        const workspace = workspaces.find(item => text.startsWith(item.title))
        if (workspace === undefined) return
        event.preventDefault()
        actionsRef.current.openAt(event.clientX, event.clientY, {
          kind: 'workspace', path: workspace.path,
        })
        return
      }

      const sessionRow = target.closest('[role="treeitem"][aria-selected]')
      if (!(sessionRow instanceof HTMLElement)) return
      const selected = sessionRow.getAttribute('aria-selected') === 'true'
      const current = sessions.current
      const text = sessionRow.textContent?.trim() ?? ''
      const ids = sessions.ids ?? Object.keys(sessions.byId)
      const sessionId = selected && current !== undefined
        ? current
        : ids.find(id => {
            const session = sessions.byId[id]
            return session !== undefined && text.startsWith(session.title ?? '')
          })
      if (sessionId === undefined) return
      event.preventDefault()
      actionsRef.current.openAt(event.clientX, event.clientY, {
        kind: 'session', sessionId,
      })
    }

    document.addEventListener('contextmenu', onContextMenu, true)
    return () => { document.removeEventListener('contextmenu', onContextMenu, true) }
  }, [sessions, workspaces])

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
