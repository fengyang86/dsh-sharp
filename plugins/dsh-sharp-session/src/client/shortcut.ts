/** 插件所需的最小会话服务接口，保持与 DSH 公共 sessions 外观一致。 */
export interface ShortcutSessions {
  open(id: string): void
  readonly list: {
    getSnapshot(): {
      readonly current: string | undefined
      readonly byId: Readonly<Record<string, { readonly running: boolean } | undefined>>
    }
  }
  binding(id: string): {
    readonly session: {
      cancel(): Promise<{ readonly ok: boolean; readonly error?: { readonly message?: string } }>
    }
  } | undefined
}

export function readFeatureFlags(locationLike: Location = window.location) {
  const query = new URLSearchParams(locationLike.search)
  const hash = new URLSearchParams(locationLike.hash.replace(/^#/, ''))
  const enabled = (name: string): boolean =>
    (hash.get(`dshsharp-${name}`) ?? query.get(`dshsharp-${name}`)) !== '0'
  return {
    escStop: enabled('esc-stop'),
    copyId: enabled('copy-id'),
    openWorkspace: enabled('open-workspace'),
    trayNavigation: enabled('tray-navigation'),
  }
}

/** 托盘会话跳转：等待 DSH 会话列表就绪的重试节奏（整页重载后列表数据晚于插件加载）。 */
const NAVIGATION_RETRY_INTERVAL_MS = 150
const NAVIGATION_RETRY_LIMIT = 67 // ≈10s

/**
 * 处理桌面托盘传入的会话地址，并交给 DSH 官方 sessions.open。
 * sessions.open 要求目标会话已存在于客户端列表（否则抛错），而整页重载后
 * 插件加载早于列表 baseline；因此在重试窗口内等待目标出现后再打开。
 */
export function installSessionNavigation(sessions: ShortcutSessions): () => void {
  let generation = 0
  let timer: ReturnType<typeof setTimeout> | undefined

  const waitForSessionAndOpen = (id: string): void => {
    const current = ++generation
    let attempts = 0
    const attempt = (): void => {
      if (current !== generation) return
      if (sessions.list.getSnapshot().byId[id] !== undefined) {
        try {
          sessions.open(id)
          history.replaceState(null, '', window.location.pathname)
        } catch (error: unknown) {
          console.error('[dsh-sharp-session] 打开托盘会话失败:', error)
        }
        return
      }
      if (++attempts > NAVIGATION_RETRY_LIMIT) {
        console.error('[dsh-sharp-session] 打开托盘会话失败: 会话列表就绪超时', id)
        return
      }
      timer = setTimeout(attempt, NAVIGATION_RETRY_INTERVAL_MS)
    }
    attempt()
  }

  const openFromHash = (): void => {
    const match = window.location.hash.match(/(?:^#|&)dsh-session=([^&]+)/)
      ?? window.location.search.match(/[?&]dsh-session=([^&]+)/)
    if (match === null) return
    waitForSessionAndOpen(decodeURIComponent(match[1]))
  }
  window.addEventListener('hashchange', openFromHash)
  openFromHash()
  return () => {
    generation++
    if (timer !== undefined) clearTimeout(timer)
    window.removeEventListener('hashchange', openFromHash)
  }
}

const TRANSIENT_LAYER_SELECTOR = [
  '[aria-modal="true"]',
  '[role="dialog"]',
  '[role="menu"]',
  '[role="listbox"]',
].join(',')

/**
 * 安装 Esc 停止当前会话的网页快捷键。
 * 临时界面拥有 Esc 的优先权；插件只处理未被其他界面消费的按键。
 */
export function installSessionShortcuts(
  sessions: ShortcutSessions,
  documentRoot: Document = document,
): () => void {
  let cancelPending = false

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key !== 'Escape' || event.repeat || event.isComposing || cancelPending) return

    // 必须在其他监听器关闭弹窗前记录该状态，否则微任务阶段已无法辨别 Esc 的原始用途。
    const transientLayerWasOpen = documentRoot.querySelector(TRANSIENT_LAYER_SELECTOR) !== null

    queueMicrotask(() => {
      if (event.defaultPrevented || transientLayerWasOpen || cancelPending) return

      const snapshot = sessions.list.getSnapshot()
      const sessionId = snapshot.current
      if (sessionId === undefined || snapshot.byId[sessionId]?.running !== true) return

      const session = sessions.binding(sessionId)?.session
      if (session === undefined) return

      cancelPending = true
      void session.cancel()
        .then((result) => {
          if (!result.ok) {
            console.error('[dsh-sharp-session] 停止会话失败:', result.error?.message ?? '未知错误')
          }
        })
        .catch((error: unknown) => {
          console.error('[dsh-sharp-session] 停止会话失败:', error)
        })
        .finally(() => { cancelPending = false })
    })
  }

  documentRoot.addEventListener('keydown', onKeyDown)
  return () => { documentRoot.removeEventListener('keydown', onKeyDown) }
}
