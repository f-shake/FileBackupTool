<script setup lang="ts">
/**
 * 可视化筛选规则编辑器（07 §4.1）。
 *
 * 结构沿用旧界面：**白名单/黑名单 × 文件/文件夹/路径** 共 6 个多行文本框，可切正则。
 * 说明文案沿用旧的语义（文档里只给了截断的前半句，这里按语义补全 —— 见下方 filterHelp）。
 * 新增能力：**「使用当前目录测试」** 返回文件数量与文件大小（对等清单要求）。
 */
import { computed, ref } from 'vue'
import { MagicStick } from '@element-plus/icons-vue'
import { asApiError, fsApi } from '@/api'
import type { FileFilterRule, FilterRuleGroup, FilterTestResult, NormalizedApiError } from '@/api/types'
import { countConditions } from '@/utils/filter'
import { formatBytes, formatCount } from '@/utils/format'
import ErrorState from './ErrorState.vue'

const props = withDefaults(
  defineProps<{
    modelValue: FileFilterRule
    /** 测试要用到的源目录。为空时禁用测试按钮。 */
    sourceDir?: string
  }>(),
  { sourceDir: '' },
)

const emit = defineEmits<{ 'update:modelValue': [value: FileFilterRule] }>()

/** 沿用旧的说明文案（07 §4.1 引用了它的前半句） */
const filterHelp =
  '每一组文本框中，可以输入多行匹配条件，任意一条匹配成功则认为该组匹配成功。' +
  '白名单用于限定「要备份什么」，全部留空表示不限制；黑名单用于排除「不要备份什么」，优先级高于白名单。' +
  '文件组匹配文件名（含扩展名），文件夹组匹配目录名，路径组匹配相对于源目录的相对路径。' +
  '不使用正则时，条件按通配符解释（* 匹配任意字符，? 匹配单个字符）。'

const testVisible = ref(false)
const testing = ref(false)
const testResult = ref<FilterTestResult | null>(null)
const testError = ref<NormalizedApiError | null>(null)

const conditionCount = computed(() => countConditions(props.modelValue))

function patch(patchValue: Partial<FileFilterRule>): void {
  emit('update:modelValue', { ...props.modelValue, ...patchValue })
}

function patchGroup(which: 'whitelist' | 'blacklist', key: keyof FilterRuleGroup, value: string): void {
  const group = { ...props.modelValue[which], [key]: value }
  patch({ [which]: group } as Partial<FileFilterRule>)
}

async function runTest(): Promise<void> {
  if (!props.sourceDir) return
  testing.value = true
  testError.value = null
  testResult.value = null
  try {
    testResult.value = await fsApi.testFilter({
      sourceDir: props.sourceDir,
      filterJson: countConditions(props.modelValue) === 0 ? null : JSON.stringify(props.modelValue),
    })
  } catch (err) {
    testError.value = asApiError(err)
  } finally {
    testing.value = false
  }
}

const groups: { key: 'whitelist' | 'blacklist'; title: string; type: 'success' | 'danger'; hint: string }[] = [
  { key: 'whitelist', title: '白名单（限定要备份的范围）', type: 'success', hint: '全部留空表示不限制' },
  { key: 'blacklist', title: '黑名单（排除不要备份的内容）', type: 'danger', hint: '优先级高于白名单' },
]
</script>

