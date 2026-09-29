<#
.SYNOPSIS
    构建 FileBackupTool 的生产发布包（在**开发机**上运行）。

.DESCRIPTION
    产出一个**自包含**的发布目录（默认 publish\）。整个目录拷到生产机后，
    在该目录里以管理员身份运行 install-service.ps1 即可注册为 Windows 服务。
    生产机因此**不需要**安装 .NET SDK、.NET 运行时或 Node.js。

    前端与后端在同一个目录里产出：这里先用 npm 构建 web\dist，
    再把产物拷进 src\FileBackupTool.Api\wwwroot\，然后 dotnet publish 一起带走
    （设计文档 01 §7.1）。顺序不能反——publish 之后再拷 wwwroot，包里就没有前端。

    为什么发**自包含**而不是设计文档 08 §4.1 写的"框架依赖"：
    那条决策的前提是"目标机已装 .NET 10 runtime"，而这个前提在本项目的实际
    生产环境里不成立。偏离记在 docs/plans/09-实现期修订.md §N。
    代价是包大约 105 MB；收益是生产机零依赖，少一类"运行时版本不对"的故障。

    默认还开两个开关，都是为了**文件数**（488 → 约 54）：

      · PublishSingleFile —— 355 个 dll 合并成一个 exe。
        本项目全仓没有 Assembly.Location / AppContext.BaseDirectory 的用法，
        所以单文件最经典的陷阱（靠物理路径定位资源）在这里不存在。
        留在 exe 外面的只有原生库（e_sqlite3.dll 等），它们本来也不能打包。
      · CompressionEnabled=false —— 关掉 SDK 的静态资源预压缩。
        它会为每个前端文件额外生成 .gz 与 .br（38 个变 114 个），
        而本应用用的是经典 UseStaticFiles()，**不认**这些预压变体：
        实测带不带 Accept-Encoding 返回的都是未压缩的原文，
        那些文件一个都没被用到，纯占地方。

    想退回传统多文件发布（便于逐个替换 dll 排查问题）用 -MultiFile。

.PARAMETER MultiFile
    不做单文件合并，产出 355 个独立 dll 的传统布局。

