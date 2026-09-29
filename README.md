# FileBackupTool

> ⚠️ **本项目由 AI 开发，目前尚未完成，未达到可用级别。**
>
> 代码、设计文档与提交记录都由 AI 产出，且**未经过人工测试**。具体缺口：
> 自动化测试只覆盖引擎与数据层——`tests/` 两个项目**不引用** `FileBackupTool.Api`，
> 所以控制器、SignalR Hub、Identity 这条链**没有任何自动化测试兜底**；
> 浏览器端只被一次性脚本验证过，而那个脚本**没有入库**，因此不构成可复跑的回归防线。
>
> 请**不要**把它当成可依赖的备份工具——备份工具出错的代价是数据。
> 现阶段适合的用途是阅读代码与设计文档。

一个**服务端常驻、浏览器控制**的文件备份工具。备份引擎跑在服务器上，浏览器只做控制面。

- **后端**：ASP.NET Core (.NET 10) / Kestrel / SQLite + EF Core / ASP.NET Core Identity（**单密码登录**）
- **前端**：Vue 3 + TypeScript + Vite + Element Plus（构建产物由后端带起）
- **核心算法**：移植自 `ArchiveMaster.Module.FileBackupper`，但**数据层与存储格式是重新设计的**
- **部署**：Windows + nginx 终止 TLS，后续可用 nssm 注册为服务

## 设计文档（先读这个）

全部在 [`docs/plans/`](docs/plans/)：

| 文档 | 内容 |
|---|---|
| [README](docs/plans/README.md) | 索引 + **43 条决策总览**（这是双方确认过的契约） |
| [01 总体设计](docs/plans/01-总体设计.md) | 定位、技术栈、工程结构、进程与并发模型、配置、部署 |
| [02 存储与数据模型](docs/plans/02-存储与数据模型.md) | 数据库 schema、备份目录布局、blob 池、回收站、抢救清单 |
| [03 备份引擎](docs/plans/03-备份引擎.md) | 核心算法、去重与哈希、变更检测、实时监控、调度、错误处理 |
| [04 还原与快照管理](docs/plans/04-还原与快照管理.md) | 还原流程与冲突策略、快照删除级联、保留策略 |
| [05 认证与安全](docs/plans/05-认证与安全.md) | Identity、TOTP、会话、nginx 协同、限流、审计、威胁模型（**TOTP 那部分已按 [09 §L](docs/plans/09-实现期修订.md) 改为单密码登录**） |
| [06 后端与 API](docs/plans/06-后端与API.md) | 分层、服务契约、REST 端点清单、SignalR 事件 |
| [07 前端设计](docs/plans/07-前端设计.md) | 信息架构、路由、页面清单、对等与补齐清单 |
| [08 测试与待定项](docs/plans/08-测试与待定项.md) | 测试策略、CI 门槛、默认值、遗留待定项 |
| [**09 实现期修订**](docs/plans/09-实现期修订.md) | **实现期对 01–08 的偏离逐条记录**（含实现中发现的缺陷） |

> `01`–`08` 是冻结的契约。实现期发现文档有问题时，**不改文档、而是记进 `09`**，
> 逐条给出理由，由你决定接受还是推翻。

## 工程结构

```
FileBackupTool/
├─ FileBackupTool.sln
├─ Directory.Build.props          # 统一 TFM / Nullable / LangVersion
├─ src/
│  ├─ FileBackupTool.Core/        # 领域模型、枚举、抽象、配置模型、路径与筛选
│  ├─ FileBackupTool.Data/        # EF Core 上下文、迁移、仓储、TEMP 镜像表
│  ├─ FileBackupTool.Engine/      # 备份/还原/GC/清单/作业队列
│  ├─ FileBackupTool.Monitoring/  # 变更监控（两套机制 + 三个钩子）
│  └─ FileBackupTool.Api/         # ASP.NET Core 宿主：Controllers、Hub、Identity、wwwroot
├─ tests/
│  ├─ FileBackupTool.UnitTests/         # 内存文件系统 + 真实临时 SQLite
│  └─ FileBackupTool.IntegrationTests/  # **真实文件系统**：往返一致性（CI 硬门槛）
└─ web/                           # Vue 3 + TS + Vite
```

## 环境要求

- .NET SDK **10.0.4xx**（`dotnet --version`）
- Node.js **20+**（仅前端构建需要）

## 构建与运行

### 后端

```powershell
# 首次运行需要提供初始管理员密码（**不写死在文件里**）
$env:FILEBACKUPTOOL_ADMIN_PASSWORD = "<一个满足策略的密码>"   # 长度≥12，含大小写数字符号
dotnet run --project src\FileBackupTool.Api
```

首次启动会：执行 EF migrations → 建库（`App_Data/filebackuptool.db`）→ 种子创建用户 →
把残留的 `InProgress` 快照标记为 `Aborted`（启动自愈）。

