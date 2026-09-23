<script setup lang="ts">
/**
 * 应用主布局：顶部栏 + 左侧导航 + 内容区。
 *
 * 顶部栏承担 07 §7 的全局约定，以及**只有全局才该有的控件**：
 *   实时通道状态（实时/轮询）、自动备份总开关、主题切换（跟随系统/亮/暗）。
 * 实时徽章与自动备份总开关原先在任务页/监控页/设置页各挂了一份，现收敛到顶栏这一处——
 * 它们本来就是全局状态，散在多页只会让"哪个才是准的"变成问题。
 */
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  ArrowDown,
  Bell,
  DataLine,
  List,
  Monitor,
  Moon,
  Setting,
  SwitchButton,
  Sunny,
  Tickets,
  User,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { asApiError, settingsApi } from '@/api'
import type { AppSettings, ThemePreference } from '@/api/types'
import ChannelBadge from '@/components/ChannelBadge.vue'
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const theme = useThemeStore()

/**
 * 自动备份总开关。它是全局状态，所以只有一个入口（顶栏）。
 * 读不到设置时开关**保持禁用**而不是显示成"已关闭"——把"不知道"画成"关"会误导人。
 */
const settings = ref<AppSettings | null>(null)
const togglingAuto = ref(false)

const autoTip = computed(() =>
  settings.value
    ? '关闭后，定时与监控触发的备份都会被暂停；手动「立即备份」仍可用'
    : '无法读取全局设置（GET /api/settings 不可用），此开关暂不可操作',
)

onMounted(async () => {
  try {
    settings.value = await settingsApi.getSettings()
  } catch {
    settings.value = null
  }
})

async function toggleAutoBackup(value: string | number | boolean): Promise<void> {
  if (!settings.value) return
  const next = Boolean(value)
  togglingAuto.value = true
  try {
    settings.value = await settingsApi.updateSettings({ ...settings.value, autoBackupEnabled: next })
    ElMessage.success(next ? '自动备份已开启' : '自动备份已关闭（手动备份仍可用）')
  } catch (err) {
    ElMessage.error(asApiError(err).message)
    // PUT 失败后必须回读：否则界面停在用户以为已生效的位置
    try {
      settings.value = await settingsApi.getSettings()
    } catch {
      settings.value = null
    }
  } finally {
    togglingAuto.value = false
  }
}

const activeMenu = computed(() => route.meta.menu ?? 'tasks')

const themeLabel = computed(() => {
  switch (theme.preference) {
    case 'light':
      return '亮色'
    case 'dark':
      return '暗色'
    default:
      return '跟随系统'
  }
})

function setTheme(value: ThemePreference): void {
  theme.setPreference(value)
}