.PARAMETER PathBase
    把**前端**构建成挂在某个子路径下（例如 `-PathBase '/backup'`），默认根路径。

    这只是前端构建期的开关——**后端与二进制完全不受影响**（应用本身没有 PathBase
    支持，也不需要）。所以这样发出来的包，与根路径版只差 `wwwroot\` 一个目录：
    生产上换掉 `wwwroot\`、再让 nginx 把前缀剥掉即可，服务都不用重装。

    nginx 侧对应这样一条（末尾的 `/` 是关键，它负责剥掉前缀）：

        location /backup/ {
            proxy_pass http://127.0.0.1:5000/;
            ...
        }

    ⚠️ 前缀是**构建期**注入的（`import.meta.env`），所以改了它必须重新构建。

.PARAMETER OutputDir
    发布输出目录，默认 publish\（已在 .gitignore 里，不会被提交）。

.PARAMETER Runtime
    目标运行时标识，默认 win-x64。ARM 机器用 win-arm64。

.PARAMETER Configuration
    默认 Release。**不要**用 Debug 发生产。

.PARAMETER SkipTests
    跳过 dotnet test。不建议：集成测试是设计文档 08 §1.1 的 CI 硬门槛。

.PARAMETER SkipWeb
    跳过前端构建，直接用手头 wwwroot 里已有的产物。只有在你确信它是最新时才用。

.PARAMETER Clean
    用 npm ci 重装依赖（而不是复用 node_modules），并清空输出目录后重建。

.PARAMETER Zip
    额外打成 publish\FileBackupTool-<commit>.zip，方便整包拷贝到生产机。

.EXAMPLE
    .\publish.ps1

.EXAMPLE
    .\publish.ps1 -Clean -Zip
#>
# #Requires 必须放在帮助块**之后**：它是解析器指令而不是普通注释，
# 放在最前面会让 PowerShell 认为"帮助块不是文件开头"，于是整段注释帮助被忽略
# （Get-Help 只回一行语法）——实测于 PowerShell 7.6.6。
# 放在这里两个效果都保住：帮助能解析，#Requires 也照旧强制执行。
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $OutputDir     = 'publish',
    [string] $Runtime       = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $PathBase      = '',
    [switch] $SkipTests,
    [switch] $SkipWeb,
    [switch] $Clean,
    [switch] $MultiFile,
    [switch] $Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# 输出helpers
# -----------------------------------------------------------------------------
function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}
function Write-Ok([string] $Text)   { Write-Host "   OK   $Text" -ForegroundColor Green }
function Write-Warn2([string] $Text) { Write-Host "   WARN $Text" -ForegroundColor Yellow }
function Write-Err([string] $Text)  { Write-Host "   FAIL $Text" -ForegroundColor Red }
function Stop-Fail([string] $Text) {
    Write-Err $Text
    exit 1
}

# 跑一个原生命令；非 0 退出码一律当作致命错误。
# 不这么做的话，dotnet/npm 失败只会在控制台刷过去，脚本继续往下走并**产出一个残缺的包**。
function Invoke-Native([string] $Exe, [string[]] $CmdArgs, [string] $What) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Write-Host "   > $Exe $($CmdArgs -join ' ')" -ForegroundColor DarkGray
        $global:LASTEXITCODE = 0
        & $Exe @CmdArgs

        if ($LASTEXITCODE -eq 0) { return }

        # 负的退出码意味着进程**根本没起来**（Win32 NTSTATUS，例如
        # 0xC0000142 STATUS_DLL_INIT_FAILED），而不是命令跑了并且失败。
        # 这类失败在非交互会话/被重定向 stdout 的环境里是出了名的偶发，
        # 实测同一目录下直接跑就通过。真正的失败（编译错误、测试不过）是退出码 1，
        # 所以这里按"是不是启动失败"来区分，重试一次再判死刑——不掩盖真实错误。
        if ($LASTEXITCODE -gt 0) {
            Stop-Fail "$What 失败（$Exe 退出码 $LASTEXITCODE）"
        }

        if ($attempt -eq 1) {
            Write-Warn2 "$What 的进程未能启动（退出码 $LASTEXITCODE，疑似环境抖动），重试一次…"
            Start-Sleep -Seconds 2
        }
    }
    Stop-Fail "$What 重试后仍未启动（$Exe 退出码 $LASTEXITCODE）。"
}

function Invoke-Git([string[]] $CmdArgs) {
    # git 不存在时不让整个脚本炸掉：版本信息只是锦上添花。
    # 但要注意"空输出"有两种含义：`status --porcelain` 干净时本来就输出空；
    # 而进程没起来（负退出码，见 Invoke-Native 的说明）也会输出空——必须区分，
    # 否则工作区脏会被误报成干净，而"包里是哪份代码"恰恰是排查事故的第一问。
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            $global:LASTEXITCODE = 0
            $result = & git @CmdArgs 2>$null
            $code = $LASTEXITCODE
        } catch {
            return $null
        }
        if ($code -eq 0) { return ($result | Out-String).Trim() }
        if ($code -gt 0) { return $null }   # git 跑起来了但报错（例如不在仓库里）
        Start-Sleep -Seconds 1              # 负值：进程没起来，重试
    }
    return $null
}

$root = $PSScriptRoot
Set-Location $root

# 前端路径前缀（由 -PathBase 决定）。先给默认值：末尾「部署步骤」那段要引用它，
# 而 -SkipWeb 分支不会走到给它赋值的那个分支，不预置就会在 StrictMode 下抛"未定义变量"。
$prefix = ''

# -----------------------------------------------------------------------------
# 0. 前置检查
#    目的只有一个：在**动手构建之前**把"注定会失败"或"会产出错误包"的情况挡掉。
# -----------------------------------------------------------------------------
Write-Step '0/6 前置检查'

foreach ($exe in @('dotnet')) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
        Stop-Fail "找不到 $exe。开发机需要 .NET SDK 10.0.4xx。"
    }
}
Write-Ok "dotnet $(dotnet --version)"

if (-not $SkipWeb) {
    foreach ($exe in @('node', 'npm')) {
        if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
            Stop-Fail "找不到 $exe。构建前端需要 Node.js 20+；若 wwwroot 已是最新，可用 -SkipWeb 跳过。"
        }
    }
    Write-Ok "node $(node --version)"
}

# -Clean 会**递归删除**输出目录。$OutputDir 是自由字符串参数，所以先把几类
# "删了会哭"的值挡掉。默认值 publish 不受影响。
# 放在这里（而不是删除那一步）是为了在跑测试之前就失败——早失败便宜。
if ($Clean) {
    $fullOut  = [System.IO.Path]::GetFullPath((Join-Path $root $OutputDir))
    $rootFull = [System.IO.Path]::GetFullPath($root)
    $dangerous =
        ($fullOut -eq [System.IO.Path]::GetPathRoot($fullOut)) -or                  # 盘符根，如 C:\
        ($rootFull.StartsWith($fullOut, [StringComparison]::OrdinalIgnoreCase))      # 本仓库根，或它的祖先
    if ($dangerous) {
        Stop-Fail "-Clean 会递归删除 $fullOut —— 那是盘符根，或是包含本仓库的目录。拒绝执行。请给一个专门的输出目录（例如默认的 publish）。"
    }
}

# 正在运行的本程序会**锁住 bin 下的 dll**，dotnet publish 会以一个很难懂的错误失败。
# 先检查并说清楚，比让 MSBuild 抛一堆无关错误再让人去猜要好。
# 匹配 FileBackupTool.Api 而不是 FileBackupTool：测试宿主跑的是 FileBackupTool.UnitTests.dll，
# 不该被误伤。
$running = @()
$queryError = $null
try {
    $running = @(Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object { $_.CommandLine -like '*FileBackupTool.Api*' })
} catch {
    # 查不到 ≠ 没有。这一项存在的**唯一**目的就是提前拦住"dll 被占用"，
    # 而它失效时的症状恰恰就是 MSBuild 抛一堆无关错误 —— 所以不能打印成功。
    $queryError = $_.Exception.Message
}
if ($queryError) {
    Write-Warn2 "进程列表查不到（$queryError），这一项**未检测**。"
    Write-Warn2 '若随后构建报文件占用，请手工确认没有本程序在跑（服务方式或命令行起的）。'
} elseif ($running.Count -gt 0) {
    Write-Host ''
    $running | ForEach-Object { Write-Host "        PID $($_.ProcessId): $($_.CommandLine)" -ForegroundColor DarkGray }
    Stop-Fail "检测到 $($running.Count) 个正在运行的本程序进程。它会锁住待发布的 dll。请先停掉服务（或结束这些进程）再发布。"
} else {
    Write-Ok '没有正在运行的本程序进程'
}

$commit    = Invoke-Git @('rev-parse', '--short', 'HEAD')
$branch    = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
$dirtyRaw  = Invoke-Git @('status', '--porcelain')
$isDirty   = -not [string]::IsNullOrWhiteSpace($dirtyRaw)
$commitTag = if ($commit) { $commit } else { 'nogit' }

if ($commit) {
    Write-Ok "git $branch @ $commit$(if ($isDirty) { ' （有未提交改动）' })"
    if ($isDirty) {
        Write-Warn2 '工作区有未提交的改动：包里将是"当前文件内容"，与仓库里的任何提交都不完全对应。'
        Write-Warn2 '生产事故排查时这一点很要命——建议先提交再发布。'
    }
} else {
    Write-Warn2 '读不到 git 信息（未安装 git 或不在仓库里），VERSION.txt 里会记成 nogit。'
}

# 依赖漏洞审计。09 §E3 记过一次 NU1903 高危**传递**依赖——公网部署不能带着已知漏洞跑，
# 而传递依赖不会出现在 csproj 里，靠人看是看不出来的，只能靠这个命令。
#
# **只警告不阻断**：这个命令要联网查 NuGet 源。把"网络不通"变成"发布失败"是错误的
# 失败方式（发版的人会去关掉检查，而不是去修依赖）。
Write-Host '   > dotnet list package --vulnerable --include-transitive' -ForegroundColor DarkGray
$vulnOutput = ''
try {
    $global:LASTEXITCODE = 0
    $vulnOutput = (& dotnet list src\FileBackupTool.Api\FileBackupTool.Api.csproj `
        package --vulnerable --include-transitive 2>&1 | Out-String)
} catch {
    $vulnOutput = ''
}

