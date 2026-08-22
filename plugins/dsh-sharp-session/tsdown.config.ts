import type { UserConfig } from 'tsdown'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'

const ROOT = fileURLToPath(new URL('.', import.meta.url))
const fromRoot = (path: string): string => resolve(ROOT, path)

const EXTERNALS = new Set([
  '@deepseek-ai/cordis',
  'react', 'react/jsx-runtime', 'react-dom', 'react-dom/client',
  '@deepseek-ai/dsh-client-runtime/client',
  '@deepseek-ai/dsh-client-ui-primitives',
])

const node: UserConfig = {
  name: '@yangfeng/dsh-sharp-session',
  entry: { index: fromRoot('src/index.ts') },
  outDir: fromRoot('lib'),
  format: ['esm'],
  platform: 'node',
  target: 'es2024',
  dts: false,
  clean: false,
  fixedExtension: false,
  deps: {
    neverBundle: () => true,
    alwaysBundle: () => false,
  },
}

const client: UserConfig = {
  name: '@yangfeng/dsh-sharp-session/client',
  entry: { client: fromRoot('src/client/index.ts') },
  outDir: fromRoot('lib'),
  format: 'cjs',
  platform: 'browser',
  target: 'es2022',
  dts: false,
  clean: false,
  fixedExtension: false,
  sourcemap: true,
  deps: {
    neverBundle: (specifier: string) => EXTERNALS.has(specifier),
    alwaysBundle: (specifier: string) => !EXTERNALS.has(specifier),
  },
  define: {
    'process.env.NODE_ENV': JSON.stringify('production'),
    'import.meta.env.MODE': JSON.stringify('production'),
    'import.meta.env': JSON.stringify({ MODE: 'production' }),
  },
  outputOptions: {
    entryFileNames: 'client.js',
    banner: 'window.__ModuleLoader__.load({ id: "@yangfeng/dsh-sharp-session", factory: (require) => {',
    footer: 'return module.exports; } });',
    intro: 'var module = { exports: {} }; var exports = module.exports;',
  },
}

export default [node, client]
