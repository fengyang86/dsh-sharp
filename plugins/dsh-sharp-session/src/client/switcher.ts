/**
 * Ctrl+K 会话快速切换器：模糊过滤当前列表中的会话，回车直达。
 * 纯 DOM 实现不依赖官方 UI 组件：数据取自 sessions 列表快照，
 * 打开复用官方 sessions.open（列表内会话必然满足其前置条件）。
 */

/** 切换器所需的最小会话服务接口（byId 条目额外消费可选的 title/updatedAt）。 */
export interface SwitcherSessions {
  open(id: string): void
  readonly list: {
    getSnapshot(): {
      readonly current: string | undefined
      readonly byId: Readonly<Record<string, SwitcherSessionEntry | undefined>>
    }
  }
}

export interface SwitcherSessionEntry {
  readonly running: boolean
  readonly title?: string
  readonly updatedAt?: number
}

/** 列表最多展示的条目数。 */
const MAX_ITEMS = 12

export function filterSessions(
  entries: Array<{ id: string; entry: SwitcherSessionEntry }>,
  query: string,
): Array<{ id: string; entry: SwitcherSessionEntry }> {
  const needle = query.trim().toLowerCase()
  const candidates = needle === ''
    ? entries
    : entries.filter(({ id, entry }) =>
        displayName(id, entry).toLowerCase().includes(needle) || id.toLowerCase().includes(needle))
  return candidates.slice().sort((left, right) => {
    const lu = left.entry.updatedAt ?? 0
    const ru = right.entry.updatedAt ?? 0
    if (lu !== ru) return ru - lu
    return left.id.localeCompare(right.id)
  }).slice(0, MAX_ITEMS)
}

function displayName(id: string, entry: SwitcherSessionEntry): string {
  const title = entry.title?.trim()
  return title !== undefined && title !== '' ? title : id.slice(0, 8)
}

export function installSessionSwitcher(
  sessions: SwitcherSessions,
  documentRoot: Document = document,
): () => void {
  let root: HTMLElement | undefined
  let input: HTMLInputElement | undefined
  let listEl: HTMLElement | undefined
  let selected = 0
  let visible: Array<{ id: string; entry: SwitcherSessionEntry }> = []

  const close = (): void => {
    if (root === undefined) return
    root.remove()
    root = input = listEl = undefined
  }

  const open = (): void => {
    if (root !== undefined) {
      input?.focus()
      return
    }

    root = documentRoot.createElement('div')
    Object.assign(root.style, {
      position: 'fixed', inset: '0', zIndex: '9999',
      background: 'rgba(15, 18, 25, 0.45)',
      display: 'flex', justifyContent: 'center', alignItems: 'flex-start',
      paddingTop: '14vh', fontFamily: 'system-ui, sans-serif',
    } satisfies Partial<CSSStyleDeclaration>)
    root.addEventListener('mousedown', (event) => {
      if (event.target === root) close()
    })

    const panel = documentRoot.createElement('div')
    Object.assign(panel.style, {
      width: 'min(560px, 92vw)', borderRadius: '12px', overflow: 'hidden',
      background: 'rgb(24, 27, 36)', color: 'rgb(232, 236, 245)',
      boxShadow: '0 18px 48px rgba(0, 0, 0, 0.45)',
    } satisfies Partial<CSSStyleDeclaration>)

    input = documentRoot.createElement('input')
    input.type = 'text'
    input.placeholder = '切换到会话…（↑↓ 选择，回车打开，Esc 关闭）'
    Object.assign(input.style, {
      width: '100%', boxSizing: 'border-box', padding: '14px 16px',
      border: 'none', outline: 'none', fontSize: '15px',
      background: 'transparent', color: 'inherit',
      borderBottom: '1px solid rgba(255, 255, 255, 0.12)',
    } satisfies Partial<CSSStyleDeclaration>)
    input.addEventListener('input', () => render(input!.value))
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        close()
      } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault()
        if (visible.length === 0) return
        selected = event.key === 'ArrowDown'
          ? (selected + 1) % visible.length
          : (selected - 1 + visible.length) % visible.length
        updateSelection()
      } else if (event.key === 'Enter') {
        event.preventDefault()
        const target = visible[selected]
        if (target === undefined) return
        close()
        try {
          sessions.open(target.id)
        } catch (error: unknown) {
          console.error('[dsh-sharp-session] 切换会话失败:', error)
        }
      }
    })

    listEl = documentRoot.createElement('div')
    Object.assign(listEl.style, { maxHeight: '46vh', overflowY: 'auto' } satisfies Partial<CSSStyleDeclaration>)

    panel.append(input, listEl)
    root.append(panel)
    documentRoot.body.append(root)
    render('')
    input.focus()
  }

  const updateSelection = (): void => {
    listEl?.querySelectorAll('[data-switcher-item]').forEach((node, index) => {
      const el = node as HTMLElement
      const active = index === selected
      el.style.background = active ? 'rgba(90, 130, 255, 0.25)' : 'transparent'
      if (active) el.scrollIntoView?.({ block: 'nearest' })
    })
  }

  const render = (query: string): void => {
    if (listEl === undefined) return
    const snapshot = sessions.list.getSnapshot()
    const entries = Object.entries(snapshot.byId)
      .filter((pair): pair is [string, SwitcherSessionEntry] => pair[1] !== undefined)
      .map(([id, entry]) => ({ id, entry }))
    visible = filterSessions(entries, query)
    selected = 0
    listEl.replaceChildren()

    if (visible.length === 0) {
      const empty = documentRoot.createElement('div')
      empty.textContent = query.trim() === '' ? '（会话列表为空）' : '（无匹配会话）'
      Object.assign(empty.style, { padding: '16px', fontSize: '13px', opacity: '0.6' } satisfies Partial<CSSStyleDeclaration>)
      listEl.append(empty)
      return
    }

    for (const { id, entry } of visible) {
      const row = documentRoot.createElement('div')
      row.dataset.switcherItem = ''
      Object.assign(row.style, {
        display: 'flex', alignItems: 'center', gap: '8px',
        padding: '10px 16px', fontSize: '14px', cursor: 'pointer',
      } satisfies Partial<CSSStyleDeclaration>)
      const label = documentRoot.createElement('span')
      label.textContent = (entry.running ? '● ' : '') + displayName(id, entry)
      label.style.flex = '1'
      label.style.overflow = 'hidden'
      label.style.textOverflow = 'ellipsis'
      label.style.whiteSpace = 'nowrap'
      row.append(label)
      row.addEventListener('click', () => {
        close()
        try {
          sessions.open(id)
        } catch (error: unknown) {
          console.error('[dsh-sharp-session] 切换会话失败:', error)
        }
      })
      listEl.append(row)
    }
    updateSelection()
  }

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.repeat || event.isComposing) return
    if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'k') return
    event.preventDefault()
    event.stopPropagation()
    if (root === undefined) open()
    else close()
  }

  documentRoot.addEventListener('keydown', onKeyDown, true)
  return () => {
    close()
    documentRoot.removeEventListener('keydown', onKeyDown, true)
  }
}