async function handleLogout(): Promise<void> {
  try {
    await ElMessageBox.confirm('确定要退出登录吗？', '退出登录', {
      confirmButtonText: '退出',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  await auth.logout()
  ElMessage.success('已退出登录')
  await router.push({ name: 'login' })
}
</script>

<template>
  <el-container class="layout">
    <el-header class="layout__header">
      <div class="layout__brand" @click="router.push({ name: 'tasks' })">
        <el-icon class="layout__logo"><Tickets /></el-icon>
        <span class="layout__title">FileBackupTool</span>
        <span class="layout__subtitle">文件备份工具</span>
      </div>

      <div class="fbt-spacer" />

      <ChannelBadge />

      <!-- 自动备份总开关：夹在实时徽章与主题切换之间（全局控件只此一处） -->
      <el-tooltip :content="autoTip" placement="bottom">
        <div class="layout__auto">
          <span class="layout__auto-label">自动备份</span>
          <el-switch
            :model-value="settings?.autoBackupEnabled ?? false"
            :loading="togglingAuto"
            :disabled="!settings"
            inline-prompt
            active-text="开"
            inactive-text="关"
            @update:model-value="toggleAutoBackup"
          />
        </div>
      </el-tooltip>

      <el-dropdown trigger="click" @command="setTheme">
        <el-button text :icon="theme.isDark ? Moon : Sunny">
          {{ themeLabel }}
          <el-icon class="layout__caret"><ArrowDown /></el-icon>
        </el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="auto" :disabled="theme.preference === 'auto'">
              跟随系统
            </el-dropdown-item>
            <el-dropdown-item command="light" :disabled="theme.preference === 'light'">
              强制亮色
            </el-dropdown-item>
            <el-dropdown-item command="dark" :disabled="theme.preference === 'dark'">
              强制暗色
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>

      <el-dropdown trigger="click">
        <el-button text :icon="User">
          {{ auth.user?.username ?? '未登录' }}
          <el-icon class="layout__caret"><ArrowDown /></el-icon>
        </el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item @click="router.push({ name: 'settings' })">
              <el-icon><Setting /></el-icon> 设置
            </el-dropdown-item>
            <el-dropdown-item @click="handleLogout">
              <el-icon><SwitchButton /></el-icon> 退出登录
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </el-header>

    <el-container class="layout__body">
      <el-aside class="layout__aside" :width="'var(--fbt-aside-width)'">
        <el-menu :default-active="activeMenu" router class="layout__menu">
          <el-menu-item index="tasks" :route="{ name: 'tasks' }">
            <el-icon><List /></el-icon>
            <span>任务</span>
          </el-menu-item>
          <el-menu-item index="monitor" :route="{ name: 'monitor' }">
            <el-icon><DataLine /></el-icon>
            <span>全局实时监控</span>
          </el-menu-item>
          <el-menu-item index="settings" :route="{ name: 'settings' }">
            <el-icon><Setting /></el-icon>
            <span>设置</span>
          </el-menu-item>
          <el-menu-item index="settings-audit" :route="{ name: 'settings-audit' }">
            <el-icon><Bell /></el-icon>
            <span>审计日志</span>
          </el-menu-item>
        </el-menu>

        <div class="layout__aside-foot">
          <el-tooltip content="作业在服务端全局串行执行：任意时刻最多只有一个备份或还原在跑" placement="top">
            <div class="layout__aside-note">
              <el-icon><Monitor /></el-icon>
              全局串行 IO 队列
            </div>
          </el-tooltip>
        </div>
      </el-aside>

      <el-main class="layout__main">
        <router-view v-slot="{ Component }">
          <!-- 用 path 作为 key：同一个页面换 :id 时重新挂载，但改查询串（筛选条件）不重挂 -->
          <component :is="Component" :key="route.path" />
        </router-view>
      </el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.layout {
  height: 100vh;
}

.layout__header {
  display: flex;
  align-items: center;
  gap: 8px;
  height: var(--fbt-header-height);
  padding: 0 16px;
  border-bottom: 1px solid var(--el-border-color-lighter);
  background-color: var(--el-bg-color);
}

.layout__brand {
  display: flex;
  align-items: baseline;
  gap: 8px;
  cursor: pointer;
}

.layout__logo {
  color: var(--el-color-primary);
  font-size: 20px;
  align-self: center;
}

.layout__title {
  font-size: 16px;
  font-weight: 600;
}

.layout__subtitle {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.layout__caret {
  margin-left: 2px;
  font-size: 12px;
}

.layout__auto {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 6px;
  cursor: default;
}

.layout__auto-label {
  font-size: 13px;
  color: var(--el-text-color-regular);
  white-space: nowrap;
}

.layout__body {
  height: calc(100vh - var(--fbt-header-height));
}

.layout__aside {
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--el-border-color-lighter);
  background-color: var(--el-bg-color);
}

.layout__menu {
  border-right: none;
  flex: 1 1 auto;
}

.layout__aside-foot {
  padding: 10px 12px;
  border-top: 1px solid var(--el-border-color-lighter);
}

.layout__aside-note {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  cursor: help;
}

.layout__main {
  padding: 0;
  overflow: auto;
  background-color: var(--el-bg-color-page);
}
</style>
