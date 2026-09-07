// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { installSessionNavigation, installSessionShortcuts, type ShortcutSessions } from '../src/client/shortcut.ts'

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

describe('托盘会话跳转', () => {
  function navigationFixture(byId: Record<string, { readonly running: boolean }> = {}) {
    const open = vi.fn()
    let snapshot = { current: undefined as string | undefined, byId }
    const sessions = {
      open,
      list: { getSnapshot: () => snapshot },
    } as unknown as ShortcutSessions
    return {
      sessions,
      open,
      publish: (next: Record<string, { readonly running: boolean }>) => { snapshot = { current: undefined, byId: next } },
    }
  }

  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    history.replaceState(null, '', window.location.pathname)
  })

  it('会话已在列表中时立即打开并清除 hash', () => {
    const { sessions, open } = navigationFixture({ 'session-abc': { running: false } })
    window.location.hash = '#dsh-session=session-abc'

    const dispose = installSessionNavigation(sessions)

    expect(open).toHaveBeenCalledExactlyOnceWith('session-abc')
    expect(window.location.hash).toBe('')
    dispose()
  })

  it('hash 同时携带功能开关参数时不影响会话 ID 提取', () => {
    const { sessions, open } = navigationFixture({ 'session-def': { running: false } })
    window.location.hash = '#dsh-session=session-def&dshsharp-esc-stop=0&dshsharp-copy-id=1'

    const dispose = installSessionNavigation(sessions)

    expect(open).toHaveBeenCalledExactlyOnceWith('session-def')
    dispose()
  })

  it('整页重载后列表晚于插件加载：等待目标会话出现再打开', () => {
    const { sessions, open, publish } = navigationFixture()
    window.location.hash = '#dsh-session=session-late'

    const dispose = installSessionNavigation(sessions)
    expect(open).not.toHaveBeenCalled()

    // 列表 baseline 到达（若干次重试之后）。
    publish({ 'session-late': { running: false } })
    vi.advanceTimersByTime(1000)

    expect(open).toHaveBeenCalledExactlyOnceWith('session-late')
    dispose()
  })

  it('会话始终未出现时在重试窗口结束后放弃', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {})
    const { sessions, open } = navigationFixture()
    window.location.hash = '#dsh-session=session-never'

    const dispose = installSessionNavigation(sessions)
    vi.advanceTimersByTime(150 * 70)

    expect(open).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalled()
    dispose()
  })

  it('hash 变化时响应跳转（托盘再次点击）', () => {
    const { sessions, open } = navigationFixture({ 'session-ghi': { running: true } })
    const dispose = installSessionNavigation(sessions)
    expect(open).not.toHaveBeenCalled()

    window.location.hash = '#dsh-session=session-ghi'
    vi.advanceTimersByTime(0)

    expect(open).toHaveBeenCalledExactlyOnceWith('session-ghi')
    dispose()
  })

  it('无 dsh-session 时不调用 open', () => {
    const { sessions, open } = navigationFixture({ 'session-x': { running: false } })
    window.location.hash = '#dshsharp-esc-stop=1'

    const dispose = installSessionNavigation(sessions)
    vi.advanceTimersByTime(2000)

    expect(open).not.toHaveBeenCalled()
    dispose()
  })

  it('卸载后停止等待且 hash 变化不再响应', () => {
    const { sessions, open, publish } = navigationFixture()
    window.location.hash = '#dsh-session=session-jkl'

    const dispose = installSessionNavigation(sessions)
    dispose()
    publish({ 'session-jkl': { running: false } })
    vi.advanceTimersByTime(2000)

    expect(open).not.toHaveBeenCalled()
  })
})
