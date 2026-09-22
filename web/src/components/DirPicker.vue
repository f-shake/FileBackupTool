<script setup lang="ts">
/**
 * 目录选择器（07 §4.1）：**手输 + 服务端目录浏览 + 拖放**。
 *
 * 关于拖放的实际限制（已在实现中体现，不是遗漏）：
 * 前端跑在浏览器里，源目录与备份目录都是**服务器上的路径**。浏览器出于安全原因不会
 * 把本地拖入文件夹的真实路径交给页面（`File.path` 是 Electron 专有扩展）。因此这里的
 * 拖放支持的是「拖入一段路径文本」（例如从别处拖一个路径过来），对拖入的本地文件夹会
 * 明确提示改用服务端目录浏览 —— 而不是静默忽略或假装成功。
 */
import { computed, ref, watch } from 'vue'
import { FolderOpened, Upload } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { asApiError, fsApi } from '@/api'
import type { FsEntry, FsRoot, NormalizedApiError } from '@/api/types'
import ErrorState from './ErrorState.vue'
import StatePlaceholder from './StatePlaceholder.vue'

const props = withDefaults(
  defineProps<{
    modelValue: string
    placeholder?: string
    disabled?: boolean
    /** 允许拖放。表单里的目录字段默认开启。 */
    droppable?: boolean
  }>(),
  { placeholder: '例如 D:\\Documents', disabled: false, droppable: true },
)

const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const dialogVisible = ref(false)
const dragging = ref(false)

const roots = ref<FsRoot[]>([])
const entries = ref<FsEntry[]>([])
const currentPath = ref('')
const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)

const segments = computed(() => {
  const p = currentPath.value
  if (!p) return []
  const parts = p.split('\\').filter(Boolean)
  const out: { label: string; path: string }[] = []
  let acc = ''
  for (const part of parts) {
    acc = acc ? `${acc}\\${part}` : part.endsWith(':') ? `${part}\\` : part
    out.push({ label: part, path: acc })
  }
  return out
})

const parentPath = computed(() => {
  const p = currentPath.value.replace(/\\+$/, '')
  const idx = p.lastIndexOf('\\')
  if (idx < 0) return ''
  const parent = p.slice(0, idx)
  return parent.endsWith(':') ? `${parent}\\` : parent
})

async function open(): Promise<void> {
  dialogVisible.value = true
  currentPath.value = props.modelValue || ''
  await Promise.all([loadRoots(), currentPath.value ? browse(currentPath.value) : Promise.resolve()])
}

async function loadRoots(): Promise<void> {
  try {
    roots.value = await fsApi.listFsRoots()
  } catch (err) {
    error.value = asApiError(err)
  }
}

async function browse(path: string): Promise<void> {
  loading.value = true
  error.value = null
  try {
    entries.value = await fsApi.listFsDirs(path)
    currentPath.value = path
  } catch (err) {
    error.value = asApiError(err)
    entries.value = []
  } finally {
    loading.value = false
  }
}

function chooseCurrent(): void {
  if (!currentPath.value) {
    ElMessage.warning('请先选择一个目录')
    return
  }
  emit('update:modelValue', currentPath.value)
  dialogVisible.value = false
}

function chooseEntry(entry: FsEntry): void {
  void browse(entry.path)
}

function onDrop(event: DragEvent): void {
  dragging.value = false
  if (!props.droppable || props.disabled) return

  const text = event.dataTransfer?.getData('text/plain')?.trim()
  if (text && /^[a-zA-Z]:\\|^\\\\|^\//.test(text)) {
    emit('update:modelValue', text)
    ElMessage.success('已填入拖入的路径')
    return
  }

  const items = event.dataTransfer?.items
  if (items && items.length > 0) {
    ElMessage.warning(
      '浏览器不会把本地拖入文件夹的真实路径交给页面（源/备份目录都是服务器上的路径）。请使用「浏览」从服务端选择。',
    )
    return
  }

  ElMessage.warning('未能从拖入内容中识别出路径')
}

watch(
  () => props.modelValue,
  (v) => {
    if (!dialogVisible.value) currentPath.value = v
  },
)
</script>

