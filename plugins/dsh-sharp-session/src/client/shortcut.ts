/** 插件所需的最小会话服务接口，保持与 DSH 公共 sessions 外观一致。 */
export interface ShortcutSessions {
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
