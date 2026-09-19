// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { filterSessions, installSessionSwitcher, type SwitcherSessions } from '../src/client/switcher.ts'

function sessionsFixture(entries: Record<string, { running?: boolean; title?: string; updatedAt?: number }> = {}) {
  const open = vi.fn()
  const byId = Object.fromEntries(
    Object.entries(entries).map(([id, value]) => [id, { running: false, ...value }]),
  )
  const sessions: SwitcherSessions = {
    open,
    list: { getSnapshot: () => ({ current: undefined, byId }) },
  }
  return { sessions, open }
}

function pressCtrlK(): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true, cancelable: true })
  document.dispatchEvent(event)
  return event
}

function typeQuery(value: string): void {
  const input = document.querySelector('input')
  if (!(input instanceof HTMLInputElement)) throw new Error('切换器输入框不存在')
  input.value = value
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

function pressInputKey(key: string): void {
  const input = document.querySelector('input')
  if (!(input instanceof HTMLInputElement)) throw new Error('切换器输入框不存在')
  input.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
}

function renderedItems(): string[] {
  return [...document.querySelectorAll('[data-switcher-item] span')].map(el => el.textContent ?? '')
}

afterEach(() => {
  document.body.replaceChildren()
})

describe('过滤逻辑', () => {
  it('按标题与 ID 子串过滤，按 updatedAt 倒序，封顶 12 条', () => {
    const entries = [
      { id: 'aaa11111', entry: { running: false, title: '重构运行时', updatedAt: 10 } },
      { id: 'bbb22222', entry: { running: false, title: '修复登录', updatedAt: 30 } },
      { id: 'ccc33333', entry: { running: false, title: '重构协议', updatedAt: 20 } },
      { id: 'ddd44444', entry: { running: false, updatedAt: 99 } },
    ]
    const filtered = filterSessions(entries, '重构')
    expect(filtered.map(f => f.entry.title)).toEqual(['重构协议', '重构运行时'])
    expect(filterSessions(entries, 'bbb2')[0]?.id).toBe('bbb22222')
    expect(filterSessions(entries, '').map(f => f.entry.updatedAt)).toEqual([99, 30, 20, 10])
  })

  it('无标题会话回退到 ID 前缀', () => {
    const entries = [{ id: 'abcdefgh', entry: { running: false } }]
    expect(filterSessions(entries, 'abcd')[0]?.id).toBe('abcdefgh')
  })
})

describe('Ctrl+K 切换器', () => {
  it('Ctrl+K 打开，Esc 关闭，再次 Ctrl+K 重新打开', () => {
    const { sessions } = sessionsFixture({ s1: { title: '会话一' } })
    const dispose = installSessionSwitcher(sessions)

    const event = pressCtrlK()
    expect(event.defaultPrevented).toBe(true)
    expect(document.querySelector('input')).not.toBeNull()
    expect(renderedItems()).toEqual(['会话一'])

    pressInputKey('Escape')
    expect(document.querySelector('input')).toBeNull()

    pressCtrlK()
    expect(document.querySelector('input')).not.toBeNull()
    dispose()
  })

  it('输入过滤，回车打开选中会话', () => {
    const { sessions, open } = sessionsFixture({
      s1: { title: '修复登录', updatedAt: 1 },
      s2: { title: '重构运行时', updatedAt: 2 },
      s3: { title: '重构协议', updatedAt: 3 },
    })
    const dispose = installSessionSwitcher(sessions)

    pressCtrlK()
    typeQuery('重构')
    expect(renderedItems()).toEqual(['重构协议', '重构运行时'])

    pressInputKey('ArrowDown')
    pressInputKey('Enter')
    expect(open).toHaveBeenCalledWith('s2')
    expect(document.querySelector('input')).toBeNull()
    dispose()
  })

  it('点击条目打开对应会话', () => {
    const { sessions, open } = sessionsFixture({ s1: { title: '会话一' } })
    const dispose = installSessionSwitcher(sessions)

    pressCtrlK()
    const row = document.querySelector('[data-switcher-item]')
    ;(row as HTMLElement).click()
    expect(open).toHaveBeenCalledWith('s1')
    expect(document.querySelector('input')).toBeNull()
    dispose()
  })

  it('卸载后 Ctrl+K 不再响应', () => {
    const { sessions } = sessionsFixture({ s1: { title: '会话一' } })
    const dispose = installSessionSwitcher(sessions)
    dispose()

    pressCtrlK()
    expect(document.querySelector('input')).toBeNull()
  })

  it('普通按键与无修饰 K 不触发', () => {
    const { sessions } = sessionsFixture()
    const dispose = installSessionSwitcher(sessions)

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', bubbles: true, cancelable: true }))
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true, cancelable: true }))
    expect(document.querySelector('input')).toBeNull()
    dispose()
  })
})