<template>
  <div
    class="dir-picker"
    :class="{ 'is-dragging': dragging, 'is-disabled': disabled }"
    @dragover.prevent="droppable && !disabled && (dragging = true)"
    @dragleave.prevent="dragging = false"
    @drop.prevent="onDrop"
  >
    <el-input
      :model-value="modelValue"
      :placeholder="placeholder"
      :disabled="disabled"
      clearable
      @update:model-value="emit('update:modelValue', $event)"
    >
      <template #append>
        <el-button :icon="FolderOpened" :disabled="disabled" @click="open">浏览</el-button>
      </template>
    </el-input>
    <div v-if="droppable" class="dir-picker__hint">
      <el-icon><Upload /></el-icon>
      可以拖入一段路径文本粘贴到这里
    </div>

    <el-dialog v-model="dialogVisible" title="选择服务器上的目录" width="640px" append-to-body>
      <div class="dir-picker__roots">
        <span class="fbt-muted">根：</span>
        <el-button
          v-for="root in roots"
          :key="root.path"
          size="small"
          :type="currentPath === root.path ? 'primary' : 'default'"
          :disabled="!root.ready"
          @click="browse(root.path)"
        >
          {{ root.name }}<span v-if="!root.ready" class="fbt-muted">（未就绪）</span>
        </el-button>
        <el-button v-if="roots.length === 0" size="small" text @click="loadRoots">加载根目录</el-button>
      </div>

      <el-breadcrumb separator="\" class="dir-picker__crumbs">
        <el-breadcrumb-item>
          <el-link :underline="false" @click="browse(currentPath.split('\\')[0] + '\\')">根</el-link>
        </el-breadcrumb-item>
        <el-breadcrumb-item v-for="seg in segments" :key="seg.path">
          <el-link :underline="false" @click="browse(seg.path)">{{ seg.label }}</el-link>
        </el-breadcrumb-item>
      </el-breadcrumb>

      <div class="dir-picker__current fbt-mono">
        <span class="fbt-muted">当前：</span>{{ currentPath || '（未选择）' }}
      </div>

      <ErrorState v-if="error" :error="error" compact @retry="currentPath ? browse(currentPath) : loadRoots()" />
      <StatePlaceholder v-else-if="loading" text="正在读取目录…" />
      <StatePlaceholder
        v-else-if="entries.length === 0"
        empty
        description="该目录下没有子目录（接口只返回目录，不返回文件）"
      />
      <el-scrollbar v-else height="280px">
        <div
          v-for="entry in entries"
          :key="entry.path"
          class="dir-picker__entry"
          @dblclick="chooseEntry(entry)"
        >
          <el-icon><FolderOpened /></el-icon>
          <span class="dir-picker__entry-name fbt-ellipsis">{{ entry.name }}</span>
          <span class="fbt-muted fbt-mono">{{ entry.lastWriteTimeUtc?.slice(0, 10) ?? '' }}</span>
          <el-button size="small" text type="primary" @click="chooseEntry(entry)">进入</el-button>
        </div>
      </el-scrollbar>

      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button @click="currentPath = parentPath" :disabled="!parentPath">上一级</el-button>
        <el-button type="primary" @click="chooseCurrent">选择此目录</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.dir-picker {
  width: 100%;
}

.dir-picker.is-dragging :deep(.el-input__wrapper) {
  box-shadow: 0 0 0 1px var(--el-color-primary) inset;
}

.dir-picker__hint {
  margin-top: 4px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  display: flex;
  align-items: center;
  gap: 4px;
}

.dir-picker__roots {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}

.dir-picker__crumbs {
  margin-bottom: 6px;
}

.dir-picker__current {
  font-size: 12px;
  padding: 6px 8px;
  background-color: var(--el-fill-color-light);
  border-radius: 4px;
  margin-bottom: 8px;
  word-break: break-all;
}

.dir-picker__entry {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 6px;
  border-radius: 4px;
  cursor: pointer;
}

.dir-picker__entry:hover {
  background-color: var(--el-fill-color-light);
}

.dir-picker__entry-name {
  flex: 1 1 auto;
  min-width: 0;
}
</style>
