<script setup lang="ts">
/**
 * 快照文件浏览 `/tasks/:id/snapshots/:sid`（07 §4.2）。
 *
 * **核心页面**，独立路由（便于直接发链接与浏览器前进后退）。三栏：
 *   左：快照导航（按全量分组的时间轴）
 *   中：路径面包屑 + 搜索（真过滤）+ 表格（**服务端分页**）
 *   右：选中文件的元数据 + 版本历史
 *
 * 三个必须体现的设计决定：
 *   1. 中间表格是**服务端分页的目录导航**，不是一次性加载整棵树 —— 百万级目录下旧做法跑不起来
 *      （07 §5 明确说明这是唯一的能力取舍）。
 *   2. `备份文件名` 与 `文件哈希` 两列**合并为一列** —— 内容寻址后 blob 名就是哈希。
 *   3. 只显示文件名与元数据，**不做内容预览**。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  ArrowLeft,
  ArrowRight,
  Document,
  Download,
  Folder,
  FolderOpened,
  HomeFilled,
  Refresh,
  Search,
} from '@element-plus/icons-vue'
import { asApiError, filesApi, snapshotsApi, tasksApi } from '@/api'
import type {
  BackupTask,
  FileHistoryResult,
  MirrorEntry,
  MirrorListing,
  NormalizedApiError,
  Snapshot,
  SnapshotGroup,
  SnapshotType,
} from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import FileContextMenu, { type ContextMenuItem } from '@/components/FileContextMenu.vue'
import PathText from '@/components/PathText.vue'
import RestoreWizard from '@/components/RestoreWizard.vue'
import SnapshotTypeTag from '@/components/SnapshotTypeTag.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import { copyText } from '@/composables/useClipboard'
import { snapshotStatusLabels, snapshotStatusTagType } from '@/locales/zh-CN'
import { useRealtimeStore } from '@/stores/realtime'
import { formatBytes, formatCount, shortHash } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const props = defineProps<{ id: string; sid: string }>()

const router = useRouter()
const realtime = useRealtimeStore()

const snapshotId = computed(() => Number(props.sid))

/* ------------------------------- 左栏：快照导航 ------------------------------- */

const groups = ref<SnapshotGroup[]>([])
const groupsLoading = ref(false)
const groupsError = ref<NormalizedApiError | null>(null)

const currentSnapshot = ref<Snapshot | null>(null)
const snapshotDetailError = ref<NormalizedApiError | null>(null)

/** 平铺后的导航用列表（全量 + 其后的增量），最新的在最上 */
const navItems = computed(() => {
  const out: { snapshot: Snapshot; isGroupHead: boolean }[] = []
  for (const g of groups.value) {
    const list = [...g.snapshots].sort(
      (a, b) => new Date(b.beginTime).getTime() - new Date(a.beginTime).getTime(),
    )
    for (const s of list) {
      out.push({ snapshot: s, isGroupHead: s.type !== 'Increment' })
    }
  }
  return out
})

async function loadSnapshots(): Promise<void> {
  groupsLoading.value = true
  groupsError.value = null
  try {
    const result = await snapshotsApi.listSnapshots(props.id, { page: 1, pageSize: 200 })
    groups.value = result.items
  } catch (err) {
    groupsError.value = asApiError(err)
    groups.value = []
  } finally {
    groupsLoading.value = false
  }
}

const task = ref<BackupTask | null>(null)

async function loadTask(): Promise<void> {
  try {
    task.value = await tasksApi.getTask(props.id)
  } catch {
    // 任务信息只用于标题，失败不必打断页面
    task.value = null
  }
}

/* ------------------------------- 中栏：镜像浏览 ------------------------------- */

const listing = ref<MirrorListing | null>(null)
const listingLoading = ref(false)
const listingError = ref<NormalizedApiError | null>(null)
const currentPath = ref('')
const page = ref(1)
const pageSize = ref(100)
const keyword = ref('')
const searchMode = ref(false)
const searchLoading = ref(false)
const searchError = ref<NormalizedApiError | null>(null)
const searchResults = ref<MirrorEntry[]>([])
const searchTotal = ref(0)