if ($vulnOutput -match '没有易受攻击的包|no vulnerable packages') {
    Write-Ok '依赖审计：没有已知漏洞的包（含传递依赖）'
} elseif ([string]::IsNullOrWhiteSpace($vulnOutput)) {
    Write-Warn2 '依赖审计没有得到任何输出，已跳过。发生产前请手工跑一次确认。'
} else {
    Write-Warn2 '依赖审计需要人工确认（下面不是结论，是原始输出）：'
    $vulnOutput -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 20 |
        ForEach-Object { Write-Host "        $($_.Trim())" -ForegroundColor DarkGray }
}

# -----------------------------------------------------------------------------
# 1. 测试
#    放在构建之前：测试失败时不该留下一个半成品发布目录让人误以为"发布成功了"。
# -----------------------------------------------------------------------------
if ($SkipTests) {
    Write-Step '1/6 测试（已按 -SkipTests 跳过）'
    Write-Warn2 '跳过了单元测试与集成测试。集成测试是设计文档 08 §1.1 的 CI 硬门槛。'
} else {
    Write-Step '1/6 测试'
    Invoke-Native 'dotnet' @('test', 'tests\FileBackupTool.UnitTests', '-c', $Configuration, '--nologo') '单元测试'
    Write-Ok '单元测试通过'
    Invoke-Native 'dotnet' @('test', 'tests\FileBackupTool.IntegrationTests', '-c', $Configuration, '--nologo') '集成测试'
    Write-Ok '集成测试通过（真实文件系统的往返一致性）'
}

