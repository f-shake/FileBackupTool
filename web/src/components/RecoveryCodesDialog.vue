<script setup lang="ts">
/**
 * 恢复码展示弹窗（05 §2.2）。
 *
 * 恢复码**只在生成的那一刻可见一次**，所以：
 *   - 必须能一键复制 / 下载成 txt；
 *   - 必须勾选「我已保存」才能关闭，避免用户随手点掉后永久丢失。
 */
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { CopyDocument, Download } from '@element-plus/icons-vue'
import { copyText } from '@/composables/useClipboard'

const props = withDefaults(
  defineProps<{
    modelValue: boolean
    codes: string[]
    title?: string
  }>(),
  { title: '恢复码' },
)

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

const acknowledged = ref(false)

/** 关闭前必须确认已保存（恢复码不会再显示第二次） */
const visible = computed({
  get: () => props.modelValue,
  set: (v: boolean) => {
    if (!v && !acknowledged.value) {
      ElMessage.warning('请先勾选「我已妥善保存恢复码」再关闭')
      return
    }
    emit('update:modelValue', v)
  },
})

watch(
  () => props.modelValue,
  (open) => {
    if (open) acknowledged.value = false
  },
)

async function copyAll(): Promise<void> {
  const ok = await copyText(props.codes.join('\n'))
  if (ok) ElMessage.success('恢复码已复制到剪贴板')
  else ElMessage.warning('复制失败：浏览器拒绝了剪贴板访问，请手动选中复制')
}

function download(): void {
  const header = [
    'FileBackupTool 恢复码',
    `生成时间：${new Date().toLocaleString('zh-CN')}`,
    '每个恢复码只能使用一次，请离线保存，不要放在本机上。',
    '',
  ].join('\n')
  const blob = new Blob([header + props.codes.join('\n') + '\n'], {
    type: 'text/plain;charset=utf-8',
  })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = 'filebackuptool-recovery-codes.txt'
  a.click()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="title"
    width="520px"
    :close-on-click-modal="false"
    :close-on-press-escape="false"
  >
    <el-alert
      type="warning"
      :closable="false"
      show-icon
      title="这些恢复码只会显示这一次。丢失两步验证设备时，只能靠它们登录。"
    />

    <ol class="codes">
      <li v-for="code in codes" :key="code" class="fbt-mono">{{ code }}</li>
    </ol>

    <div class="codes__actions">
      <el-button size="small" :icon="CopyDocument" @click="copyAll">复制全部</el-button>
      <el-button size="small" :icon="Download" @click="download">下载为 txt</el-button>
    </div>

    <el-checkbox v-model="acknowledged" class="codes__ack">
      我已妥善保存这些恢复码，并理解它们不会再显示
    </el-checkbox>

    <template #footer>
      <el-button type="primary" :disabled="!acknowledged" @click="visible = false">完成</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.codes {
  margin: 14px 0 0;
  padding-left: 26px;
  max-height: 260px;
  overflow-y: auto;
}

.codes li {
  padding: 3px 0;
  letter-spacing: 1px;
}

.codes__actions {
  display: flex;
  gap: 8px;
  margin-top: 12px;
}

.codes__ack {
  margin-top: 12px;
}
</style>
