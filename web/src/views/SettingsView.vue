<script setup lang="ts">
/**
 * 设置 `/settings`（07 §2、06 §4.9）。
 *
 * 四块：
 *   通用（数据目录 / 日志 / 磁盘占用 + 自动备份总开关 + 失败率阈值）
 *   安全（改密码 / 会话列表）
 *   还原根白名单（**硬约束**，还原绝不接受白名单之外的路径）
 *   保留策略（默认全手动；开启时必须二次确认）
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Plus, Refresh } from '@element-plus/icons-vue'
import { asApiError, settingsApi } from '@/api'
import type { AppSettings, NormalizedApiError, ServerInfo, SessionInfo } from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import PathText from '@/components/PathText.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import { useAuthStore } from '@/stores/auth'
import { formatBytes, formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const auth = useAuthStore()

/* ============================ 设置本身 ============================ */

const settings = ref<AppSettings | null>(null)
const original = ref<string>('')
const loading = ref(false)
const saving = ref(false)
const error = ref<NormalizedApiError | null>(null)
const loadError = ref<NormalizedApiError | null>(null)

const dirty = computed(() => !!settings.value && JSON.stringify(settings.value) !== original.value)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const s = await settingsApi.getSettings()
    settings.value = s
    // 记录原始快照用于「未保存」提示
    original.value = JSON.stringify(s)
  } catch (err) {
    loadError.value = asApiError(err)
    settings.value = null
  } finally {
    loading.value = false
  }
}

async function save(): Promise<void> {
  if (!settings.value) return
  saving.value = true
  error.value = null
  try {
    const saved = await settingsApi.updateSettings(settings.value)
    settings.value = saved
    original.value = JSON.stringify(saved)
    ElMessage.success('设置已保存')
  } catch (err) {
    error.value = asApiError(err)
  } finally {
    saving.value = false
  }
}

function discard(): void {
  if (!settings.value) return
  settings.value = JSON.parse(original.value) as AppSettings
}

/* -------------------------- 还原根白名单 -------------------------- */

const newRoot = ref('')

function addRoot(): void {
  const value = newRoot.value.trim()
  if (!value) return
  if (!settings.value) return
  if (settings.value.allowedRestoreRoots.includes(value)) {
    ElMessage.warning('该路径已在白名单中')
    return
  }
  settings.value.allowedRestoreRoots.push(value)
  newRoot.value = ''
}

function removeRoot(path: string): void {
  if (!settings.value) return
  settings.value.allowedRestoreRoots = settings.value.allowedRestoreRoots.filter((p) => p !== path)
}

/* -------------------------- 保留策略 -------------------------- */

/**
 * 保留策略默认关闭，**开启时必须二次确认**（04 §2.3）——
 * 它会真的删掉快照，这是破坏性配置。
 */
async function onRetentionToggle(enabled: boolean): Promise<void> {
  if (!settings.value) return
  if (!enabled) {
    settings.value.retention.enabled = false
    return
  }
  try {
    await ElMessageBox.confirm(
      '开启保留策略后，系统会在每次备份完成后按策略自动删除旧快照。删除是级联的且不可撤销，请确认策略参数无误。',
      '开启保留策略',
      { type: 'warning', confirmButtonText: '我已了解，开启', cancelButtonText: '保持关闭' },
    )
    settings.value.retention.enabled = true
  } catch {
    settings.value.retention.enabled = false
  }
}

const retentionModeHint = computed(() => {
  const mode = settings.value?.retention.mode
  if (mode === 'Count') return '保留每个全量链组中最近的 N 个快照'
  if (mode === 'Days') return '删除超过 N 天的快照'
  if (mode === 'Size') return '快照总占用超过 N 字节时从最旧的开始删'
  return ''
})

/* ============================ 只读服务端信息 ============================ */

const info = ref<ServerInfo | null>(null)
const infoError = ref<NormalizedApiError | null>(null)

async function loadInfo(): Promise<void> {
  try {
    info.value = await settingsApi.getServerInfo()
    infoError.value = null
  } catch (err) {
    infoError.value = asApiError(err)
    info.value = null
  }
}

onMounted(() => {
  void load()
  void loadInfo()
})

/* ============================ 安全 ============================ */

const me = computed(() => auth.user)

