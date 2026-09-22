import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

/**
 * 开发期代理目标：后端 Kestrel（见 docs/plans/01 §3）。
 * 生产期不经过 Vite —— 前端产物被拷入 src/FileBackupTool.Api/wwwroot/，由后端同源带起。
 */
const BACKEND = 'http://localhost:5000'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    strictPort: false,
    proxy: {
      // REST
      '/api': {
        target: BACKEND,
        changeOrigin: true,
      },
      // SignalR：必须开 WebSocket 升级
      '/hubs': {
        target: BACKEND,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    // 产物输出到 web/dist（保持默认），后端会来取，前端不主动拷贝
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        // Vite 8 / rolldown 只接受函数形式的 manualChunks（对象形式会报
        // "Expected Function but received Object"），这里按模块来源分包。
        manualChunks(id: string) {
          if (!id.includes('node_modules')) return undefined
          if (id.includes('@microsoft/signalr')) return 'signalr'
          if (id.includes('element-plus') || id.includes('@element-plus/icons-vue')) {
            return 'element'
          }
          if (id.includes('/vue/') || id.includes('vue-router') || id.includes('/pinia/')) {
            return 'vue'
          }
          return undefined
        },
      },
    },
  },
})
