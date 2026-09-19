// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { installThemeBridge, THEME_SOURCE_ATTRIBUTE } from '../src/client/theme-bridge.ts'

function installWebviewPost(): { posted: string[] } {
  const posted: string[] = []
  ;(window as any).chrome = { webview: { postMessage: (data: string) => posted.push(data) } }
  return { posted }
}

afterEach(() => {
  delete (window as any).chrome
  document.documentElement.removeAttribute(THEME_SOURCE_ATTRIBUTE)
})

describe('主题桥', () => {
  it('安装时上报当前主题源，属性变化时上报新值', async () => {
    const { posted } = installWebviewPost()
    document.documentElement.setAttribute(THEME_SOURCE_ATTRIBUTE, 'dark')

    const dispose = installThemeBridge()
    expect(posted).toEqual([JSON.stringify({ type: 'dshsharp-theme', source: 'dark' })])

    document.documentElement.setAttribute(THEME_SOURCE_ATTRIBUTE, 'light')
    await new Promise<void>(resolve => queueMicrotask(resolve))
    expect(posted).toHaveLength(2)
    expect(JSON.parse(posted[1]!)).toEqual({ type: 'dshsharp-theme', source: 'light' })
    dispose()
  })

  it('未设置属性时按 system 上报', () => {
    const { posted } = installWebviewPost()
    const dispose = installThemeBridge()
    expect(JSON.parse(posted[0]!)).toEqual({ type: 'dshsharp-theme', source: 'system' })
    dispose()
  })

  it('普通浏览器无 chrome.webview 时静默无操作', () => {
    document.documentElement.setAttribute(THEME_SOURCE_ATTRIBUTE, 'dark')
    expect(() => {
      const dispose = installThemeBridge()
      document.documentElement.setAttribute(THEME_SOURCE_ATTRIBUTE, 'light')
      dispose()
    }).not.toThrow()
  })

  it('卸载后属性变化不再上报', () => {
    const { posted } = installWebviewPost()
    const dispose = installThemeBridge()
    dispose()
    document.documentElement.setAttribute(THEME_SOURCE_ATTRIBUTE, 'dark')
    expect(posted).toHaveLength(1)
  })
})
