<script setup lang="ts">
/**
 * 任务编辑表单（07 §4.1）。
 *
 * 修正了旧界面的两个语义错误：
 *   1. 定时备份用**分钟数输入**，不是 TimePicker（旧界面选"时刻"，语义错误）
 *   2. 新增**任务级启用开关**（旧设计只能用两个子开关间接控制）
 * 目录关系实时校验：源/备份目录变化时调用 `POST /api/tasks/validate-dirs`。
 */
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { asApiError } from '@/api'
import type { BackupTask, BackupTaskInput, DirValidationIssue, FileFilterRule } from '@/api/types'
import DirPicker from '@/components/DirPicker.vue'
import FilterRuleEditor from '@/components/FilterRuleEditor.vue'
import { useTasksStore } from '@/stores/tasks'
import { emptyFilterRule, parseFilterRule, serializeFilterRule } from '@/utils/filter'

const props = defineProps<{
  modelValue: boolean
  /** 传入表示编辑，不传表示新建 */
  task?: BackupTask | null
}>()

const emit = defineEmits<{ 'update:modelValue': [value: boolean]; saved: [task: BackupTask] }>()

const tasksStore = useTasksStore()

const form = ref<BackupTaskInput>(blank())
const filterRule = ref<FileFilterRule>(emptyFilterRule())
const saving = ref(false)
const activeTab = ref('basic')

const issues = ref<DirValidationIssue[]>([])
const validating = ref(false)
const validateError = ref<string | null>(null)

const isEdit = computed(() => Boolean(props.task))
const title = computed(() => (isEdit.value ? `编辑任务：${props.task?.name ?? ''}` : '新建任务'))

const dirsOk = computed(() => issues.value.length === 0)

function blank(): BackupTaskInput {
  return {
    name: '',
    sourceDir: '',
    backupDir: '',
    filterJson: null,
    byTimeInterval: false,
    timeIntervalMinutes: 120,
    maxAutoIncrementBackupCount: 10,
    isDefaultVirtualBackup: false,
    byWatching: false,
    isEnabled: true,
  }
}

function reset(): void {
  if (props.task) {
    const t = props.task
    form.value = {
      name: t.name,
      sourceDir: t.sourceDir,
      backupDir: t.backupDir,
      filterJson: t.filterJson,
      byTimeInterval: t.byTimeInterval,
      timeIntervalMinutes: t.timeIntervalMinutes,
      maxAutoIncrementBackupCount: t.maxAutoIncrementBackupCount,
      isDefaultVirtualBackup: t.isDefaultVirtualBackup,
      byWatching: t.byWatching,
      isEnabled: t.isEnabled,
    }
    filterRule.value = parseFilterRule(t.filterJson)
  } else {
    form.value = blank()
    filterRule.value = emptyFilterRule()
  }
  issues.value = []
  validateError.value = null
  activeTab.value = 'basic'
}

watch(
  () => props.modelValue,
  (visible) => {
    if (visible) {
      reset()
      void runValidate()
    }
  },
)

watch(
  () => [form.value.sourceDir, form.value.backupDir],
  () => {
    if (props.modelValue) void runValidateDebounced()
  },
)

let validateTimer: ReturnType<typeof setTimeout> | null = null
function runValidateDebounced(): void {
  if (validateTimer) clearTimeout(validateTimer)
  validateTimer = setTimeout(() => void runValidate(), 400)
}

/**
 * 目录关系预校验（02 §7 五条规则）。
 * 校验本身失败（接口不存在/网络错误）时**不能**当成"通过"——必须让用户看见。
 */
async function runValidate(): Promise<void> {
  if (!form.value.sourceDir || !form.value.backupDir) {
    issues.value = []
    validateError.value = null
    return
  }
  validating.value = true
  validateError.value = null
  try {
    const result = await tasksStore.validateDirs({
      taskId: props.task?.id ?? null,
      sourceDir: form.value.sourceDir,
      backupDir: form.value.backupDir,
    })
    issues.value = result.issues ?? []
  } catch (err) {
    issues.value = []
    validateError.value = asApiError(err).message
  } finally {
    validating.value = false
  }
}

async function submit(): Promise<void> {
  if (!form.value.name.trim()) {
    ElMessage.warning('请填写任务名')
    return
  }
  if (!form.value.sourceDir.trim() || !form.value.backupDir.trim()) {
    ElMessage.warning('请填写需要备份的目录与备份文件存放目录')
    activeTab.value = 'basic'
    return
  }
  if (issues.value.length > 0) {
    ElMessage.error('目录关系校验未通过，请先修正')
    activeTab.value = 'dirs'
    return
  }

  saving.value = true
  try {
    const payload: BackupTaskInput = {
      ...form.value,
      filterJson: serializeFilterRule(filterRule.value),
    }
    const saved = props.task
      ? await tasksStore.update(props.task.id, payload)
      : await tasksStore.create(payload)
    ElMessage.success(isEdit.value ? '已保存' : '已创建')
    emit('saved', saved)
    emit('update:modelValue', false)
  } catch (err) {
    // 后端会返回结构化错误码（如 TASK_DIR_CONFLICT），直接展示
    ElMessage.error(asApiError(err).message)
  } finally {
    saving.value = false
  }
}

