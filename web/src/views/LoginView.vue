<script setup lang="ts">
/**
 * 登录 `/login`（07 §2、05 §2）。
 *
 * 三个步骤：
 *   1. `credentials` 用户名 + 密码
 *   2. `totp`        已绑定 → 输入动态码（或一次性恢复码）
 *   3. `bind`        **首次登录强制绑定 TOTP**，未绑定前不能访问任何业务功能（05 §2.2）
 *
 * 关于二维码：如果后端在 `TotpSetupResult.qrCodePngBase64` 里直接给了 PNG 就显示图片；
 * 否则只提供 Base32 密钥供手动输入 —— **不为了画二维码引入新的前端依赖**。
 */
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Key, Lock, User } from '@element-plus/icons-vue'
import { asApiError } from '@/api'
import type { NormalizedApiError, TotpSetupResult } from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import RecoveryCodesDialog from '@/components/RecoveryCodesDialog.vue'
import { copyText } from '@/composables/useClipboard'
import { useAuthStore } from '@/stores/auth'
import { appSubtitle, appTitle } from '@/locales/zh-CN'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

type Step = 'credentials' | 'totp' | 'bind'
const step = ref<Step>('credentials')

const username = ref('')
const password = ref('')
const totpCode = ref('')
const useRecoveryCode = ref(false)
const recoveryCode = ref('')

const working = ref(false)
const error = ref<NormalizedApiError | null>(null)

/** 登录成功后要回到哪里 */
const redirect = computed(() => {
  const raw = route.query.redirect
  const value = Array.isArray(raw) ? raw[0] : raw
  return value && value.startsWith('/') ? value : '/tasks'
})

/** 路由守卫在「已登录但未绑定 TOTP」时会带 step=bind 过来 */
onMounted(() => {
  if (route.query.step === 'bind' && auth.isAuthenticated) {
    step.value = 'bind'
    void beginBind()
  }
})

function goAfterLogin(): void {
  ElMessage.success('登录成功')
  void router.replace(redirect.value)
}

/* ------------------------------- 第 1 步：用户名 / 密码 ------------------------------- */

async function submitCredentials(): Promise<void> {
  if (!username.value || !password.value) {
    error.value = localError('请填写用户名和密码')
    return
  }
  working.value = true
  error.value = null
  try {
    const result = await auth.login({ username: username.value, password: password.value })
    if (result.authenticated) {
      goAfterLogin()
      return
    }
    if (result.requiresTotpSetup) {
      // 首次登录：强制绑定
      step.value = 'bind'
      await beginBind()
      return
    }
    if (result.requiresTotp) {
      step.value = 'totp'
      if (result.message) ElMessage.info(result.message)
      return
    }
    error.value = localError(result.message ?? '登录未完成，请重试')
  } catch (err) {
    error.value = asApiError(err)
  } finally {
    working.value = false
  }
}

/* ------------------------------- 第 2 步：TOTP ------------------------------- */

async function submitTotp(): Promise<void> {
  const base = { username: username.value, password: password.value }
  const payload = useRecoveryCode.value
    ? { ...base, recoveryCode: recoveryCode.value.trim() }
    : { ...base, totpCode: totpCode.value.trim() }

  if (useRecoveryCode.value && !recoveryCode.value.trim()) {
    error.value = localError('请输入恢复码')
    return
  }
  if (!useRecoveryCode.value && !totpCode.value.trim()) {
    error.value = localError('请输入验证器上的 6 位动态码')
    return
  }

  working.value = true
  error.value = null
  try {
    const result = await auth.login(payload)
    if (result.authenticated) {
      goAfterLogin()
      return
    }
    error.value = localError(result.message ?? '动态码不正确')
  } catch (err) {
    error.value = asApiError(err)
  } finally {
    working.value = false
  }
}

/* ------------------------------- 第 3 步：强制绑定 ------------------------------- */

const setup = ref<TotpSetupResult | null>(null)
const setupLoading = ref(false)
const setupError = ref<NormalizedApiError | null>(null)
const bindCode = ref('')
const codesVisible = ref(false)
const codes = ref<string[]>([])

async function beginBind(): Promise<void> {
  setupLoading.value = true
  setupError.value = null
  try {
    setup.value = await auth.setupTotp()
  } catch (err) {
    setupError.value = asApiError(err)
    setup.value = null
  } finally {
    setupLoading.value = false
  }
}

async function submitBind(): Promise<void> {
  if (!bindCode.value.trim()) {
    error.value = localError('请输入验证器上显示的 6 位动态码')
    return
  }
  working.value = true
  error.value = null
  try {
    codes.value = await auth.confirmTotp(bindCode.value.trim())
    codesVisible.value = true
    ElMessage.success('TOTP 绑定完成')
  } catch (err) {
    error.value = asApiError(err)
  } finally {
    working.value = false
  }
}

/** 恢复码确认保存后再放行 —— 恢复码是验证器丢失时的唯一退路 */
function onCodesClosed(open: boolean): void {
  codesVisible.value = open
  if (!open) goAfterLogin()
}

/** 共享密钥复制（非安全上下文下 navigator.clipboard 不可用，走降级实现） */
async function copySharedKey(): Promise<void> {
  const key = setup.value?.sharedKey
  if (!key) return
  const ok = await copyText(key)
  if (ok) ElMessage.success('共享密钥已复制')
  else ElMessage.warning('复制失败，请手动选中复制')
}

function localError(message: string): NormalizedApiError {
  return { status: 0, code: 'VALIDATION', message, backendUnavailable: false }
}

const qrSrc = computed(() => {
  const b64 = setup.value?.qrCodePngBase64
  if (!b64) return null
  return b64.startsWith('data:') ? b64 : `data:image/png;base64,${b64}`
})
</script>

