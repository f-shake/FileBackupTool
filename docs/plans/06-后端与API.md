# 06 · 后端与 API

## 1. 分层

```
FileBackupTool.Api           ASP.NET Core 宿主
  ├─ Controllers/            REST 端点
  ├─ Hubs/                   SignalR
  ├─ Identity/               单用户种子、TOTP、会话管理
  ├─ Middleware/             ForwardedHeaders、异常处理、审计
  └─ wwwroot/                前端构建产物

FileBackupTool.Engine        备份 / 还原 / GC 的编排
  ├─ SnapshotEngine          全量、增量、虚拟全量
  ├─ MirrorBuilder           镜像重建（流式）
  ├─ BlobStore               内容寻址 blob 池（写入、探测、回收）
  ├─ ManifestWriter          抢救清单
  ├─ RestoreService          白名单校验 + 冲突策略 + 复制
  ├─ GarbageCollector        引用计数 + 移入回收站
  └─ JobQueue                全局串行 IO 作业队列

FileBackupTool.Monitoring    变更检测
  ├─ WatcherRoot             单根的 FileSystemWatcher
  ├─ ScanRoot                单根的扫描（局部 / 全量）
  ├─ ChangeDetector          静默期冻结 + 合并窗口
  └─ RootOrchestrator        两套机制的协作契约（三个钩子）

FileBackupTool.Data          EF Core
  ├─ AppDbContext
  ├─ Migrations/
  └─ Repositories/

FileBackupTool.Core          领域模型与抽象
  ├─ Entities/ Enums/
  ├─ Abstractions/           IFileSystem、IClock、IHashService、IBlobStore…
  └─ Filtering/
```

## 2. 测试驱动的架构约束

因为**要写测试**（含"往返一致性"作为 CI 硬门槛，见 [08](08-测试与待定项.md)），下列边界**从第一天就必须抽象**：

| 接口 | 目的 |
|---|---|
| `IFileSystem` | 枚举、读、写、移动、删除、存在性、时间戳、属性。所有文件 IO 必须经过它 |
| `IClock` | 所有 `UtcNow` 必须经过它（否则测试无法构造时间场景） |
| `IHashService` | 哈希计算可替换（测试可用快速假哈希） |
| `IBlobStore` | blob 池操作，便于断言"写了哪些 blob" |
| `IPathNormalizer` | 平台相关的大小写归一化与长路径前缀，**唯一入口** |

**不允许**在引擎里直接调用 `System.IO.File` / `Directory`——除了 `IFileSystem` 的实现内部。

## 3. 服务契约（引擎层）

```csharp
public interface IBackupService
{
    Task<SnapshotId> RunAsync(BackupTaskId taskId, SnapshotType type, CancellationToken ct);
    Task CancelAsync(SnapshotId snapshotId);
}

public interface IRestoreService
{
    Task<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct);   // 预演：列出将写哪些文件、冲突有哪些
    Task<RestoreResult> ExecuteAsync(RestorePlan plan, IProgress<RestoreProgress> progress, CancellationToken ct);
}

public interface IMirrorQueryService
{
    Task<PagedResult<MirrorEntry>> ListDirectoryAsync(
        BackupTaskId taskId, SnapshotId snapshotId, string relativePath, int page, int pageSize, CancellationToken ct);

    Task<IReadOnlyList<FileVersionEntry>> GetFileHistoryAsync(
        BackupTaskId taskId, string relativePath, CancellationToken ct);
}

public interface ISnapshotService
{
    Task<PagedResult<SnapshotGroup>> ListAsync(BackupTaskId taskId, SnapshotFilter filter, int page, int pageSize, CancellationToken ct);
    Task<DeleteImpact> PreviewDeleteAsync(SnapshotId snapshotId, CancellationToken ct);  // 级联影响清单
    Task DeleteAsync(SnapshotId snapshotId, CancellationToken ct);
}

public interface IIntegrityService
{
    Task<IntegrityReport> VerifyAsync(BackupTaskId taskId, bool deepHashCheck, CancellationToken ct);
    Task<GcResult> CollectAsync(BackupTaskId taskId, CancellationToken ct);
}

public interface IJobQueue
{
    Task<JobHandle> EnqueueAsync(JobRequest request, CancellationToken ct);
    Task<IReadOnlyList<QueuedJob>> ListQueuedAsync(CancellationToken ct);
    Task<bool> CancelAsync(JobId jobId, CancellationToken ct);
}
```

**所有公开方法必须带 `CancellationToken`**（旧设计的 CT 是私有字段，`CheckAndBackupAllAsync` 直接调用还会 NRE）。

## 4. REST API

统一前缀 `/api`。分页统一 `?page=&pageSize=`，响应 `{ items, total, page, pageSize }`。错误统一 `{ code, message, detail? }`。

