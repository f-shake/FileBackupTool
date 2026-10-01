import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'

/**
 * 前端单元测试配置（`npm run test`，publish.ps1 会在构建前跑它）。
 *
 * 刻意与 vite.config.ts 分开：
 *   · 不加载 Vue 插件、不读构建期的 VITE_BASE —— 这里只跑纯逻辑；
 *   · 需要组件测试时再引入 @vue/test-utils 与 happy-dom，不要顺手把构建配置拖进来。
 */
export default defineConfig({
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.spec.ts'],
  },
})
