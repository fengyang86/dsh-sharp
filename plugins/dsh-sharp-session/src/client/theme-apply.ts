/**
 * 主题接收通道：宿主壳经 URL hash 下发主题意图（dshsharp-theme=light|dark|system），
 * 调用 web 官方唯一写入口 theme.setTheme 完成切换与持久化（settings scope 落盘）。
 *
 * theme 服务必须经运行时注入（ctx.inject）获取：Cordis 对未声明 inject 的服务属性
 * 直接读取会抛 "cannot get property ... without inject"（导致 loader entry 失败、
 * 整个插件无法加载）。注入回调在服务就绪时触发并立即应用当前 hash；旧版 runtime
 * 无该服务时回调不运行，通道静默降级为无操作，不影响插件其他能力。
 */
interface ReceiverContext {
  effect(callback: () => (() => void) | void, label?: string): unknown
  inject?(deps: readonly string[], callback: (scope: unknown) => void): unknown
}

export function installThemeReceiver(ctx: ReceiverContext): () => void {
  let theme: { setTheme?: (id: string) => void } | undefined

  const applyHash = (): void => {
    const hash = location.hash.startsWith('#') ? location.hash.slice(1) : location.hash
    const match = /(?:^|&)dshsharp-theme=(light|dark|system)(?:&|$)/.exec(hash)
    if (!match) return
    theme?.setTheme?.(match[1] ?? '')
  }

  const disposeInject = typeof ctx.inject === 'function'
    ? ctx.inject(['theme'], (scope) => {
        // 回调参数是注入作用域 Context，服务经其属性访问（与官方 ctx.inject 用法一致）。
        theme = (scope as { theme?: { setTheme?: (id: string) => void } }).theme
        applyHash()
      })
    : undefined

  window.addEventListener('hashchange', applyHash)
  applyHash()
  return () => {
    window.removeEventListener('hashchange', applyHash)
    if (typeof disposeInject === 'function') disposeInject()
  }
}