function close(): void {
  emit('update:modelValue', false)
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="title"
    width="820px"
    top="6vh"
    append-to-body
    :close-on-click-modal="false"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <el-tabs v-model="activeTab">
      <el-tab-pane label="基本" name="basic">
        <el-form label-width="150px" label-position="right">
          <el-form-item label="任务名" required>
            <el-input v-model="form.name" maxlength="200" show-word-limit placeholder="例如：文档备份" />
          </el-form-item>

          <el-form-item label="启用">
            <el-switch v-model="form.isEnabled" active-text="启用" inactive-text="禁用" />
            <div class="editor__hint">
              任务级总开关（旧设计缺失，只能用两个子开关间接控制）。禁用后不会定时触发，也不会响
              应监控到的变更，但手动「立即备份」仍可用。
            </div>
          </el-form-item>

          <el-form-item label="筛选规则">
            <div class="editor__summary">
              已配置 {{ filterRule.whitelist.file || filterRule.blacklist.file ? '自定义' : '默认' }} 规则
              <el-button link type="primary" size="small" @click="activeTab = 'filter'">
                去编辑
              </el-button>
            </div>
          </el-form-item>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="目录" name="dirs">
        <el-form label-width="150px" label-position="right">
          <el-form-item label="需要备份的目录" required>
            <DirPicker v-model="form.sourceDir" placeholder="D:\Documents" />
          </el-form-item>

          <el-form-item label="备份文件存放目录" required>
            <DirPicker v-model="form.backupDir" placeholder="E:\Backup\Documents" />
          </el-form-item>

          <el-alert
            v-if="validateError"
            type="warning"
            :closable="false"
            show-icon
            class="fbt-section"
            :title="`目录关系校验接口不可用：${validateError}`"
            description="保存时后端仍会强制校验，这里只是少了实时反馈。"
          />

          <el-alert
            v-else-if="issues.length > 0"
            type="error"
            :closable="false"
            show-icon
            class="fbt-section"
            title="目录关系校验未通过"
          >
            <ul class="editor__issues">
              <li v-for="(issue, i) in issues" :key="i">
                <el-tag size="small" type="danger" effect="plain">{{ issue.code }}</el-tag>
                {{ issue.message }}
                <div v-if="issue.pathA || issue.pathB" class="fbt-mono fbt-muted">
                  {{ issue.pathA }}<span v-if="issue.kind"> / {{ issue.kind }} / </span>{{ issue.pathB }}
                </div>
              </li>
            </ul>
          </el-alert>

          <el-alert
            v-else-if="validating"
            type="info"
            :closable="false"
            title="正在校验目录关系…"
          />

          <el-alert
            v-else-if="form.sourceDir && form.backupDir"
            type="success"
            :closable="false"
            show-icon
            title="目录关系校验通过"
          />

          <div class="editor__hint">
            校验规则（02 §7）：源目录 ≠ 备份目录；备份目录不得位于源目录之内；源目录不得位于备份目录之内；
            两个任务的备份目录不得相同或互相包含；还原根目录不得位于任何备份目录之内。
            其中「备份目录位于源目录之内」最容易犯且后果最严重 —— 它会与实时监控叠加成自我触发的死循环。
          </div>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="调度与监控" name="schedule">
        <el-form label-width="150px" label-position="right">
          <el-form-item label="定时备份">
            <el-switch v-model="form.byTimeInterval" />
          </el-form-item>

          <el-form-item label="备份间隔（分钟）">
            <el-input-number
              v-model="form.timeIntervalMinutes"
              :min="1"
              :max="10080"
              :disabled="!form.byTimeInterval"
              controls-position="right"
            />
            <span class="editor__inline-hint">
              修正项：旧界面用 TimePicker 选「时刻」，语义错误 —— 这里就是间隔分钟数。
            </span>
          </el-form-item>

          <el-form-item label="增量备份最大数量">
            <el-input-number
              v-model="form.maxAutoIncrementBackupCount"
              :min="1"
              :max="9999"
              controls-position="right"
            />
            <div class="editor__hint">
              当一个连续的增量备份超过该数量后，将进行一次全量备份。
            </div>
          </el-form-item>

          <el-form-item label="自动全量时使用虚拟全量">
            <el-switch v-model="form.isDefaultVirtualBackup" />
            <div class="editor__hint">
              虚拟全量不复制任何文件，只在数据库中开一条新的增量链。注意：虚拟全量中的文件没有实际
              备份文件，若对应 blob 已被回收则无法还原。
            </div>
          </el-form-item>

          <el-form-item label="变更监控">
            <el-switch v-model="form.byWatching" />
            <div class="editor__hint">
              旧的 <code>ByWatching</code> 是死配置，新设计真正实现。UNC/NAS 源无法使用
              FileSystemWatcher 时会自动转为轮询模式，界面上会显示「轮询模式」。
            </div>
          </el-form-item>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="筛选规则" name="filter">
        <FilterRuleEditor v-model="filterRule" :source-dir="form.sourceDir" />
      </el-tab-pane>
    </el-tabs>

    <template #footer>
      <el-button @click="close">取消</el-button>
      <el-button
        v-if="activeTab !== 'basic'"
        @click="activeTab = activeTab === 'dirs' ? 'schedule' : 'basic'"
      >
        上一步
      </el-button>
      <el-button type="primary" :loading="saving" :disabled="issues.length > 0" @click="submit">
        保存
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.editor__hint {
  font-size: 12px;
  line-height: 1.7;
  color: var(--el-text-color-secondary);
  margin-top: 4px;
  width: 100%;
}

.editor__inline-hint {
  margin-left: 10px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.editor__issues {
  margin: 4px 0 0;
  padding-left: 18px;
  line-height: 1.9;
}

.editor__summary {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>
