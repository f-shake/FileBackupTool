<script setup lang="ts">
/**
 * 右键菜单（07 §3 补齐项 5）。
 *
 * 旧界面只有 `复制信息` 下拉，没有右键菜单。
 * 旧界面的「打开文件 / 打开所在文件夹」在这里改为**服务端打开所在目录** ——
 * 文件在服务器上，浏览器无法打开它（07 §5 对等清单明确说明）。
 */
import { onBeforeUnmount, onMounted, ref } from 'vue'
import {
  CopyDocument,
  Document,
  FolderOpened,
  Tickets,
} from '@element-plus/icons-vue'

export interface ContextMenuItem {
  key: string
  label: string
  icon?: 'copy-name' | 'copy-path' | 'copy-hash' | 'restore' | 'history' | 'open-dir'
  divided?: boolean
  disabled?: boolean
}

const visible = ref(false)
const x = ref(0)
const y = ref(0)
const items = ref<ContextMenuItem[]>([])

const emit = defineEmits<{ select: [key: string] }>()

/** 外部通过 ref 调用：在鼠标位置打开菜单 */
function open(event: MouseEvent, menuItems: ContextMenuItem[]): void {
  event.preventDefault()
  items.value = menuItems
  x.value = Math.min(event.clientX, window.innerWidth - 200)
  y.value = Math.min(event.clientY, window.innerHeight - menuItems.length * 34 - 16)
  visible.value = true
}

function close(): void {
  visible.value = false
}

function onSelect(item: ContextMenuItem): void {
  if (item.disabled) return
  close()
  emit('select', item.key)
}

function onGlobalClick(): void {
  if (visible.value) close()
}

onMounted(() => {
  window.addEventListener('click', onGlobalClick)
  window.addEventListener('scroll', onGlobalClick, true)
})

onBeforeUnmount(() => {
  window.removeEventListener('click', onGlobalClick)
  window.removeEventListener('scroll', onGlobalClick, true)
})

const iconMap = {
  'copy-name': CopyDocument,
  'copy-path': CopyDocument,
  'copy-hash': CopyDocument,
  restore: Document,
  history: Tickets,
  'open-dir': FolderOpened,
} as const

/** 用函数取图标，避免模板里对可选字段的窄化问题 */
function iconOf(item: ContextMenuItem) {
  return item.icon ? iconMap[item.icon] : null
}

defineExpose({ open, close })
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      class="ctx-menu"
      :style="{ left: `${x}px`, top: `${y}px` }"
      @click.stop
      @contextmenu.prevent
    >
      <template v-for="item in items" :key="item.key">
        <div v-if="item.divided" class="ctx-menu__divider" />
        <div
          class="ctx-menu__item"
          :class="{ 'is-disabled': item.disabled }"
          @click="onSelect(item)"
        >
          <el-icon v-if="iconOf(item)" class="ctx-menu__icon">
            <component :is="iconOf(item)" />
          </el-icon>
          <span>{{ item.label }}</span>
        </div>
      </template>
    </div>
  </Teleport>
</template>

<style scoped>
.ctx-menu {
  position: fixed;
  z-index: 4000;
  min-width: 180px;
  padding: 4px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 6px;
  background-color: var(--el-bg-color-overlay);
  box-shadow: var(--el-box-shadow-light);
  font-size: 13px;
}

.ctx-menu__item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  border-radius: 4px;
  cursor: pointer;
  color: var(--el-text-color-regular);
}

.ctx-menu__item:hover {
  background-color: var(--el-fill-color-light);
  color: var(--el-color-primary);
}

.ctx-menu__item.is-disabled {
  color: var(--el-text-color-disabled);
  cursor: not-allowed;
  background-color: transparent;
}

.ctx-menu__icon {
  font-size: 14px;
}

.ctx-menu__divider {
  height: 1px;
  margin: 4px 6px;
  background-color: var(--el-border-color-lighter);
}
</style>