### 4.1 任务

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/tasks` | 任务列表（含实时状态、上次备份时间、快照数） |
| `POST` | `/api/tasks` | 新建任务（触发目录关系校验） |
| `GET` | `/api/tasks/{id}` | 任务详情 |
| `PUT` | `/api/tasks/{id}` | 修改任务（触发目录关系校验） |
| `DELETE` | `/api/tasks/{id}` | 软删除任务 |
| `POST` | `/api/tasks/{id}/enable` | 启用 / 禁用 |
| `POST` | `/api/tasks/validate-dirs` | 目录关系预校验（表单实时反馈用） |
| `POST` | `/api/tasks/{id}/backup` | 立即备份（body 指定 `Full`/`VirtualFull`/`Increment`） |
| `POST` | `/api/tasks/{id}/cancel` | 取消当前备份 |

### 4.2 快照

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/tasks/{id}/snapshots` | 快照列表（按全量分组，支持类型/日期筛选与分页） |
| `GET` | `/api/snapshots/{sid}` | 快照详情（计数、失败数、占用空间） |
| `GET` | `/api/snapshots/{sid}/delete-impact` | **级联删除影响清单**（删除前必须调） |
| `DELETE` | `/api/snapshots/{sid}` | 删除（级联） |
| `GET` | `/api/snapshots/{sid}/changes` | 变更内容（新增/修改/删除三类，分页） |
| `GET` | `/api/snapshots/{sid}/files` | **镜像浏览**：`?path=&page=&pageSize=`，返回该目录下的子目录与文件 |
| `GET` | `/api/snapshots/{sid}/tree` | 目录树骨架（用于导航，不含文件） |

### 4.3 文件与历史

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/tasks/{id}/file-history` | `?path=` 返回该路径的所有版本 + 快照时间 |
| `GET` | `/api/snapshots/{sid}/file` | `?path=` 单文件元数据（名称、大小、mtime、哈希、所属快照） |
| `GET` | `/api/tasks/{id}/search` | 在指定快照的镜像中按文件名搜索（真过滤，非"展开+多选"） |

### 4.4 还原

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/restore/roots` | 白名单还原根目录列表 |
| `POST` | `/api/restore/plan` | **预演**：给定 快照+路径集合+目标+冲突策略，返回将写入的清单与冲突明细 |
| `POST` | `/api/restore/execute` | 执行（入全局队列），返回作业句柄 |
| `DELETE` | `/api/restore/{jobId}` | 取消 |

### 4.5 作业与监控

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/jobs` | 队列与运行中作业 |
| `GET` | `/api/jobs/{jobId}` | 作业详情与进度快照 |
| `GET` | `/api/monitoring` | 各根的监控状态（`watcher` / `polling` / `error`）、待处理变更队列长度、最近触发记录 |
| `POST` | `/api/tasks/{id}/scan` | 手动触发扫描（局部或全量） |

### 4.6 完整性

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/api/tasks/{id}/verify` | 完整校验（返回 多余文件 / 丢失文件 / 链断裂） |
| `POST` | `/api/tasks/{id}/collect` | 回收（移入回收站） |
| `GET` | `/api/tasks/{id}/recycle-bin` | 回收站占用与账本 |
| `DELETE` | `/api/tasks/{id}/recycle-bin` | 清空回收站（**硬删除，不可逆，需二次确认**） |

### 4.7 日志与审计

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/logs` | `?taskId=&snapshotId=&level=&from=&to=&keyword=&page=` |
| `GET` | `/api/audit` | 审计日志（按时间/类型筛选） |
| `GET` | `/api/logs/{id}` | 单条日志详情（含 `Detail`） |

### 4.8 文件系统浏览（用于目录选择）

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/fs/roots` | 可浏览的根（驱动器 / 配置的根） |
| `GET` | `/api/fs/list` | `?path=` 列出子目录（**只返回目录**，用于选源/目标；返回名称、时间，不返回内容） |
| `POST` | `/api/fs/validate` | 校验目录关系（源 vs 备份 vs 还原根） |
| `POST` | `/api/fs/filter-test` | **筛选规则试算**（见下） |

**筛选规则试算**（[07 §4.1](07-前端设计.md) 的对等清单要求"使用当前目录测试"，
返回文件数量与文件大小；本节是对 06 初版的补充）：

```
POST /api/fs/filter-test
{ "sourceDir": "D:\\Documents", "filterJson": "{…}" }        // filterJson 与 BackupTask.FilterJson 同格式

→ {
    "fileCount": 12345,
    "totalBytes": 9876543210,
    "files": [ { "relativePath": "报告/2026.docx", "length": 12345, "fileTimeUtc": "…" } ],
    "skippedDirCount": 3,
    "truncated": false
  }
```

语义与备份引擎**必须一致**：走同一个 `IFileFilter`（含 `$RECYCLE.BIN`、数据目录、备份目录的硬排除），
所以"试算通过"意味着"备份时确实会这样筛"。