### 前端（开发期）

```powershell
cd web
npm install
npm run dev        # Vite dev server，/api 与 /hubs 代理到 http://localhost:5000
```

### 前端（发布）

```powershell
cd web
npm run build      # → web/dist
# 把 web/dist 的内容拷进 src/FileBackupTool.Api/wwwroot/，由后端静态文件中间件带起
```

### 测试

```powershell
dotnet test tests\FileBackupTool.UnitTests
dotnet test tests\FileBackupTool.IntegrationTests
```

> **集成测试是 CI 硬门槛**：它在真实文件系统上做"备份 → 改动 → 再备份 → 还原 → 逐字节比对"。
> 这是备份工具存在的全部理由，不通过不得合并（设计文档 08 §1.1）。
>
> `IntegrationTests` **刻意不引用 `Monitoring`**，这样这条硬门槛不会被监控模块的编译问题连坐。

### 数据库迁移

```powershell
dotnet ef migrations add <Name> -p src\FileBackupTool.Data -o Migrations
```

### 一键发布（生产部署）

```powershell
.\publish.ps1 -Zip        # 在开发机上跑
```

它会依次：跑单元 + 集成测试 → `npm ci` + 构建前端 → 产物拷进 `wwwroot` → `dotnet publish`
→ 附加部署件 → 打包。产出 `publish/`（已在 `.gitignore` 里）与同名 zip。

- **自包含 + 单文件**：105 MB 一个 exe，生产机不需要装 .NET、Node 或任何运行时。
  留在 exe 外面的只有 `e_sqlite3.dll` 这类原生库（它们本来就无法打包）。
- 整个 `publish/` 拷到生产机，在**那个目录里**以管理员身份跑
  `.\install-service.ps1 -ServiceAccount '<能读写备份源与数据目录的账号>'`
  即可注册并启动 Windows 服务（用 [nssm](https://nssm.cc/download)），
  脚本会自己探活验证服务真的起来了。
- 同目录带**两份** nginx 样例，按拓扑选一份（两份的转发头写法**必须不同**，
  抄错不是"少一层防护"而是静默降级：限流退化成全站共用配额、实时通道变成轮询）：
  `nginx.conf.sample` = 本机自己终止 TLS、应用挂根路径；
  `nginx-subpath.sample.conf` = 本机只是内层一跳、应用挂子路径。
  另带 `appsettings.Production.json.template`。生产配置**已存在就绝不覆盖**，
  重跑安装脚本不会动它，要重建得显式 `-Force`。
- 要挂在子路径下（如 `/backup/`）用 `.\publish.ps1 -PathBase /backup`：
  这只是**前端构建期**的开关，后端与二进制不受影响，所以换目录时只需要替换
  `wwwroot\`。两份 nginx 样例的适用范围见 [09 §P](docs/plans/09-实现期修订.md)。
- 发布参数（自包含 / 单文件 / 关预压缩）与设计文档 08 §4.1 的偏离、
  以及各项的**验证状态**记在 [09 §N](docs/plans/09-实现期修订.md)。

> ⚠️ `install-service.ps1` 里 "nssm 注册" 那一段**尚未在真实环境验证过**
> （开发机没装 nssm、也没提权）。脚本失败时会打印服务状态与日志尾部再退出，
> 所以第一次在生产机上跑请盯着输出看。

## 首次运行的几步

1. 放置 `appsettings.json`，配置 `DataDir`、连接串、`ReverseProxy:TrustedProxies`。
2. 用环境变量提供初始账号密码（见上）。
3. 浏览器输入**那一个密码**即可登录（账号由配置的 `SeedUserName` 解析，界面不要求用户名）。
4. 配置**还原根白名单**——还原是写文件操作，白名单是最重要的安全缓解。

## 部署要点

- **nginx 终止 TLS**，Kestrel 只监听 `localhost`。四项必须配置见 [05 §6](docs/plans/05-认证与安全.md)：
  `ForwardedHeaders`、只信任 nginx 的 IP、`UseHttpsRedirection` 在反代后可能死循环、HSTS。
- **nssm 服务化**时必须把 `DataDir` 改成**绝对路径**（服务的工作目录与双击运行不同），
  并用**专用服务账号**（不要 LocalSystem）。
- **强制单实例**：误起两个进程会同时写同一批 blob 与同一个 SQLite——**静默毁数据**。
- 长路径：应用清单已声明 `longPathAware`；目标机建议同时设置 `LongPathsEnabled=1`。

## 这个工具**不做**什么

- 不做多用户、不做协作、不做分享。
- **不提供任何打包下载**——所有文件不出服务器。
- 不读取 ArchiveMaster 已有的备份目录（不做格式兼容）。
- 不做备份数据的静态加密（原因见 [05 §7.1](docs/plans/05-认证与安全.md)）。