const selectedRows = ref<MirrorEntry[]>([])

const breadcrumbs = computed(() => {
  const p = currentPath.value
  if (!p) return []
  const parts = p.split('/').filter(Boolean)
  const out: { label: string; path: string }[] = []
  let acc = ''
  for (const part of parts) {
    acc = acc ? `${acc}/${part}` : part
    out.push({ label: part, path: acc })
  }
  return out
})

const displayEntries = computed(() => (searchMode.value ? searchResults.value : listing.value?.items ?? []))
const displayTotal = computed(() => (searchMode.value ? searchTotal.value : listing.value?.total ?? 0))

const canRestoreSelection = computed(() =>
  selectedRows.value.some((r) => !r.isDirectory && r.restorable !== false),
)

async function loadListing(path: string, keepSelection = false): Promise<void> {
  listingLoading.value = true
  listingError.value = null
  if (!keepSelection) selectedRows.value = []
  try {
    listing.value = await snapshotsApi.listMirror(snapshotId.value, path, {
      page: page.value,
      pageSize: pageSize.value,
    })
    currentPath.value = path
  } catch (err) {
    listingError.value = asApiError(err)
    listing.value = null
  } finally {
    listingLoading.value = false
  }
}

async function runSearch(): Promise<void> {
  if (!keyword.value.trim()) {
    searchMode.value = false
    return
  }
  searchLoading.value = true
  searchError.value = null
  searchMode.value = true
  try {
    const result = await filesApi.searchMirror(props.id, {
      snapshotId: snapshotId.value,
      path: currentPath.value || undefined,
      keyword: keyword.value.trim(),
      page: 1,
      pageSize: 200,
    })
    searchResults.value = result.items
    searchTotal.value = result.total
  } catch (err) {
    searchError.value = asApiError(err)
    searchResults.value = []
    searchTotal.value = 0
  } finally {
    searchLoading.value = false
  }
}

function clearSearch(): void {
  keyword.value = ''
  searchMode.value = false
  searchResults.value = []
  searchTotal.value = 0
}

function enterDir(entry: MirrorEntry): void {
  clearSearch()
  page.value = 1
  void loadListing(entry.relativePath)
}

function goTo(path: string): void {
  clearSearch()
  page.value = 1
  void loadListing(path)
}

function goUp(): void {
  const p = currentPath.value
  const idx = p.lastIndexOf('/')
  goTo(idx < 0 ? '' : p.slice(0, idx))
}

/* ------------------------------- 右栏：详情与历史 ------------------------------- */

const detail = ref<MirrorEntry | null>(null)
const history = ref<FileHistoryResult | null>(null)
const historyLoading = ref(false)
const historyError = ref<NormalizedApiError | null>(null)

async function selectEntry(entry: MirrorEntry): Promise<void> {
  detail.value = entry
  history.value = null
  historyError.value = null
  if (entry.isDirectory) return

  historyLoading.value = true
  try {
    history.value = await filesApi.getFileHistory(props.id, entry.relativePath)
  } catch (err) {
    historyError.value = asApiError(err)
  } finally {
    historyLoading.value = false
  }
}

onMounted(async () => {
  await Promise.all([loadSnapshots(), loadTask()])
  await loadListing('')
  try {
    currentSnapshot.value = await snapshotsApi.getSnapshot(snapshotId.value)
    snapshotDetailError.value = null
  } catch (err) {
    snapshotDetailError.value = asApiError(err)
  }
})

// 切换快照 → 重置浏览状态
watch(snapshotId, async (next, prev) => {
  if (next === prev) return
  clearSearch()
  page.value = 1
  detail.value = null
  history.value = null
  await loadListing('')
  try {
    currentSnapshot.value = await snapshotsApi.getSnapshot(next)
    snapshotDetailError.value = null
  } catch (err) {
    snapshotDetailError.value = asApiError(err)
  }
})

watch(page, () => {
  if (!searchMode.value) void loadListing(currentPath.value, true)
})

// 重连/轮询后刷新（快照可能已被其它操作改动）
watch(
  () => realtime.resyncTick,
  () => {
    if (!groupsError.value) void loadSnapshots()
  },
)

