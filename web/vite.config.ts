import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

/**
 * 开发期代理目标：后端 Kestrel（见 docs/plans/01 §3）。
 * 生产期不经过 Vite —— 前端产物被拷入 src/FileBackupTool.Api/wwwroot/，由后端同源带起。
 */
const BACKEND = 'http://localhost:5000'

export default defineConfig({
  // 应用挂在哪个路径下。默认根路径；要挂子路径（如 /backup/）时由
  // publish.ps1 -PathBase 注入环境变量 VITE_BASE。
  //
  // 这里是**唯一**读 VITE_BASE 的地方。`base` 派生出的内置 BASE_URL 被 router、
  // API 路径、Hub 路径共用（见 src/router/index.ts、src/api/http.ts、
  // src/realtime/client.ts），所以"页面 base"与"接口/路由 base"不会各自漂移——
  // 早先让三处各自读一次 VITE_BASE，存在错配成白屏的可能。
  //
  // 关于 .env 文件与 process.env 的优先级（实测本仓 vite 8.3.0 的 loadEnv 源码，
  // 它最后一步是无条件 `env[key] = process.env[key]`）：**process.env 胜出**，
  // .env 文件次之。所以 .env.production 里写 VITE_BASE 是**无效**的（会被这里
  // 读到的值忽略）——注意这与"静默生效"相反，方向是安全的，但确实是个会让人
  // 白写的坑，故记在此处。
  base: process.env.VITE_BASE || '/',
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
      // 注意：下面两个键是**写死的根路径**，只在 `npm run dev`（本地开发）生效。
      // 生产不走 Vite —— 前端产物由后端同源带起，所以这两行与 base 无关。
      //
      // 但留个话在这里：如果有人想在本地给 VITE_BASE 赋值跑 dev（模拟挂子路径），
      // 请求会打到 /backup/api、/backup/hubs，这两个键就匹配不上了 → dev 下 404。
      // 那种情况下要把键一并改成 `${process.env.VITE_BASE ?? '/'}api` 之类。
      // 正常开发（不设 VITE_BASE，base='/')不受影响。
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
