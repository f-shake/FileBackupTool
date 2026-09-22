<script setup lang="ts">
/**
 * 应用外壳。
 *
 * 一个职责：决定「现在该渲染什么」——启动中 / 后端不可用 / 正常路由。
 * 任何情况下都不允许白屏（任务书要求）。
 */
import { onBeforeUnmount, onMounted, watch } from 'vue'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import ErrorState from '@/components/ErrorState.vue'
import { useAuthStore } from '@/stores/auth'
import { useRealtimeStore } from '@/stores/realtime'
import { useTasksStore } from '@/stores/tasks'
import { useThemeStore } from '@/stores/theme'
import { USE_MOCK } from '@/api/http'

const auth = useAuthStore()
const realtime = useRealtimeStore()
const tasks = useTasksStore()
const theme = useThemeStore()

let unwatchSystem: (() => void) | null = null

onMounted(() => {
  auth.installUnauthorizedHandler()
  unwatchSystem = theme.watchSystem()
  // 路由守卫里已经触发过 ensureBootstrapped；这里补一次以防首屏是 /login
  void auth.ensureBootstrapped()
})

onBeforeUnmount(() => {
  unwatchSystem?.()
})

/** 已登录且已完成 TOTP 绑定 → 启动实时通道 */
watch(
  () => [auth.isAuthenticated, auth.mustBindTotp] as const,
  async ([isAuth, mustBind]) => {
    if (isAuth && !mustBind) {
      tasks.wireRealtime()
      await realtime.start()
    } else if (!isAuth) {
      await realtime.stop()
    }
  },
  { immediate: true },
)

/** 收到重载信号（重连成功 / 每轮轮询）→ 重新拉取任务列表 */
watch(
  () => realtime.resyncTick,
  () => {
    if (auth.isAuthenticated) tasks.onResync()
  },
)
</script>

<template>
  <el-config-provider :locale="zhCn">
    <!-- 启动中 -->
    <div v-if="auth.status === 'unknown'" class="boot">
      <div class="boot__card">
        <div class="boot__title">FileBackupTool</div>
        <el-skeleton :rows="2" animated />
        <div class="boot__text">正在确认登录状态…</div>
      </div>
    </div>

    <!-- 后端不可用：明确错误态 + 重试，绝不假装「未登录」把用户丢到登录页 -->
    <div v-else-if="auth.status === 'error'" class="boot">
      <div class="boot__card boot__card--wide">
        <div class="boot__title">FileBackupTool</div>
        <ErrorState
          :error="auth.error"
          title="无法连接后端服务"
          description="前端无法访问 /api 接口，因此无法确认登录状态。请确认后端已启动并监听 http://localhost:5000（开发期由 Vite 代理 /api 与 /hubs）。"
          @retry="auth.ensureBootstrapped(true)"
        >
          <template #actions>
            <el-button size="small" @click="realtime.retry()">重试实时通道</el-button>
          </template>
        </ErrorState>
        <div v-if="USE_MOCK" class="boot__text">
          当前构建启用了 <code>VITE_USE_MOCK=true</code>，若仍看到本页说明 Mock 层没有覆盖 <code>/api/auth/me</code>。
        </div>
      </div>
    </div>

    <router-view v-else />
  </el-config-provider>
</template>

<style scoped>
.boot {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  padding: 24px;
  background-color: var(--el-bg-color-page);
}

.boot__card {
  width: 420px;
  max-width: 100%;
  padding: 28px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  background-color: var(--el-bg-color);
  box-shadow: var(--el-box-shadow-light);
}

.boot__card--wide {
  width: 620px;
}

.boot__title {
  font-size: 18px;
  font-weight: 600;
  margin-bottom: 16px;
}

.boot__text {
  margin-top: 12px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  line-height: 1.7;
}
</style>