/* ------------------------------- 交互：右键 / 复制 / 还原 ------------------------------- */

const ctxMenu = ref<InstanceType<typeof FileContextMenu> | null>(null)

/**
 * Element Plus 的 `row-contextmenu` 回调签名是 `(row, column, event)` ——
 * 第二个参数是列定义，不是事件对象，必须显式忽略它。
 */
function onRowContextMenu(row: MirrorEntry, _column: unknown, event: MouseEvent): void {
  if (!selectedRows.value.includes(row)) selectedRows.value = [row]
  detail.value = row
  const items: ContextMenuItem[] = [
    { key: 'restore', label: '还原此项…', icon: 'restore', disabled: row.isDirectory || row.restorable === false },
    { key: 'history', label: '查看版本历史', icon: 'history', disabled: row.isDirectory },
    { key: 'copy-name', label: '复制文件名', icon: 'copy-name', divided: true },
    { key: 'copy-path', label: '复制相对路径', icon: 'copy-path' },
    {
      key: 'copy-hash',
      label: '复制哈希',
      icon: 'copy-hash',
      disabled: !row.contentHash,
    },
    { key: 'open-dir', label: '打开所在目录（服务端）', icon: 'open-dir', divided: true },
  ]
  ctxMenu.value?.open(event, items)
}

async function onContextSelect(key: string): Promise<void> {
  const row = detail.value
  if (!row) return
  switch (key) {
    case 'restore':
      openRestore('Files', null, [row.relativePath])
      break
    case 'history':
      await selectEntry(row)
      break
    case 'copy-name':
      await doCopy(row.name, '文件名已复制')
      break
    case 'copy-path':
      await doCopy(row.relativePath, '相对路径已复制')
      break
    case 'copy-hash':
      await doCopy(row.contentHash ?? '', '哈希已复制')
      break
    case 'open-dir':
      openRestore('Directory', row.isDirectory ? row.relativePath : parentOf(row.relativePath), [])
      break
  }
}

function parentOf(path: string): string {
  const idx = path.lastIndexOf('/')
  return idx < 0 ? '' : path.slice(0, idx)
}

async function doCopy(text: string, message: string): Promise<void> {
  const ok = await copyText(text)
  if (ok) ElMessage.success(message)
  else ElMessage.warning('复制失败：浏览器拒绝了剪贴板访问')
}

/** 表格复选框变化（07 §3 补齐项 4：旧界面所有网格都是单选） */
function onSelectionChange(rows: MirrorEntry[]): void {
  selectedRows.value = rows
}

/* ------------------------------- 还原入口 ------------------------------- */

const restoreVisible = ref(false)
const restoreScope = ref<'Snapshot' | 'Directory' | 'Files'>('Snapshot')
const restoreDirectory = ref<string | null>(null)
const restorePaths = ref<string[]>([])

function openRestore(
  scope: 'Snapshot' | 'Directory' | 'Files',
  directory: string | null,
  paths: string[],
): void {
  restoreScope.value = scope
  restoreDirectory.value = directory
  restorePaths.value = paths
  restoreVisible.value = true
}

function restoreWholeSnapshot(): void {
  openRestore('Snapshot', null, [])
}

function restoreCurrentDir(): void {
  openRestore('Directory', currentPath.value, [])
}

function restoreSelection(): void {
  const paths = selectedRows.value.filter((r) => !r.isDirectory).map((r) => r.relativePath)
  if (paths.length === 0) {
    ElMessage.warning('请先勾选要还原的文件')
    return
  }
  openRestore('Files', null, paths)
}

function switchToSnapshot(target: number): void {
  void router.push({ name: 'snapshot-browser', params: { id: props.id, sid: String(target) } })
}

/** 相对当前快照的上一条 / 下一条（按导航列表的时间顺序） */
const navIndex = computed(() => navItems.value.findIndex((n) => n.snapshot.id === snapshotId.value))
const olderSnapshot = computed(() => navItems.value[navIndex.value + 1]?.snapshot ?? null)
const newerSnapshot = computed(() => navItems.value[navIndex.value - 1]?.snapshot ?? null)

