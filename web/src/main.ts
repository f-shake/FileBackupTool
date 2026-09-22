import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'

import 'element-plus/dist/index.css'
// Element Plus 暗色主题变量（跟随 <html class="dark">，见 stores/theme.ts）
import 'element-plus/theme-chalk/dark/css-vars.css'
import '@/styles/index.css'

import App from './App.vue'
import router from './router'

async function bootstrap(): Promise<void> {
  // Mock 层：默认关闭。仅在显式设置 VITE_USE_MOCK=true 时装配。
  if (import.meta.env.VITE_USE_MOCK === 'true') {
    const [{ http }, { createMockAdapter }] = await Promise.all([
      import('@/api/http'),
      import('@/api/mock'),
    ])
    http.defaults.adapter = createMockAdapter()
    console.info(
      '[FileBackupTool] Mock 层已启用（VITE_USE_MOCK=true）。所有数据都是假的，不要据此判断后端行为。',
    )
  }

  const app = createApp(App)
  app.use(createPinia())
  app.use(router)
  app.use(ElementPlus, { locale: zhCn })
  app.mount('#app')
}

void bootstrap()