# -----------------------------------------------------------------------------
# 2. 前端构建
# -----------------------------------------------------------------------------
if ($SkipWeb) {
    Write-Step '2/6 前端构建（已按 -SkipWeb 跳过）'
    if ($PathBase) {
        # 前缀是**构建期**注入的（Vite 的 base 与 import.meta.env 都是编译进产物的常量），
        # 跳过构建就不可能带上前缀。这种组合看起来"发布成功了"，实际产出的是根路径前端。
        Stop-Fail '-SkipWeb 与 -PathBase 不能一起用：路径前缀是前端构建期注入的，跳过构建就不可能带上它。'
    }
    if (-not (Test-Path 'src\FileBackupTool.Api\wwwroot\index.html')) {
        Stop-Fail 'wwwroot\index.html 不存在，而 -SkipWeb 又跳过了构建。这样出来的包里没有前端。'
    }
    Write-Warn2 '直接沿用了 wwwroot 里已有的产物，它不一定是最新的。'
} else {
    Write-Step '2/6 前端构建'
    $nodeModules = 'web\node_modules'
    if ($Clean -or -not (Test-Path $nodeModules)) {
        # npm ci 而不是 install：ci 严格按 package-lock.json 装，可复现。
        Invoke-Native 'npm' @('--prefix', 'web', 'ci') 'npm ci'
    } else {
        Write-Ok '复用 web\node_modules（想强制重装用 -Clean）'
    }

    # ---- 路径前缀：必须在 npm run build **之前**注入 ----
    # Vite 的 base 是构建期常量，编译进产物之后就没法再改了。
    #
    # 只注入 VITE_BASE 一个：接口路径与 Hub 路径在源码里从 Vite 内置的 BASE_URL
    # 派生（见 src/api/http.ts、src/realtime/client.ts），它们跟着 base 走。
    # 注入的变量越少，"某处写错/漏改"的可能越小。
    #
    # 先把三个都清掉再按需设：同一个会话里先跑 -PathBase、再跑不带参数的，
    # 残留的环境变量会让第二次构建悄悄带上前缀（那两个覆盖项残留了同样生效）。
    foreach ($v in @('VITE_BASE', 'VITE_API_BASE', 'VITE_HUB_PATH')) {
        [Environment]::SetEnvironmentVariable($v, $null)
    }
    $prefix = ''
    if ($PathBase) {
        $prefix = '/' + $PathBase.Trim('/')     # 'backup' / '/backup/' → '/backup'
        $env:VITE_BASE = "$prefix/"
        Write-Ok "路径前缀 $prefix/（资源、接口、Hub 都跟着它走）"
    }

    # build = type-check + vite build。类型错误会让它非 0 退出，正是我们要的：
    # 前端类型错误不该发到生产。
    Invoke-Native 'npm' @('--prefix', 'web', 'run', 'build') '前端构建'

    if (-not (Test-Path 'web\dist\index.html')) {
        Stop-Fail '前端构建报成功，但 web\dist\index.html 不存在。'
    }

    # 断言产物里真的是我们要的路径。
    #
    # 这一条是"注入到底生效了没有"的**唯一**证据：base 是构建期常量，脚本设了
    # 环境变量不等于 Vite 用了它。构建报成功而前缀没生效，症状是"部署上去白屏"，
    # 属于最难查的一类，所以在这里就拦下来。
    $builtHtml = Get-Content 'web\dist\index.html' -Raw -Encoding UTF8
    if ($prefix) {
        $expected = "$prefix/assets/"
        if ($builtHtml -notmatch [regex]::Escape($expected)) {
            Stop-Fail "产物里的资源路径不是预期的 $expected —— -PathBase 没有生效，部署上去就是白屏。"
        }
        if ($builtHtml -match '"\/assets/') {
            Stop-Fail '产物里同时存在根路径 /assets/ 引用 —— 前缀只生效了一半（部分资源会 404）。'
        }
        Write-Ok "已确认产物带前缀（$expected）"
    } elseif ($builtHtml -notmatch '"/assets/') {
        Stop-Fail '产物里的资源路径不是根路径 /assets/ —— 多出了一个前缀（查 web\.env*，或本会话残留的 VITE_BASE 环境变量）。'
    }
    Write-Ok 'web\dist 已生成'
}