function typeTagType(type: SnapshotType): 'primary' | 'warning' | 'info' {
  return type === 'Full' ? 'primary' : type === 'VirtualFull' ? 'warning' : 'info'
}
</script>

<template>
  <div class="fbt-page browser">
    <!-- 头部 -->
    <div class="fbt-page-header">
      <div class="browser__heading">
        <el-button text :icon="ArrowLeft" @click="router.push({ name: 'task-detail', params: { id } })">
          返回任务
        </el-button>
        <h1 class="fbt-page-title">
          快照文件浏览
          <span class="fbt-muted">· {{ task?.name ?? id }}</span>
        </h1>
        <div class="fbt-page-desc">
          快照 #{{ sid }}
          <template v-if="currentSnapshot">
            <SnapshotTypeTag :type="currentSnapshot.type" show-help />
            <el-tag
              :type="snapshotStatusTagType[currentSnapshot.status]"
              size="small"
              class="browser__status"
            >
              {{ snapshotStatusLabels[currentSnapshot.status] }}
            </el-tag>
            <span class="fbt-mono">{{ formatDateTime(currentSnapshot.beginTime) }}</span>
          </template>
        </div>
      </div>

      <div class="fbt-toolbar">
        <el-button
          size="small"
          :icon="ArrowLeft"
          :disabled="!olderSnapshot"
          @click="olderSnapshot && switchToSnapshot(olderSnapshot.id)"
        >
          更早
        </el-button>
        <el-button
          size="small"
          :disabled="!newerSnapshot"
          @click="newerSnapshot && switchToSnapshot(newerSnapshot.id)"
        >
          更晚<el-icon class="browser__caret"><ArrowRight /></el-icon>
        </el-button>
        <el-divider direction="vertical" />
        <el-button size="small" :icon="Refresh" :loading="listingLoading" @click="loadListing(currentPath, true)">
          刷新
        </el-button>
        <el-button size="small" type="primary" :icon="Download" @click="restoreWholeSnapshot">
          还原整快照
        </el-button>
      </div>
    </div>

    <ErrorState v-if="snapshotDetailError" :error="snapshotDetailError" compact class="fbt-section" />

    <div class="fbt-three-pane">
      <!-- 左：快照导航 -->
      <div class="fbt-pane browser__nav">
        <div class="fbt-pane-title">快照（按全量分组）</div>
        <ErrorState v-if="groupsError" :error="groupsError" compact @retry="loadSnapshots" />
        <StatePlaceholder v-else-if="groupsLoading" text="加载快照…" />
        <StatePlaceholder v-else-if="navItems.length === 0" empty description="该任务还没有快照" />
        <el-scrollbar v-else height="calc(100vh - 260px)">
          <div
            v-for="item in navItems"
            :key="item.snapshot.id"
            class="snap-nav__item"
            :class="{
              'is-active': item.snapshot.id === snapshotId,
              'is-head': item.isGroupHead,
            }"
            @click="switchToSnapshot(item.snapshot.id)"
          >
            <div class="snap-nav__row">
              <span class="snap-nav__type">
                {{ item.snapshot.type === 'Increment' ? '└' : '■' }}
              </span>
              <el-tag :type="typeTagType(item.snapshot.type)" size="small" effect="plain">
                {{ item.snapshot.type === 'Full' ? '全量' : item.snapshot.type === 'VirtualFull' ? '虚拟全量' : '增量' }}
              </el-tag>
              <span class="snap-nav__time fbt-mono">#{{ item.snapshot.id }}</span>
            </div>
            <div class="snap-nav__time fbt-mono">{{ formatDateTime(item.snapshot.beginTime) }}</div>
            <div class="snap-nav__stats">
              <span>+{{ item.snapshot.createdFileCount }}</span>
              <span>~{{ item.snapshot.modifiedFileCount }}</span>
              <span>-{{ item.snapshot.deletedFileCount }}</span>
              <span v-if="item.snapshot.failedFileCount > 0" class="fbt-warn-count">
                失败 {{ item.snapshot.failedFileCount }}
              </span>
              <span v-if="item.snapshot.skippedFileCount > 0" class="fbt-muted">
                跳过 {{ item.snapshot.skippedFileCount }}
              </span>
            </div>
          </div>
        </el-scrollbar>
      </div>

      <!-- 中：文件浏览器 -->
      <div class="fbt-pane browser__main">
        <div class="browser__toolbar">
          <el-button-group>
            <el-button size="small" :icon="HomeFilled" @click="goTo('')">根</el-button>
            <el-button size="small" :icon="FolderOpened" :disabled="!currentPath" @click="goUp">
              上一级
            </el-button>
          </el-button-group>

          <el-breadcrumb class="browser__crumbs" separator="/">
            <el-breadcrumb-item>
              <el-link :underline="false" @click="goTo('')">镜像根</el-link>
            </el-breadcrumb-item>
            <el-breadcrumb-item v-for="c in breadcrumbs" :key="c.path">
              <el-link :underline="false" @click="goTo(c.path)">{{ c.label }}</el-link>
            </el-breadcrumb-item>
          </el-breadcrumb>

          <div class="fbt-spacer" />

          <el-input
            v-model="keyword"
            size="small"
            placeholder="按文件名搜索（真过滤，非展开整棵树）"
            clearable
            style="width: 260px"
            @keyup.enter="runSearch"
            @clear="clearSearch"
          >
            <template #prefix>
              <el-icon><Search /></el-icon>
            </template>
            <template #append>
              <el-button :loading="searchLoading" @click="runSearch">搜索</el-button>
            </template>
          </el-input>
        </div>

        <div class="browser__actions">
          <el-button size="small" :icon="Download" :disabled="!canRestoreSelection" @click="restoreSelection">
            还原选中（{{ selectedRows.length }}）
          </el-button>
          <el-button size="small" :disabled="!currentPath" @click="restoreCurrentDir">
            还原当前目录
          </el-button>
          <span class="fbt-footnote">
            右键任意行可打开菜单（还原 / 版本历史 / 复制信息 / 打开所在目录）
          </span>
        </div>

        <ErrorState v-if="searchError" :error="searchError" compact @retry="runSearch" />
        <ErrorState v-else-if="listingError" :error="listingError" @retry="loadListing(currentPath)" />
        <StatePlaceholder v-else-if="listingLoading || searchLoading" text="正在读取目录…" />
        <StatePlaceholder
          v-else-if="displayEntries.length === 0"
          empty
          :description="searchMode ? '没有匹配的文件' : '该目录为空'"
        />

        <template v-else>
          <el-table
            :data="displayEntries"
            size="small"
            border
            highlight-current-row
            row-key="relativePath"
            :max-height="'calc(100vh - 430px)'"
            @row-click="selectEntry"
            @row-contextmenu="onRowContextMenu"
            @selection-change="onSelectionChange"
          >
            <!-- 多选：旧界面所有网格都是单选，复选框机制写好了但被禁用（07 §3 补齐项 4） -->
            <el-table-column type="selection" width="42" />

            <!-- 不加 sortable：表格是服务端分页的，客户端排序只会排当前页，是误导 -->
            <el-table-column label="名称" min-width="240">
              <template #default="{ row }">
                <div class="browser__name">
                  <el-icon v-if="row.isDirectory" class="browser__icon is-dir"><Folder /></el-icon>
                  <el-icon v-else class="browser__icon"><Document /></el-icon>
                  <el-link
                    v-if="row.isDirectory"
                    :underline="false"
                    class="fbt-ellipsis"
                    @click.stop="enterDir(row)"
                  >
                    {{ row.name }}
                  </el-link>
                  <span v-else class="fbt-ellipsis" :title="row.name">{{ row.name }}</span>
                </div>
              </template>
            </el-table-column>

            <el-table-column label="文件大小" width="190" align="right">
              <template #default="{ row }">
                <span v-if="row.isDirectory" class="fbt-footnote">
                  {{ formatCount(row.childDirCount ?? 0) }}个子文件夹，{{
                    formatCount(row.childFileCount ?? 0)
                  }}个子文件
                </span>
                <span v-else class="fbt-mono">{{ formatBytes(row.length) }}</span>
              </template>
            </el-table-column>

            <el-table-column label="修改时间" width="160">
              <template #default="{ row }">
                <span class="fbt-mono">{{ formatDateTime(row.fileTimeUtc) }}</span>
              </template>
            </el-table-column>

            <!-- 旧界面的「备份文件名」与「文件哈希（SHA1）」合并为一列：内容寻址后 blob 名就是哈希 -->
            <el-table-column label="哈希（blob 名）" width="170">
              <template #default="{ row }">
                <el-tooltip v-if="row.contentHash" :content="row.contentHash" placement="top">
                  <span class="fbt-mono browser__hash">{{ shortHash(row.contentHash) }}</span>
                </el-tooltip>
                <span v-else class="fbt-muted">—</span>
              </template>
            </el-table-column>

            <el-table-column label="操作" width="150">
              <template #default="{ row }">
                <el-button
                  size="small"
                  text
                  type="primary"
                  :disabled="row.isDirectory || row.restorable === false"
                  @click.stop="openRestore('Files', null, [row.relativePath])"
                >
                  另存为
                </el-button>
                <el-dropdown
                  trigger="click"
                  @command="(c: string) => {
                    detail = row
                    void onContextSelect(c)
                  }"
                >
                  <el-button size="small" text>复制信息</el-button>
                  <template #dropdown>
                    <el-dropdown-menu>
                      <el-dropdown-item command="copy-name">文件名</el-dropdown-item>
                      <el-dropdown-item command="copy-path">相对路径</el-dropdown-item>
                      <el-dropdown-item command="copy-hash" :disabled="!row.contentHash">哈希</el-dropdown-item>
                    </el-dropdown-menu>
                  </template>
                </el-dropdown>
              </template>
            </el-table-column>
          </el-table>

          <div class="browser__foot">
            <span class="fbt-footnote">
              <template v-if="searchMode">
                搜索「{{ keyword }}」共 {{ formatCount(displayTotal) }} 项
              </template>
              <template v-else>
                共 {{ formatCount(listing?.dirCount ?? 0) }} 个文件夹，{{
                  formatCount(listing?.fileCount ?? 0)
                }} 个文件，合计 {{ formatBytes(listing?.totalBytes ?? 0) }}
              </template>
            </span>
            <div class="fbt-spacer" />
            <el-pagination
              v-if="!searchMode"
              v-model:current-page="page"
              :page-size="pageSize"
              :total="displayTotal"
              layout="prev, pager, next, sizes"
              :page-sizes="[50, 100, 200, 500]"
              small
              @size-change="(s: number) => { pageSize = s; page = 1; loadListing(currentPath, true) }"
            />
          </div>
        </template>
      </div>

      <!-- 右：详情面板 -->
      <div class="fbt-pane browser__detail">
        <div class="fbt-pane-title">详情</div>
        <StatePlaceholder v-if="!detail" empty description="点击左侧任意行查看元数据" />

        <template v-else>
          <el-descriptions :column="1" size="small" border>
            <el-descriptions-item label="名称">
              <span class="fbt-ellipsis" :title="detail.name">{{ detail.name }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="完整路径（相对源目录）">
              <span class="fbt-mono browser__fullpath">{{ detail.relativePath }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="类型">
              {{ detail.isDirectory ? '文件夹' : '文件' }}
            </el-descriptions-item>
            <el-descriptions-item label="大小">
              <span class="fbt-mono">{{ formatBytes(detail.length) }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="修改时间">
              <span class="fbt-mono">{{ formatDateTime(detail.fileTimeUtc) }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="哈希">
              <span v-if="detail.contentHash" class="fbt-mono browser__fullpath">
                {{ detail.contentHash }}
              </span>
              <span v-else class="fbt-muted">—</span>
            </el-descriptions-item>
            <el-descriptions-item label="所属快照">
              <span class="fbt-mono">#{{ snapshotId }}</span>
            </el-descriptions-item>
          </el-descriptions>

          <el-alert
            v-if="detail.restorable === false"
            type="warning"
            :closable="false"
            show-icon
            class="fbt-section"
            :title="detail.blockedReason ?? '该文件不存在实际备份文件，可能是文件丢失'"
          />

          <div class="browser__detail-actions">
            <el-button
              size="small"
              type="primary"
              :disabled="detail.isDirectory || detail.restorable === false"
              @click="openRestore('Files', null, [detail.relativePath])"
            >
              还原此文件
            </el-button>
            <el-button size="small" @click="doCopy(detail.relativePath, '相对路径已复制')">
              复制路径
            </el-button>
          </div>

          <el-divider />

          <div class="fbt-pane-title">版本历史</div>
          <StatePlaceholder v-if="detail.isDirectory" empty description="文件夹没有版本历史" />
          <ErrorState v-else-if="historyError" :error="historyError" compact />
          <StatePlaceholder v-else-if="historyLoading" text="正在读取版本历史…" />
          <StatePlaceholder
            v-else-if="!history || history.versions.length === 0"
            empty
            description="该路径没有历史版本记录"
          />
          <el-timeline v-else>
            <el-timeline-item
              v-for="v in history.versions"
              :key="`${v.snapshotId}-${v.changeType}`"
              :timestamp="formatDateTime(v.snapshotBeginTime)"
              placement="top"
              size="normal"
            >
              <div class="version__row">
                <SnapshotTypeTag :type="v.snapshotType" />
                <span class="fbt-mono">#{{ v.snapshotId }}</span>
                <span class="fbt-mono">{{ formatBytes(v.length) }}</span>
              </div>
              <div class="version__actions">
                <el-button
                  size="small"
                  text
                  type="primary"
                  :disabled="!v.restorable"
                  @click="openRestore('Files', null, [v.relativePath])"
                >
                  另存为
                </el-button>
                <el-button size="small" text @click="switchToSnapshot(v.snapshotId)">
                  查看该快照
                </el-button>
              </div>
            </el-timeline-item>
          </el-timeline>
        </template>
      </div>
    </div>

    <FileContextMenu ref="ctxMenu" @select="onContextSelect" />

    <RestoreWizard
      v-model="restoreVisible"
      :snapshot-id="snapshotId"
      :initial-scope="restoreScope"
      :initial-directory="restoreDirectory"
      :initial-paths="restorePaths"
      :task-name="task?.name"
    />
  </div>
</template>

<style scoped>
.browser__heading {
  min-width: 0;
}

.browser__status {
  margin: 0 6px;
}

.browser__caret {
  margin-left: 2px;
}

.browser__nav {
  padding: 10px;
}

.snap-nav__item {
  padding: 6px 8px;
  border-radius: 4px;
  cursor: pointer;
  border-left: 2px solid transparent;
  margin-bottom: 2px;
}

.snap-nav__item:hover {
  background-color: var(--el-fill-color-light);
}

.snap-nav__item.is-active {
  background-color: var(--el-color-primary-light-9);
  border-left-color: var(--el-color-primary);
}

.snap-nav__item.is-head {
  margin-top: 8px;
}

.snap-nav__row {
  display: flex;
  align-items: center;
  gap: 6px;
}

.snap-nav__type {
  color: var(--el-text-color-placeholder);
  font-size: 12px;
}

.snap-nav__time {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.snap-nav__stats {
  display: flex;
  gap: 8px;
  font-size: 11px;
  color: var(--el-text-color-secondary);
  margin-top: 2px;
}

.browser__main {
  min-width: 0;
}

.browser__toolbar,
.browser__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
  flex-wrap: wrap;
}

.browser__crumbs {
  display: inline-flex;
  flex-wrap: wrap;
  font-size: 13px;
}

.browser__name {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.browser__icon {
  color: var(--el-text-color-secondary);
  flex: 0 0 auto;
}

.browser__icon.is-dir {
  color: var(--el-color-warning);
}

.browser__hash {
  cursor: help;
}

.browser__foot {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 8px;
  flex-wrap: wrap;
}

.browser__detail {
  min-width: 0;
}

.browser__fullpath {
  word-break: break-all;
}

.browser__detail-actions {
  display: flex;
  gap: 8px;
  margin-top: 10px;
}

.version__row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
}

.version__actions {
  display: flex;
  gap: 4px;
  margin-top: 2px;
}
</style>