实现注意：

- `files` **必须截断**（建议 200 条），`truncated` 如实标记——否则一个百万文件的源目录会把响应撑爆。
- 枚举要设**总量上限**（建议 20 万条）后停止并标记 `truncated`，
  避免这个端点被当成"廉价的全盘扫描"（[05](05-认证与安全.md) 的限流要求对所有 `/api/fs/*` 生效）。
- `skippedDirCount` 覆盖无权限/重解析点跳过的目录数，"不静默"。
- 必须认证（与 `/api/fs/*` 其余端点一致）。

**安全**：必须认证；拒绝符号链接穿越；不返回文件内容。因为登录者就是管理员，不做路径白名单限制，但要做穿越防护。

### 4.9 认证与设置

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/api/auth/login` | 登录（限流 + 锁定） |
| `POST` | `/api/auth/logout` | 登出 |
| `GET` | `/api/auth/me` | 当前用户信息 |
| `POST` | `/api/auth/totp/setup` | 生成 TOTP 密钥与二维码 |
| `POST` | `/api/auth/totp/confirm` | 确认绑定 |
| `POST` | `/api/auth/totp/recovery-codes` | 重新生成恢复码 |
| `POST` | `/api/auth/password` | 改密码（失效全部会话） |
| `GET` | `/api/auth/sessions` | 会话列表 |
| `DELETE` | `/api/auth/sessions/{id}` | 踢出某个会话 |
| `GET` / `PUT` | `/api/settings` | 全局设置（非基础设施部分，如还原根白名单、保留策略） |
| `GET` | `/api/settings/info` | 版本、数据目录、磁盘占用等只读信息 |

## 5. SignalR

**Hub 路径 `/hubs/events`**，需要认证。前端作为**主通道**，REST 轮询作为降级（见 [07](07-前端设计.md#5-实时通信)）。

| 事件 | 负载 | 用途 |
|---|---|---|
| `job.queued` | `{ jobId, type, position }` | 排队位置变化 |
| `job.started` | `{ jobId, type, taskId }` | 作业开始 |
| `backup.progress` | `{ jobId, phase, discovered, processed, total, currentPath, bytesDone, bytesTotal }` | 备份进度 |
| `restore.progress` | `{ jobId, processed, total, currentPath }` | 还原进度 |
| `job.completed` | `{ jobId, result }` | 完成（含失败计数） |
| `job.failed` | `{ jobId, error }` | 失败 |
| `job.aborted` | `{ jobId, reason }` | 取消 / 崩溃自愈 |
| `log.appended` | `{ taskId, snapshotId, level, time, message }` | 日志流（可按任务订阅） |
| `monitor.changed` | `{ taskId, rootState, pendingCount }` | 监控状态与待处理变更数 |
| `snapshot.committed` | `{ taskId, snapshotId }` | 快照提交（前端刷新列表） |

**分组**：`task:{taskId}`，前端只订阅当前查看的任务。

## 6. 进度模型

因为文件数可能到百万级，**枚举阶段不能先数完再开工**（先数一遍等于多扫一次盘）。所以是**两段式**：

| 阶段 | 进度形态 |
|---|---|
| **枚举** | **不确定态** + 实时计数：「已发现 123,456 个文件」 |
| **处理** | **确定态百分比**：「已备份 45,678 / 123,456」+ 当前文件路径 + 已完成字节 / 总字节 |
| **提交** | 不确定态 + 「正在写入数据库…」 |
| **清单 / GC** | 不确定态 + 「正在生成抢救清单…」「正在回收…」 |

`phase` 字段区分阶段，前端据此切换进度条形态。

## 7. 日志

- **Serilog** 文件 sink，按天滚动，写在 `App_Data/logs/`。
- **请求日志**：Serilog 的 `UseSerilogRequestLogging`。
- **业务日志**：写入 `BackupLog` 表（按任务/快照关联），同时输出到文件。
- **审计日志**：写入 `AuditLog` 表（见 [05](05-认证与安全.md#5-审计日志)）。

> 旧设计只有文件 sink，没有 console、没有结构化、没有请求日志。

## 8. 后台服务清单

| 服务 | 职责 | 节拍 |
|---|---|---|
| `JobQueueService` | 全局串行 IO 作业队列 | 事件驱动 |
| `SchedulerService` | 检查任务是否到期并入队 | 60 秒 |
| `MonitoringService` | 每源的 watcher / 轮询扫描 | 事件驱动 + 各自节拍 |
| `FullScanFallbackService` | 周期性全量扫描兜底 | 6 小时（可配） |
| `RetentionService` | 可选的定时淘汰（默认关闭） | 1 小时 |
| `StartupSelfHealService` | 把 `InProgress` 快照标为 `Aborted`；启动自检扫描 | 启动一次 |
