// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { installSessionShortcuts, type ShortcutSessions } from '../src/client/shortcut.ts'

function sessionsFixture(options: { current?: string; running?: boolean } = {}) {
  const cancel = vi.fn(async () => ({ ok: true as const }))
  const current = options.current ?? 'current-session'
  const sessions: ShortcutSessions = {
    list: {
      getSnapshot: () => ({
        current,
        byId: { [current]: { running: options.running ?? true } },
      }),
    },
    binding: id => id === current ? { session: { cancel } } : undefined,
  }
  return { sessions, cancel }
}

async function pressEscape(init: KeyboardEventInit = {}): Promise<KeyboardEvent> {
  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, ...init })
  document.dispatchEvent(event)
  await new Promise<void>(resolve => queueMicrotask(resolve))
  return event
}

afterEach(() => {
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

describe('Esc 会话快捷键', () => {
  it('停止当前运行会话', async () => {
    const { sessions, cancel } = sessionsFixture()
    const dispose = installSessionShortcuts(sessions)

    await pressEscape()

    expect(cancel).toHaveBeenCalledOnce()
    dispose()
  })

  it('当前会话空闲时不调用停止', async () => {
    const { sessions, cancel } = sessionsFixture({ running: false })
    const dispose = installSessionShortcuts(sessions)

    await pressEscape()

    expect(cancel).not.toHaveBeenCalled()
    dispose()
  })

  it.each(['dialog', 'menu', 'listbox'])('优先交给临时 %s 界面', async (role) => {
    const { sessions, cancel } = sessionsFixture()
    const layer = document.createElement('div')
    layer.setAttribute('role', role)
    document.body.append(layer)
    const dispose = installSessionShortcuts(sessions)
    document.addEventListener('keydown', () => { layer.remove() }, { once: true })

    await pressEscape()

    expect(cancel).not.toHaveBeenCalled()
    dispose()
  })

  it('其他监听器阻止默认行为时不调用停止', async () => {
    const { sessions, cancel } = sessionsFixture()
    const dispose = installSessionShortcuts(sessions)
    document.addEventListener('keydown', event => event.preventDefault(), { once: true })

    await pressEscape({ cancelable: true })

    expect(cancel).not.toHaveBeenCalled()
    dispose()
  })

  it('忽略长按重复事件和输入法组合事件', async () => {
    const { sessions, cancel } = sessionsFixture()
    const dispose = installSessionShortcuts(sessions)

    await pressEscape({ repeat: true })
    await pressEscape({ isComposing: true })

    expect(cancel).not.toHaveBeenCalled()
    dispose()
  })

  it('卸载插件后移除监听器', async () => {
    const { sessions, cancel } = sessionsFixture()
    const dispose = installSessionShortcuts(sessions)
    dispose()

    await pressEscape()

    expect(cancel).not.toHaveBeenCalled()
  })
})