# -----------------------------------------------------------------------------
# 3. 前端产物 → wwwroot
#    必须在 dotnet publish **之前**。反过来的话包里只有空 wwwroot，
#    症状是部署上去之后访问根路径得到"前端资源不存在"。
# -----------------------------------------------------------------------------
if (-not $SkipWeb) {
    Write-Step '3/6 拷贝前端产物到 wwwroot'
    $wwwroot = 'src\FileBackupTool.Api\wwwroot'
    if (-not (Test-Path $wwwroot)) { New-Item -ItemType Directory -Path $wwwroot | Out-Null }

    # 先清空（保留 .gitkeep）：旧版本的 hash 文件名不会互相覆盖，
    # 不清就会把历代前端 bundle 全部累积进包里。
    Get-ChildItem $wwwroot -Exclude '.gitkeep' -Force | Remove-Item -Recurse -Force
    Copy-Item 'web\dist\*' $wwwroot -Recurse -Force

    $fileCount = @(Get-ChildItem $wwwroot -Recurse -File).Count
    Write-Ok "wwwroot 现有 $fileCount 个文件"
}

# -----------------------------------------------------------------------------
# 4. 后端发布
# -----------------------------------------------------------------------------
Write-Step '4/6 dotnet publish'

if ($Clean -and (Test-Path $OutputDir)) {
    Remove-Item $OutputDir -Recurse -Force
    Write-Ok "已清空 $OutputDir"
}

# --self-contained true：见文件头说明。
# **不加** PublishTrimmed：本项目用 EF Core + 反射较多，裁剪会在**运行期**
# 以缺少类型的方式炸掉（编译期完全看不出来），而这是一个备份工具，不值得为体积冒这个险。
$publishArgs = @(
    'publish', 'src\FileBackupTool.Api\FileBackupTool.Api.csproj',
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $OutputDir,
    '--nologo'
)

# 关掉 SDK 的静态资源预压缩：它给每个前端文件额外生成 .gz/.br（38 → 114 个文件），
# 而本应用用的是经典 UseStaticFiles()，不认这些预压变体——实测带不带 Accept-Encoding
# 返回的都是未压缩原文，等于白占 76 个文件的位置。
# 注意：万一将来改用 MapStaticAssets()，这里必须去掉这个开关，否则白白丢掉压缩收益。
$publishArgs += '-p:CompressionEnabled=false'

if (-not $MultiFile) {
    # 单文件：355 个 dll 合成一个 exe。原生库（e_sqlite3.dll 等）本来就不能打包，
    # 仍然留在 exe 旁边，这是正常的。
    $publishArgs += '-p:PublishSingleFile=true'
}

