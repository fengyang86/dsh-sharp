// @ts-nocheck
import { defineStore } from '@deepseek-ai/dsh-client-runtime/client'

export function createMenuStore() {
  return defineStore({
    init: () => ({ open: false, x: 0, y: 0, target: null }),
    actions: {
      openAt: (draft, x, y, target) => {
        draft.open = true
        draft.x = x
        draft.y = y
        draft.target = target
      },
      close: (draft) => {
        draft.open = false
        draft.target = null
      },
    },
  })
}