/* --- 改密码 --- */
const pwdVisible = ref(false)
const pwdSaving = ref(false)
const pwdError = ref<NormalizedApiError | null>(null)
const pwdForm = ref({ currentPassword: '', newPassword: '', confirmPassword: '' })

function openPassword(): void {
  pwdForm.value = { currentPassword: '', newPassword: '', confirmPassword: '' }
  pwdError.value = null
  pwdVisible.value = true
}

async function submitPassword(): Promise<void> {
  if (pwdForm.value.newPassword !== pwdForm.value.confirmPassword) {
    pwdError.value = {
      status: 0,
      code: 'PASSWORD_MISMATCH',
      message: '两次输入的新密码不一致',
      backendUnavailable: false,
    }
    return
  }
  pwdSaving.value = true
  pwdError.value = null
  try {
    await auth.changePassword({ ...pwdForm.value })
    pwdVisible.value = false
    ElMessage.success('密码已修改，所有会话已失效，请重新登录')
  } catch (err) {
    pwdError.value = asApiError(err)
  } finally {
    pwdSaving.value = false
  }
}

/* --- 会话 --- */
const sessions = ref<SessionInfo[]>([])
const sessionsLoading = ref(false)
const sessionsError = ref<NormalizedApiError | null>(null)

async function loadSessions(): Promise<void> {
  sessionsLoading.value = true
  sessionsError.value = null
  try {
    sessions.value = await auth.listSessions()
  } catch (err) {
    sessionsError.value = asApiError(err)
    sessions.value = []
  } finally {
    sessionsLoading.value = false
  }
}

async function revokeSession(session: SessionInfo): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `将踢出 ${session.sourceIp} 上的会话（${session.userAgent}）。该设备需要重新登录。`,
      '踢出会话',
      { type: 'warning', confirmButtonText: '踢出', cancelButtonText: '取消' },
    )
  } catch {
    return
  }
  try {
    await auth.revokeSession(session.id)
    ElMessage.success('会话已踢出')
    await loadSessions()
  } catch (err) {
    ElMessage.error(asApiError(err).message || '操作失败')
  }
}

watch(
  () => auth.isAuthenticated,
  (yes) => {
    if (yes) void loadSessions()
  },
)

onMounted(() => {
  if (auth.isAuthenticated) void loadSessions()
})
</script>