Invoke-Native 'dotnet' $publishArgs 'dotnet publish'

$apiExe = Join-Path $OutputDir 'FileBackupTool.Api.exe'
if (-not (Test-Path $apiExe)) { Stop-Fail "$OutputDir 里没有 FileBackupTool.Api.exe，发布不完整。" }

if (-not (Test-Path (Join-Path $OutputDir 'wwwroot\index.html'))) {
    Stop-Fail "$OutputDir\wwwroot\index.html 不存在——前端没被打进包里。"
}

# appsettings.Production.json 是**生产机上的事实来源**，不该由开发机带过去。
# 万一有人手工建过这个文件，publish 会把它一起拷走，于是开发机的配置悄悄覆盖生产。
# 这不是理论风险，所以说出来。
if (Test-Path (Join-Path $OutputDir 'appsettings.Production.json')) {
    Write-Warn2 "$OutputDir 里出现了 appsettings.Production.json——它可能带着开发机的配置。"
    Write-Warn2 '生产机上安装时若该文件已存在，install-service.ps1 不会覆盖它，但仍请确认这个文件不该在这里。'
}
Write-Ok "已发布到 $OutputDir"
# -----------------------------------------------------------------------------
# 5. 附加部署件
# -----------------------------------------------------------------------------
Write-Step '5/6 附加部署件'

$deploySrc = Join-Path $root 'deploy'
$deployFiles = @(
    'install-service.ps1',
    'appsettings.Production.json.template',
    # 两份 nginx 样例对应**两种不同的拓扑**，转发头写法必须不同（见各自文件头）：
    #   nginx.conf.sample         本机自己终止 TLS、挂根路径
    #   nginx-subpath.sample.conf 本机只是内层一跳、挂子路径
    'nginx.conf.sample',
    'nginx-subpath.sample.conf'
)
foreach ($name in $deployFiles) {
    $from = Join-Path $deploySrc $name
    if (-not (Test-Path $from)) { Stop-Fail "缺少 deploy\$name。" }
    Copy-Item $from (Join-Path $OutputDir $name) -Force
}
Write-Ok ($deployFiles -join ' / ')

# VERSION.txt：出问题时第一个要问的就是"生产上跑的是哪份代码"。
# 把这个问题的答案写进包里，而不是靠回忆。
$builtAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
$versionText = @"
FileBackupTool 发布包

构建时间   : $builtAt
git 分支   : $(if ($branch) { $branch } else { '(未知)' })
git 提交   : $(if ($commit) { $commit } else { '(未知)' })
工作区状态 : $(if ($isDirty) { '有未提交改动——本包与任何提交都不完全对应' } else { '干净' })
配置       : $Configuration / $Runtime / 自包含
dotnet SDK : $(dotnet --version)

⚠ 本项目由 AI 开发，尚未完成、未达到可用级别，且未经人工测试。
  不要把它当成可依赖的备份工具——备份工具出错的代价是数据。
  详见仓库根目录 README.md。
"@
Set-Content -Path (Join-Path $OutputDir 'VERSION.txt') -Value $versionText -Encoding UTF8
Write-Ok 'VERSION.txt'

# 校验运行时是否真的带上了：自包含发布失败时，症状往往是"在生产机上启动就报找不到框架"，
# 那时才发现就太晚了。单文件模式下 hostfxr 被合进 exe，只能靠体积判断。
if ($MultiFile) {
    if (-not (Test-Path (Join-Path $OutputDir 'hostfxr.dll'))) {
        Stop-Fail "$OutputDir 里没有 hostfxr.dll——这不是一个自包含发布。"
    }
    Write-Ok '自包含运行时已随包带上（多文件布局）'
} else {
    $exeMb = [Math]::Round((Get-Item $apiExe).Length / 1MB, 1)
    if ($exeMb -lt 50) {
        Stop-Fail "单文件 exe 只有 $exeMb MB，不像是一个自包含发布（预期 100 MB 上下）。"
    }
    Write-Ok "自包含运行时已合进 exe（$exeMb MB）"

    # 原生库不能被打进单文件，必须留在旁边——尤其是 e_sqlite3.dll，
    # 少了它应用能启动，但一碰数据库就炸。
    if (-not (Test-Path (Join-Path $OutputDir 'e_sqlite3.dll'))) {
        Stop-Fail "$OutputDir 里没有 e_sqlite3.dll。SQLite 的原生库不会被单文件打包，必须留在 exe 旁边。"
    }
    Write-Ok 'e_sqlite3.dll 在 exe 旁边（SQLite 原生库，不能打包）'
}