<template>
  <div class="filter-editor">
    <el-alert type="info" :closable="false" show-icon class="filter-editor__help">
      <template #title>{{ filterHelp }}</template>
    </el-alert>

    <div class="filter-editor__switch">
      <el-switch
        :model-value="modelValue.useRegex"
        active-text="使用正则表达式"
        inactive-text="使用通配符"
        @update:model-value="patch({ useRegex: Boolean($event) })"
      />
      <span class="fbt-muted">已填写 {{ conditionCount }} 条条件</span>
      <div class="fbt-spacer" />
      <el-tooltip
        :content="sourceDir ? '在源目录上实际跑一遍规则' : '请先填写「需要备份的目录」'"
        placement="top"
      >
        <span>
          <el-button
            size="small"
            :icon="MagicStick"
            :disabled="!sourceDir"
            @click="testVisible = true"
          >
            使用当前目录测试
          </el-button>
        </span>
      </el-tooltip>
    </div>

    <el-row :gutter="12">
      <el-col v-for="group in groups" :key="group.key" :xs="24" :md="12">
        <el-card shadow="never" class="filter-editor__card">
          <template #header>
            <div class="filter-editor__card-head">
              <el-tag :type="group.type" size="small" effect="plain">{{ group.title }}</el-tag>
              <span class="fbt-muted">{{ group.hint }}</span>
            </div>
          </template>
          <el-form-item label="文件">
            <el-input
              type="textarea"
              :rows="3"
              :model-value="modelValue[group.key].file"
              placeholder="每行一条，例如&#10;*.docx&#10;*.xlsx"
              @update:model-value="patchGroup(group.key, 'file', $event)"
            />
          </el-form-item>
          <el-form-item label="文件夹">
            <el-input
              type="textarea"
              :rows="2"
              :model-value="modelValue[group.key].folder"
              placeholder="每行一条，例如&#10;~临时"
              @update:model-value="patchGroup(group.key, 'folder', $event)"
            />
          </el-form-item>
          <el-form-item label="路径">
            <el-input
              type="textarea"
              :rows="2"
              :model-value="modelValue[group.key].path"
              placeholder="每行一条，匹配相对路径，例如&#10;报告\*"
              @update:model-value="patchGroup(group.key, 'path', $event)"
            />
          </el-form-item>
        </el-card>
      </el-col>
    </el-row>

    <el-dialog v-model="testVisible" title="使用当前目录测试" width="720px" append-to-body>
      <div class="filter-editor__test-head">
        <span>测试目录：</span><span class="fbt-mono">{{ sourceDir || '（未填写）' }}</span>
      </div>

      <ErrorState v-if="testError" :error="testError" compact @retry="runTest" />

      <div v-else-if="testing" class="filter-editor__test-loading">
        <el-skeleton :rows="3" animated />
        <el-alert
          type="warning"
          :closable="false"
          show-icon
          title="正在枚举目录。百万级目录下这一步可能很慢 —— 这是全量枚举，不会使用索引。"
          class="filter-editor__test-warn"
        />
      </div>

      <template v-else-if="testResult">
        <el-descriptions :column="3" border size="small" class="fbt-section">
          <el-descriptions-item label="匹配文件数">
            {{ formatCount(testResult.fileCount) }}
          </el-descriptions-item>
          <el-descriptions-item label="匹配文件总大小">
            {{ formatBytes(testResult.totalBytes) }}
          </el-descriptions-item>
          <el-descriptions-item label="耗时">
            {{ testResult.elapsedMs }} ms
          </el-descriptions-item>
        </el-descriptions>

        <el-alert
          v-if="testResult.skippedDirCount > 0"
          type="warning"
          :closable="false"
          show-icon
          :title="`有 ${testResult.skippedDirCount} 个目录被跳过（无权限或不可访问），这些目录的内容不在统计内。`"
          class="fbt-section"
        />

        <div class="fbt-footnote">
          匹配文件列表（共 {{ formatCount(testResult.fileCount) }} 项<template v-if="testResult.truncated">，仅显示前 {{ testResult.files.length }} 项</template>）
        </div>
        <el-table :data="testResult.files" size="small" max-height="320" border>
          <el-table-column label="相对路径" min-width="300">
            <template #default="{ row }">
              <span class="fbt-mono">{{ row.relativePath }}</span>
            </template>
          </el-table-column>
          <el-table-column label="大小" width="120" align="right">
            <template #default="{ row }">{{ formatBytes(row.length) }}</template>
          </el-table-column>
        </el-table>
      </template>

      <el-empty v-else description="点击「开始测试」在源目录上跑一遍规则">
        <el-button type="primary" @click="runTest">开始测试</el-button>
      </el-empty>

      <template #footer>
        <el-button @click="testVisible = false">关闭</el-button>
        <el-button type="primary" :loading="testing" @click="runTest">重新测试</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.filter-editor__help {
  margin-bottom: 10px;
  --el-alert-description-font-size: 12px;
}

.filter-editor__switch {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 10px;
}

.filter-editor__card {
  margin-bottom: 12px;
}

.filter-editor__card-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.filter-editor__card :deep(.el-form-item) {
  margin-bottom: 10px;
}

.filter-editor__card :deep(.el-form-item__label) {
  width: 62px;
}

.filter-editor__test-head {
  margin-bottom: 10px;
  font-size: 13px;
}

.filter-editor__test-warn {
  margin-top: 10px;
}

.filter-editor__test-loading {
  padding: 8px 0;
}
</style>
