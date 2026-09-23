<script setup lang="ts">
/**
 * 登录 `/login`（07 §2、05 §2）。
 *
 * 单密码模式：只有一个密码框。账号不由用户提供——服务端按配置的
 * `SecurityOptions.EffectiveSeedUserName` 解析出那唯一的账号。
 */
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Lock } from '@element-plus/icons-vue'
import { asApiError } from '@/api'
import type { NormalizedApiError } from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import { useAuthStore } from '@/stores/auth'
import { appSubtitle, appTitle } from '@/locales/zh-CN'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const password = ref('')
const working = ref(false)
const error = ref<NormalizedApiError | null>(null)

/** 登录成功后要回到哪里 */
const redirect = computed(() => {
  const raw = route.query.redirect
  const value = Array.isArray(raw) ? raw[0] : raw
  return value && value.startsWith('/') ? value : '/tasks'
})

function localError(message: string): NormalizedApiError {
  return { status: 0, code: 'VALIDATION', message, backendUnavailable: false }
}

async function submit(): Promise<void> {
  // 重入守卫：回车与快速双击都可能让 submit 被调两次（见下方模板里的说明），
  // 两次并发登录的后果不是"多一次请求"那么轻——错密码会双倍计数（5 次阈值实际 3 次就锁号），
  // 对密码会建出两个会话（会话列表里出现用户无法解释的幽灵条目，且两个 Set-Cookie 互相竞争）。
  if (working.value) return

  if (!password.value) {
    error.value = localError('请输入密码')
    return
  }

  working.value = true
  error.value = null
  try {
    await auth.login({ password: password.value })
    ElMessage.success('登录成功')
    await router.replace(redirect.value)
  } catch (err) {
    // 密码框刻意不清空：多数情况是打错一位，清掉反而要重输一遍。
    error.value = asApiError(err)
  } finally {
    working.value = false
  }
}
</script>

<template>
  <div class="login">
    <div class="login__card">
      <div class="login__brand">
        <h1 class="login__title">{{ appTitle }}</h1>
        <p class="login__subtitle">{{ appSubtitle }}</p>
      </div>

      <ErrorState v-if="error" :error="error" compact class="fbt-section" />

      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="fbt-section"
        title="站点暴露在公网，登录接口有严格限流；连续 5 次失败会锁定账户 15 分钟。"
      />

      <!--
        登录按钮**必须在 <form> 内部**且 native-type="submit"，且输入框上**不能**再挂 @keyup.enter。
        原因：`el-form` 渲染的是裸 <form>，而按 HTML 的隐式提交规则，
        "表单里没有提交按钮、且阻止隐式提交的字段（文本类输入框）不超过一个"时，
        在输入框里回车会**提交表单**。现在表单里只剩密码一个文本字段，于是回车会同时触发
        隐式提交（submit 事件）与 keyup 两条路径 —— 双双调用 submit()，一次回车发两个登录请求。
        （改前表单里有用户名+密码两个文本字段，隐式提交被抑制，所以只有 keyup 一条路径。）
        摆成现在这样，回车与点击都只经过 submit 事件这一条路径。
      -->
      <el-form label-position="top" @submit.prevent="submit">
        <el-form-item label="密码">
          <el-input
            v-model="password"
            size="large"
            type="password"
            show-password
            :prefix-icon="Lock"
            autocomplete="current-password"
          />
        </el-form-item>

        <el-button type="primary" size="large" native-type="submit" class="login__submit" :loading="working">
          登录
        </el-button>
      </el-form>
    </div>
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
</style>
