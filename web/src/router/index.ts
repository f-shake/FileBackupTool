/**
 * 路由表 —— 严格按 docs/plans/07-前端设计.md §2 的「信息架构」。
 *
 * 注意两点设计决定：
 *   1. `/tasks/:id/snapshots/:sid` 是**独立路由**（不是 Tab），因为它是使用频率最高的页面，
 *      值得有自己的 URL，便于直接发链接与浏览器前进后退。
 *   2. `/tasks/:id/logs` 也是独立路由，便于从别处跳转并带筛选参数。
 *      任务详情里的「日志」Tab 复用同一个 `TaskLogsPanel` 组件，避免两份实现漂移，
 *      并在 Tab 内提供「在新页面打开」的入口。
 *   其余页面内部的 Tab 不占路由。
 */
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    /** 浏览器标题后缀 */
    title?: string
    /** 免登录页面 */
    public?: boolean
    /** 侧边栏高亮用的菜单键 */
    menu?: string
  }
}

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/tasks' },

  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/LoginView.vue'),
    meta: { title: '登录', public: true },
  },

  {
    path: '/',
    component: () => import('@/layouts/AppLayout.vue'),
    children: [
      {
        path: 'tasks',
        name: 'tasks',
        component: () => import('@/views/TasksView.vue'),
        meta: { title: '任务列表', menu: 'tasks' },
      },
      {
        path: 'tasks/:id',
        name: 'task-detail',
        component: () => import('@/views/TaskDetailView.vue'),
        props: true,
        meta: { title: '任务详情', menu: 'tasks' },
      },
      {
        // 独立路由：使用频率最高的页面
        path: 'tasks/:id/snapshots/:sid',
        name: 'snapshot-browser',
        component: () => import('@/views/SnapshotBrowserView.vue'),
        props: true,
        meta: { title: '快照文件浏览', menu: 'tasks' },
      },
      {
        // 独立路由：便于从别处跳转并带筛选参数
        path: 'tasks/:id/logs',
        name: 'task-logs',
        component: () => import('@/views/TaskLogsView.vue'),
        props: true,
        meta: { title: '任务日志', menu: 'tasks' },
      },
      {
        path: 'monitor',
        name: 'monitor',
        component: () => import('@/views/MonitorView.vue'),
        meta: { title: '全局实时监控', menu: 'monitor' },
      },
      {
        path: 'settings',
        name: 'settings',
        component: () => import('@/views/SettingsView.vue'),
        meta: { title: '设置', menu: 'settings' },
      },
      {
        path: 'settings/audit',
        name: 'settings-audit',
        component: () => import('@/views/AuditView.vue'),
        meta: { title: '审计日志', menu: 'settings' },
      },
    ],
  },

  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { title: '页面不存在', public: true },
  },
]

export const router = createRouter({
  // 用 Vite 内置的 BASE_URL，而不是自己去读 VITE_BASE。
  //
  // BASE_URL 由 vite.config.ts 的 `base` 直接派生（且总以 / 结尾），所以
  // "构建配置里的 base"与"客户端路由的 base"**结构上不可能不一致**。
  // 早先的写法是两处各自读一次 VITE_BASE——那会在"某个 .env 文件提供了 VITE_BASE、
  // 而构建时环境里又没有这个变量"时错配成 base='/' 而路由 base='/foo'，
  // 症状是资源能加载、一挂载路由就白屏。改成一个来源之后这类错配不存在了。
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
  scrollBehavior(_to, _from, saved) {
    return saved ?? { top: 0 }
  },
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  await auth.ensureBootstrapped()

  const appTitle = 'FileBackupTool'
  document.title = to.meta.title ? `${to.meta.title} · ${appTitle}` : appTitle

  // 后端不可用：交给 App.vue 的全屏错误态接管，不要在这里重定向到登录页
  // （登录页同样需要后端，重定向只会让用户看到第二个错误态）
  if (auth.status === 'error') return true

  if (to.meta.public) {
    if (to.name === 'login' && auth.isAuthenticated) {
      return { name: 'tasks' }
    }
    return true
  }

  if (!auth.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  return true
})

export default router