<template>
  <div class="fbt-page">
    <div class="fbt-page-header">
      <div>
        <h1 class="fbt-page-title">设置</h1>
        <div class="fbt-page-desc">通用与安全设置、还原根白名单、保留策略</div>
      </div>
      <div class="fbt-toolbar">
        <el-tag v-if="dirty" type="warning" size="small">有未保存的修改</el-tag>
        <el-button size="small" :disabled="!dirty" @click="discard">放弃修改</el-button>
        <el-button size="small" type="primary" :loading="saving" :disabled="!dirty" @click="save">
          保存设置
        </el-button>
        <el-button size="small" :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <ErrorState v-if="error" :error="error" compact class="fbt-section" />
    <ErrorState v-if="loadError" :error="loadError" @retry="load" />
    <StatePlaceholder v-else-if="loading && !settings" text="正在加载设置…" />

    <template v-else-if="settings">
      <div class="settings__grid">
        <!-- ======================= 通用 ======================= -->
        <div class="fbt-pane">
          <div class="fbt-pane-title">通用</div>

          <el-form label-width="170px" label-position="left">
            <!-- 对等清单要求：自动备份总开关 -->
            <el-form-item label="自动备份总开关">
              <el-switch v-model="settings.autoBackupEnabled" />
              <span class="fbt-footnote settings__hint">
                关闭后所有定时与监控触发的备份都会被暂停（手动「立即备份」仍可用）
              </span>
            </el-form-item>

            <el-form-item label="失败率告警阈值">
              <el-input-number
                v-model="settings.failureRateThresholdPercent"
                :min="0"
                :max="100"
                :step="1"
              />
              <span class="fbt-footnote">% —— 单次备份失败文件占比超过该值时告警</span>
            </el-form-item>

            <el-form-item label="完整校验默认深校验">
              <el-switch v-model="settings.verifyDeepHashByDefault" />
              <span class="fbt-footnote settings__hint">
                重算哈希在 TB 级数据上极慢，默认关闭
              </span>
            </el-form-item>
          </el-form>

          <div class="fbt-pane-title" style="margin-top: 12px">服务端信息（只读）</div>
          <ErrorState v-if="infoError" :error="infoError" compact @retry="loadInfo" />
          <StatePlaceholder v-else-if="!info" text="正在读取…" />
          <el-descriptions v-else :column="1" size="small" border>
            <el-descriptions-item label="版本">
              <span class="fbt-mono">{{ info.version }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="数据目录">
              <PathText :path="info.dataDir" :max-length="60" />
            </el-descriptions-item>
            <el-descriptions-item label="日志目录">
              <PathText :path="info.logDir" :max-length="60" />
            </el-descriptions-item>
            <el-descriptions-item label="数据库">
              <PathText :path="info.databasePath" :max-length="52" />
              <span class="fbt-mono">（{{ formatBytes(info.databaseBytes) }}）</span>
            </el-descriptions-item>
            <el-descriptions-item label="blob 池占用">
              <span class="fbt-mono">{{ formatBytes(info.blobPoolBytes) }}</span>
            </el-descriptions-item>
            <!-- 回收站空间未释放，必须可见 -->
            <el-descriptions-item label="回收站占用">
              <span class="fbt-warn-count">{{ formatBytes(info.recycleBinBytes) }}</span>
              <span class="fbt-footnote">（空间尚未释放）</span>
            </el-descriptions-item>
            <el-descriptions-item label="磁盘剩余 / 总量">
              <span class="fbt-mono">
                {{ formatBytes(info.diskFreeBytes) }} / {{ formatBytes(info.diskTotalBytes) }}
              </span>
            </el-descriptions-item>
            <el-descriptions-item label="启动时间">
              <span class="fbt-mono">{{ formatDateTime(info.startedAt) }}</span>
            </el-descriptions-item>
            <el-descriptions-item v-if="info.notices && info.notices.length" label="提示">
              <div v-for="(n, i) in info.notices" :key="i" class="fbt-warn-count">{{ n }}</div>
            </el-descriptions-item>
          </el-descriptions>
        </div>

        <!-- ======================= 保留策略 + 白名单 ======================= -->
        <div class="fbt-pane">
          <div class="fbt-pane-title">保留策略</div>
          <el-alert
            type="info"
            :closable="false"
            show-icon
            title="默认全手动：系统不会自动删除任何快照。开启后请确认参数。"
          />
          <el-form label-width="170px" label-position="left" style="margin-top: 10px">
            <el-form-item label="启用保留策略">
              <el-switch
                :model-value="settings.retention.enabled"
                @update:model-value="(v: boolean) => onRetentionToggle(v)"
              />
            </el-form-item>
            <el-form-item label="策略模式">
              <el-select v-model="settings.retention.mode" :disabled="!settings.retention.enabled" style="width: 160px">
                <el-option label="按数量" value="Count" />
                <el-option label="按天数" value="Days" />
                <el-option label="按总大小" value="Size" />
              </el-select>
              <span class="fbt-footnote settings__hint">{{ retentionModeHint }}</span>
            </el-form-item>
            <el-form-item label="阈值">
              <el-input-number
                v-model="settings.retention.value"
                :min="1"
                :disabled="!settings.retention.enabled"
              />
              <span class="fbt-footnote">
                {{ settings.retention.mode === 'Count' ? '个快照' : settings.retention.mode === 'Days' ? '天' : '字节' }}
              </span>
            </el-form-item>
            <el-form-item label="只对完整全量链组生效">
              <el-switch
                v-model="settings.retention.applyToFullChains"
                :disabled="!settings.retention.enabled"
              />
            </el-form-item>
          </el-form>

          <div class="fbt-pane-title" style="margin-top: 14px">还原根白名单</div>
          <el-alert
            type="warning"
            :closable="false"
            show-icon
            title="硬约束：还原绝不接受白名单之外的路径。这里没列出的目录，向导里选不到。"
          />

          <div class="settings__root-add">
            <el-input
              v-model="newRoot"
              size="small"
              placeholder="例如 D:\Restore（服务器本机路径）"
              @keyup.enter="addRoot"
            />
            <el-button size="small" :icon="Plus" @click="addRoot">添加</el-button>
          </div>

          <StatePlaceholder
            v-if="settings.allowedRestoreRoots.length === 0"
            empty
            description="白名单为空 —— 此时无法执行任何还原"
          />
          <el-table v-else :data="settings.allowedRestoreRoots.map((p) => ({ path: p }))" size="small" border>
            <el-table-column label="路径" min-width="300">
              <template #default="{ row }">
                <PathText :path="row.path" :max-length="56" />
              </template>
            </el-table-column>
            <el-table-column label="操作" width="90">
              <template #default="{ row }">
                <el-button size="small" text type="danger" :icon="Delete" @click="removeRoot(row.path)">
                  移除
                </el-button>
              </template>
            </el-table-column>
          </el-table>
          <div class="fbt-footnote">改动需要点右上角「保存设置」才会生效。</div>
        </div>
      </div>

      <!-- ======================= 安全 ======================= -->
      <div class="fbt-pane fbt-section">
        <div class="fbt-pane-title">安全</div>

        <div class="settings__security">
          <el-descriptions :column="2" size="small" border>
            <el-descriptions-item label="用户名">
              <span class="fbt-mono">{{ me?.username ?? '—' }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="本次登录时间">
              <span class="fbt-mono">
                {{ me?.lastLoginTime ? formatDateTime(me.lastLoginTime) : '—' }}
              </span>
            </el-descriptions-item>
            <el-descriptions-item label="本次登录 IP">
              <span class="fbt-mono">{{ me?.lastLoginIp ?? '—' }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="登录方式">
              <span>单密码 + Cookie 会话</span>
            </el-descriptions-item>
          </el-descriptions>

          <div class="settings__actions">
            <el-button size="small" @click="openPassword">修改密码</el-button>
          </div>
        </div>

        <div class="fbt-pane-title" style="margin-top: 14px">会话列表</div>
        <ErrorState v-if="sessionsError" :error="sessionsError" compact @retry="loadSessions" />
        <StatePlaceholder v-else-if="sessionsLoading && sessions.length === 0" text="正在读取会话…" />
        <StatePlaceholder v-else-if="sessions.length === 0" empty description="没有活动会话" />
        <el-table v-else :data="sessions" size="small" border>
          <el-table-column label="来源 IP" width="150">
            <template #default="{ row }">
              <span class="fbt-mono">{{ row.sourceIp }}</span>
            </template>
          </el-table-column>
          <el-table-column label="客户端" min-width="240">
            <template #default="{ row }">
              <span class="fbt-ellipsis" :title="row.userAgent">{{ row.userAgent }}</span>
            </template>
          </el-table-column>
          <el-table-column label="创建时间" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.createdAt) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="最近活动" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.lastSeenAt) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="操作" width="110">
            <template #default="{ row }">
              <el-tag v-if="row.current" type="success" size="small">当前会话</el-tag>
              <el-button v-else size="small" text type="danger" @click="revokeSession(row)">踢出</el-button>
            </template>
          </el-table-column>
        </el-table>
        <div class="fbt-footnote">
          共 {{ formatCount(sessions.length) }} 个会话 · 会话为滑动 7 天 / 绝对 30 天（05 §3）
        </div>
      </div>
    </template>

    <!-- 改密码 -->
    <el-dialog v-model="pwdVisible" title="修改密码" width="440px">
      <ErrorState v-if="pwdError" :error="pwdError" compact class="fbt-section" />
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="修改密码会一次性失效全部会话（包括当前会话），需要重新登录。"
        class="fbt-section"
      />
      <el-form label-width="100px">
        <el-form-item label="当前密码">
          <el-input v-model="pwdForm.currentPassword" type="password" show-password autocomplete="current-password" />
        </el-form-item>
        <el-form-item label="新密码">
          <el-input v-model="pwdForm.newPassword" type="password" show-password autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="确认新密码">
          <el-input v-model="pwdForm.confirmPassword" type="password" show-password autocomplete="new-password" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="pwdVisible = false">取消</el-button>
        <el-button type="primary" :loading="pwdSaving" @click="submitPassword">确认修改</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.settings__grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(430px, 1fr));
  gap: 12px;
  align-items: start;
}

.settings__hint {
  margin-left: 10px;
}

.settings__root-add {
  display: flex;
  gap: 8px;
  margin: 10px 0;
}

.settings__security {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.settings__actions {
  display: flex;
  gap: 8px;
}
</style>
