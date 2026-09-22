<script setup lang="ts">
/**
 * 长路径显示：中段省略 + hover 显示全路径（07 §7）。
 * 路径很容易超过容器宽度，因此这是全局约定而不是个别页面的美化。
 */
import { computed } from 'vue'
import { ellipsisPath } from '@/utils/format'

const props = withDefaults(
  defineProps<{
    path: string | null | undefined
    /** 省略后的最大字符数 */
    maxLength?: number
    /** 是否用等宽字体 */
    mono?: boolean
  }>(),
  { maxLength: 48, mono: true },
)

const display = computed(() => ellipsisPath(props.path, props.maxLength))
const full = computed(() => props.path ?? '')
</script>

<template>
  <span class="path-text fbt-ellipsis" :class="{ 'fbt-mono': mono }" :title="full">
    {{ display }}
  </span>
</template>

<style scoped>
.path-text {
  display: inline-block;
  max-width: 100%;
  vertical-align: bottom;
}
</style>