# -----------------------------------------------------------------------------
# 6. 收尾
# -----------------------------------------------------------------------------
Write-Step '6/6 完成'

# 上一轮 -Zip 留下的包要先清掉。它就在输出目录里，不清的话：
#   ① 它会被算进下面的文件数统计，看起来像"包突然变大了"；
#   ② 更糟的是 Compress-Archive 会把它自己上一轮的结果一起压进新包，体积凭空翻倍。
Get-ChildItem $OutputDir -Filter 'FileBackupTool-*.zip' -File -ErrorAction SilentlyContinue | Remove-Item -Force

$allFiles = Get-ChildItem $OutputDir -Recurse -File
$totalMb  = [Math]::Round((($allFiles | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Ok "$OutputDir : $($allFiles.Count) 个文件，$totalMb MB"

if ($Zip) {
    $zipName = "FileBackupTool-$commitTag-$Configuration-$Runtime.zip"
    $zipPath = Join-Path $OutputDir $zipName
    # 先压到输出目录**之外**再挪进来，避免"正在写 zip、同时又在压 zip"的自包含。
    $tmpZip = Join-Path $env:TEMP $zipName
    if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force }
    Write-Host '   正在打包…' -ForegroundColor DarkGray
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $tmpZip -CompressionLevel Optimal
    Move-Item $tmpZip $zipPath -Force
    $zipMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Ok "$zipName ($zipMb MB)"
}

# 对外访问路径与"带前缀"提示先算好，再拼进下面的 here-string。
# ⚠️ 不要在 here-string 里内联**跨行**的双引号字符串——PowerShell 的普通字符串
# 不能跨行，那是个只在解析期暴露的语法错误。（这里踩过一次。）
$publicPath = if ($prefix) { "$prefix/" } else { '/' }
$pathNote = ''
if ($prefix) {
    $pathNote = @(
        "     注意：本次是带路径前缀构建的，对外地址是 https://<你的域名>$prefix/ ，不是根路径。"
        '     把 /backup 交给 nginx 而 wwwroot 是根路径版（或反之），两种都会白屏。'
    ) -join "`n"
}

Write-Host ''
Write-Host '生产机上的部署步骤：' -ForegroundColor Cyan
Write-Host @"
  1. 把 $OutputDir 整个目录拷到生产机，例如 C:\Apps\FileBackupTool
     （注意：**拷贝整个目录**，不是只拷 exe。）

  2. 在该目录里以**管理员**身份打开 PowerShell，注册并启动服务：
       cd C:\Apps\FileBackupTool
       .\install-service.ps1 -ServiceAccount '<服务账号>' [-AdminPassword '<初始密码>']
     服务账号必须是能读取备份源目录、且能写备份目录与数据目录的那个账号。
     数据目录默认 `$env:ProgramData\FileBackupTool（设计文档 01 §7.3 要求绝对路径）。

  3. nginx 侧按**你的拓扑**选一份样例（同目录下两份，各自的文件头都写明了适用条件）：
       · 本机 nginx 自己终止 TLS（域名与证书在本机、挂根路径）→ nginx.conf.sample
       · 本机 nginx 只是内层一跳（外层公网服务器终止 TLS 再转发过来）
         → nginx-subpath.sample.conf
     ⚠️ 选错不是"少一层防护"，而是**静默降级**：转发头写错会让登录限流退化成
        "全站共用一份配额"、WebSocket 头写错会让实时通道悄悄变成轮询。两份都不报错。

  4. 浏览器访问 https://<你的域名>$publicPath ，用初始密码登录，
     然后**第一件事**是配置还原根白名单（还原是写文件操作，白名单是最重要的安全缓解）。
$pathNote
"@ -ForegroundColor Gray