<template>
  <div class="login">
    <div class="login__card">
      <div class="login__brand">
        <h1 class="login__title">{{ appTitle }}</h1>
        <p class="login__subtitle">{{ appSubtitle }}</p>
      </div>

      <ErrorState v-if="error" :error="error" compact class="fbt-section" />

      <!-- ========================= 第 1 步 ========================= -->
      <template v-if="step === 'credentials'">
        <el-alert
          type="info"
          :closable="false"
          show-icon
          class="fbt-section"
          title="站点暴露在公网，登录接口有严格限流；连续 5 次失败会锁定账户 15 分钟。"
        />
        <el-form label-position="top" @submit.prevent="submitCredentials">
          <el-form-item label="用户名">
            <el-input v-model="username" size="large" :prefix-icon="User" autocomplete="username" />
          </el-form-item>
          <el-form-item label="密码">
            <el-input
              v-model="password"
              size="large"
              type="password"
              show-password
              :prefix-icon="Lock"
              autocomplete="current-password"
              @keyup.enter="submitCredentials"
            />
          </el-form-item>
        </el-form>
        <el-button type="primary" size="large" class="login__submit" :loading="working" @click="submitCredentials">
          登录
        </el-button>
      </template>

      <!-- ========================= 第 2 步 ========================= -->
      <template v-else-if="step === 'totp'">
        <div class="login__stepbar">
          <el-steps :active="1" simple>
            <el-step title="密码" />
            <el-step title="两步验证" />
            <el-step title="完成" />
          </el-steps>
        </div>

        <template v-if="!useRecoveryCode">
          <el-form label-position="top">
            <el-form-item label="验证器动态码">
              <el-input
                v-model="totpCode"
                size="large"
                maxlength="6"
                placeholder="6 位数字"
                :prefix-icon="Key"
                @keyup.enter="submitTotp"
              />
            </el-form-item>
          </el-form>
        </template>

        <template v-else>
          <el-form label-position="top">
            <el-form-item label="一次性恢复码">
              <el-input
                v-model="recoveryCode"
                size="large"
                placeholder="例如 xxxx-xxxx"
                :prefix-icon="Key"
                @keyup.enter="submitTotp"
              />
            </el-form-item>
          </el-form>
        </template>

        <el-button type="primary" size="large" class="login__submit" :loading="working" @click="submitTotp">
          验证并登录
        </el-button>

        <div class="login__links">
          <el-link :underline="false" @click="useRecoveryCode = !useRecoveryCode">
            {{ useRecoveryCode ? '改用验证器动态码' : '验证器丢了？用一次性恢复码' }}
          </el-link>
          <el-link :underline="false" @click="step = 'credentials'">返回上一步</el-link>
        </div>
      </template>

      <!-- ========================= 第 3 步：强制绑定 ========================= -->
      <template v-else>
        <el-alert
          type="warning"
          :closable="false"
          show-icon
          class="fbt-section"
          title="必须先绑定两步验证（TOTP）才能使用任何功能。这是防止密码泄露导致服务器沦陷的唯一低成本手段。"
        />

        <ErrorState v-if="setupError" :error="setupError" compact @retry="beginBind" />

        <div v-else class="login__bind">
          <div class="login__qr">
            <img v-if="qrSrc" :src="qrSrc" alt="TOTP 二维码" class="login__qr-img" />
            <div v-else class="login__qr-missing">
              <p>后端没有返回二维码图片，请用验证器**手动输入**下面的密钥：</p>
            </div>
          </div>

          <div class="login__key">
            <div class="fbt-pane-title">共享密钥（手工输入用）</div>
            <el-input :model-value="setup?.sharedKey ?? ''" readonly class="fbt-mono">
              <template #append>
                <el-button @click="copySharedKey">复制</el-button>
              </template>
            </el-input>
            <div class="fbt-pane-title" style="margin-top: 10px">otpauth URI</div>
            <el-input :model-value="setup?.authenticatorUri ?? ''" readonly type="textarea" :rows="2" />
          </div>
        </div>

        <el-form label-position="top" class="fbt-section">
          <el-form-item label="输入验证器上显示的 6 位动态码以完成绑定">
            <el-input
              v-model="bindCode"
              size="large"
              maxlength="6"
              placeholder="6 位数字"
              :prefix-icon="Key"
              @keyup.enter="submitBind"
            />
          </el-form-item>
        </el-form>

        <el-button
          type="primary"
          size="large"
          class="login__submit"
          :loading="working"
          :disabled="setupLoading || !setup"
          @click="submitBind"
        >
          完成绑定
        </el-button>
      </template>
    </div>

    <RecoveryCodesDialog
      :model-value="codesVisible"
      :codes="codes"
      title="绑定成功 —— 请保存恢复码"
      @update:model-value="onCodesClosed"
    />
  </div>
</template>

<style scoped>
.login {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background-color: var(--el-bg-color-page);
}

.login__card {
  width: 100%;
  max-width: 460px;
  padding: 28px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 10px;
  background-color: var(--el-bg-color);
  box-shadow: var(--el-box-shadow-light);
}

.login__brand {
  text-align: center;
  margin-bottom: 18px;
}

.login__title {
  margin: 0;
  font-size: 22px;
  font-weight: 600;
}

.login__subtitle {
  margin: 4px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.login__submit {
  width: 100%;
}

.login__links {
  display: flex;
  justify-content: space-between;
  margin-top: 12px;
}

.login__stepbar {
  margin-bottom: 14px;
}

.login__bind {
  display: flex;
  gap: 16px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.login__qr {
  flex: 0 0 auto;
}

.login__qr-img {
  width: 150px;
  height: 150px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 6px;
}

.login__qr-missing {
  width: 150px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.login__key {
  flex: 1 1 220px;
  min-width: 0;
}
</style>
