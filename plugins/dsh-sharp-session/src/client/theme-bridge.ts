/**
 * 主题桥：把 DSH WebUI 的主题选择镜像给桌面壳。
 * web 端在 <html data-ds-theme-source> 上发布 light/dark/system，
 * 该属性本就是为宿主壳镜像设计的官方信号（官方 Electron 同样消费它）。
 * 经 WebView2 的 chrome.webview.postMessage 送达壳的 WebMessageReceived；
 * 普通浏览器中该通道不存在，静默降级为无操作。
 */
export const THEME_SOURCE_ATTRIBUTE = 'data-ds-theme-source'

export function installThemeBridge(documentRoot: Document = document): () => void {
  const post = (source: string): void => {
    const webview = (window as { chrome?: { webview?: { postMessage?: (data: string) => void } } }).chrome?.webview
    webview?.postMessage?.(JSON.stringify({ type: 'dshsharp-theme', source }))
  }

  const readSource = (): string =>
    documentRoot.documentElement.getAttribute(THEME_SOURCE_ATTRIBUTE) ?? 'system'

  const observer = new MutationObserver(() => post(readSource()))
  observer.observe(documentRoot.documentElement, {
    attributes: true,
    attributeFilter: [THEME_SOURCE_ATTRIBUTE],
  })
  post(readSource())
  return () => observer.disconnect()
}
