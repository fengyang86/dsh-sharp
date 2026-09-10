import { describe, expect, it, vi } from 'vitest'
import { openWorkspacePath, type ShortcutConnection } from '../src/client/workspace-open.ts'

function connectionFixture(
  result: { ok: true; value: unknown } | { ok: false; error: { code: string; message: string } } = {
    ok: true,
    value: { opened: true },
  },
) {
  const call = vi.fn(async () => result)
  const connection: ShortcutConnection = { rpc: { call } }
  return { connection, call }
}

describe('工作区在文件管理器中打开', () => {
  it('通过官方 Connection RPC 调用 session/openWorkspacePath', async () => {
    const { connection, call } = connectionFixture()

    await openWorkspacePath(connection, 'D:\\DEV\\tool\\RenderSharp')

    expect(call).toHaveBeenCalledExactlyOnceWith('/api', 'session/openWorkspacePath', {
      args: { request: { path: 'D:\\DEV\\tool\\RenderSharp' } },
    })
  })

  it('不传 action（打开目录语义，而非 reveal 定位）', async () => {
    const { connection, call } = connectionFixture()

    await openWorkspacePath(connection, '/home/user/project')

    const payload = call.mock.calls[0][2] as { args: { request: Record<string, unknown> } }
    expect(payload.args.request).not.toHaveProperty('action')
    expect(Object.keys(payload.args.request)).toEqual(['path'])
  })

  it('Host 返回失败时抛出错误信息', async () => {
    const { connection } = connectionFixture({
      ok: false,
      error: { code: 'gateway/internal', message: 'path open failed: no file manager' },
    })

    await expect(openWorkspacePath(connection, '/x')).rejects.toThrow('path open failed: no file manager')
  })

  it('失败无消息时回退到错误码', async () => {
    const { connection } = connectionFixture({ ok: false, error: { code: 'gateway/cancelled', message: '' } })

    await expect(openWorkspacePath(connection, '/x')).rejects.toThrow('gateway/cancelled')
  })

  it('传输层异常原样上抛（供调用方提示）', async () => {
    const call = vi.fn(async () => { throw new Error('transport failure') })
    const connection: ShortcutConnection = { rpc: { call } }

    await expect(openWorkspacePath(connection, '/x')).rejects.toThrow('transport failure')
  })
})
