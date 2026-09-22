<script setup lang="ts">
/**
 * 404（07 §2 的兜底路由 `/:pathMatch(.*)*`）。
 * 不做成白屏 —— 明确告诉用户路由不存在，并给出回到任务列表的出口。
 */
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, House } from '@element-plus/icons-vue'

const route = useRoute()
const router = useRouter()
</script>

<template>
  <div class="fbt-page notfound">
    <el-result icon="warning" title="页面不存在" :sub-title="`找不到路径 ${route.fullPath}`">
      <template #extra>
        <el-button type="primary" :icon="House" @click="router.push({ name: 'tasks' })">
          回到任务列表
        </el-button>
        <el-button :icon="ArrowLeft" @click="router.back()">返回上一页</el-button>
      </template>
    </el-result>
  </div>
</template>

<style scoped>
.notfound {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 60vh;
}
</style>
