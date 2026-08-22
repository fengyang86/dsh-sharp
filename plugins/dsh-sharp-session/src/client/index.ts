import { installSessionShortcuts, type ShortcutSessions } from './shortcut.ts'
import { ContextMenuView } from './ContextMenuView.tsx'
import { createMenuStore } from './menu-store.ts'

/** DSH 插件上下文中本插件实际使用的公开成员。 */
interface ShortcutContext {
  readonly sessions: ShortcutSessions
  readonly slots: any
  readonly workspaces: any
  effect(callback: () => () => void, label: string): unknown
}

/** 所需服务：DSH 浏览器运行时的会话服务。 */
export const inject = ['slots', 'sessions', 'workspaces']

/**
 * 注册浏览器端快捷键，并让监听器跟随插件生命周期卸载。
 * @param ctx DSH 浏览器客户端上下文。
 */
export function apply(ctx: ShortcutContext): void {
  ctx.effect(
    () => installSessionShortcuts(ctx.sessions),
    'dsh-sharp-session: document keyboard listener',
  )
  ctx.slots.inject('shell.overlay', () => ctx.slots.register(
    {
      name: 'shell.overlay',
      id: 'dsh-sharp-session.context-menu',
      order: 100,
      store: createMenuStore(),
      inject: () => ({
        openWorkspace: (path: string) => ctx.workspaces.openPath(path),
      }),
    },
    ContextMenuView,
  ))
}
