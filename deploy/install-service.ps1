<#
.SYNOPSIS
    把 FileBackupTool 注册成 Windows 服务（在**生产机**上以管理员身份运行）。

.DESCRIPTION
    本脚本由 publish.ps1 随发布包一起产出，应当就放在发布目录里运行。
    它做四件事：

      1. 准备数据目录（绝对路径，设计文档 01 §7.3）并生成 appsettings.Production.json；
      2. 用 nssm 注册/更新服务，并配置优雅停机与自动重启；
      3. 启动服务；
      4. **验证它真的起来了**（服务状态 + HTTP 探活），失败就打印日志尾部并以非 0 退出。

    关于"不覆盖已有配置"：appsettings.Production.json 一旦存在就**原样保留**。
    重新发布、重跑本脚本都不会动它。要强制用模板重新生成，用 -Force。
    这是刻意的——配置里写着数据目录，改错了等于找不到自己的备份。

.PARAMETER ServiceName
    Windows 服务名，默认 FileBackupTool。

.PARAMETER InstallDir
    程序所在目录，默认本脚本所在目录（$PSScriptRoot）。

.PARAMETER DataDir
    数据目录：数据库、日志、DataProtection 密钥环、单实例锁都在这里。
    默认 $env:ProgramData\FileBackupTool。
    **必须绝对路径**：注册成服务后工作目录与双击运行不同，
    相对路径会解析到别处——数据会写到意想不到的地方（设计文档 01 §7.3）。

.PARAMETER Port
    Kestrel 监听端口，默认 5000。只监听 localhost，对外由 nginx 终止 TLS。

.PARAMETER NssmPath
    nssm.exe 的路径。不给则依次找 PATH、本目录下的 nssm.exe。

.PARAMETER ServiceAccount
    **必填**。服务以哪个账号运行，例如 'NT AUTHORITY\LocalService' 或 '.\backupuser'。
    没有默认值是刻意的：备份要读取源目录、写备份目录与数据目录，
    这个账号必须对这三处都有权限，只有你知道该用谁。设计文档 01 §6 明确要求
    **不要默认 LocalSystem**（权限过大，且一旦被利用就是整机）。

.PARAMETER ServiceAccountPassword
    服务账号的密码。账号是 'NT AUTHORITY\*' 内置账号或 LocalSystem 时不需要；
    是普通用户/DOMAIN 账号时**必填**（nssm 需要它来配置登录）。

.PARAMETER AdminPassword
    初始管理员密码，仅**首次**启动需要（账号建好后不再读取，见 IdentitySeeder）。
    不提供时会去读环境变量 FILEBACKUPTOOL_ADMIN_PASSWORD。
    ⚠️ 通过命令行传参会留在 PowerShell 历史里；更稳妥的是先设环境变量再运行本脚本。

.PARAMETER Force
    即使 appsettings.Production.json 已存在也用模板重新生成（会丢掉你改过的配置）。

.PARAMETER SkipStart
    只注册与配置服务，不启动、不验证。

.PARAMETER SetLongPaths
    把本机的注册表 LongPathsEnabled 设为 1（机器级改动）。
    不给的话只检查并警告，不动系统设置。

.PARAMETER Uninstall
    停掉并删除服务。**数据目录原样保留**，不会被删除。

.EXAMPLE
    .\install-service.ps1 -ServiceAccount 'NT AUTHORITY\LocalService'

.EXAMPLE
    .\install-service.ps1 -ServiceAccount '.\backupuser' -ServiceAccountPassword '...' -AdminPassword '...'
#>
# #Requires 必须放在帮助块**之后**：它是解析器指令而不是普通注释，
# 放在最前面会让 PowerShell 认为"帮助块不是文件开头"，于是整段注释帮助被忽略
# （Get-Help 只回一行语法）——实测于 PowerShell 7.6.6。
# 放在这里两个效果都保住：帮助能解析，#Requires 也照旧强制执行。
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ServiceName  = 'FileBackupTool',
    [string] $InstallDir   = $PSScriptRoot,
    [string] $DataDir      = (Join-Path $env:ProgramData 'FileBackupTool'),
    [int]    $Port         = 5000,
    [string] $NssmPath,
    [Parameter(Mandatory = $true)]
    [string] $ServiceAccount,
    [string] $ServiceAccountPassword,
    [string] $AdminPassword,
    [switch] $Force,
    [switch] $SkipStart,
    [switch] $SetLongPaths,
    [switch] $Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Ok([string] $Text)    { Write-Host "   OK   $Text" -ForegroundColor Green }
function Write-Warn2([string] $Text) { Write-Host "   WARN $Text" -ForegroundColor Yellow }
function Write-Err([string] $Text)   { Write-Host "   FAIL $Text" -ForegroundColor Red }
function Write-Step([string] $Text)  { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Stop-Fail([string] $Text)   { Write-Err $Text; exit 1 }

# 调 nssm 并**校验退出码**。
#
# 为什么必须校验：`nssm set` 失败时只返回一个非 0 码、不抛异常，脚本若用
# `| Out-Null` 吞掉就会继续往下走、最后打印"已配置"。最现实的一条是
# `set ObjectName <账号> <密码>`——密码错时它返回非 0，于是服务最后以
# "登录失败(1069)"起不来，而那时脚本报的是探活超时，看不出是哪一条设置没成。
# 一个假的成功比一个失败更糟。
function Invoke-Nssm([string[]] $CmdArgs, [string] $What) {
    $global:LASTEXITCODE = 0
    & $NssmPath @CmdArgs | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Stop-Fail "nssm $($CmdArgs[0]) 失败（$What，退出码 $LASTEXITCODE）。服务未被正确配置。"
    }
}

# -----------------------------------------------------------------------------
# 0. 前置检查
# -----------------------------------------------------------------------------
Write-Step '0/6 前置检查'

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Stop-Fail '需要管理员权限（注册服务、写 ProgramData）。请以管理员身份重新打开 PowerShell。'
}
Write-Ok "管理员权限（当前账号 $($identity.Name)）"

# nssm：注册服务全靠它。
if (-not $NssmPath) {
    $found = Get-Command 'nssm' -ErrorAction SilentlyContinue
    if ($found) { $NssmPath = $found.Source }
    elseif (Test-Path (Join-Path $InstallDir 'nssm.exe')) { $NssmPath = Join-Path $InstallDir 'nssm.exe' }
}
if (-not $NssmPath -or -not (Test-Path $NssmPath)) {
    Stop-Fail @'
找不到 nssm.exe。请从 https://nssm.cc/download 下载后：
  · 放到 PATH 里，或
  · 放到本目录（发布目录）下，或
  · 用 -NssmPath 'C:\tools\nssm\nssm.exe' 指定。
'@
}
$NssmPath = (Resolve-Path $NssmPath).Path
Write-Ok "nssm: $NssmPath"

$InstallDir = (Resolve-Path $InstallDir).Path
$apiExe = Join-Path $InstallDir 'FileBackupTool.Api.exe'
if (-not (Test-Path $apiExe)) {
    Stop-Fail "$InstallDir 里没有 FileBackupTool.Api.exe。本脚本要放在发布目录里运行，或用 -InstallDir 指定。"
}
Write-Ok "程序目录: $InstallDir"

# 服务账号的密码：只有内置账号可以不填。
$isBuiltInAccount = $ServiceAccount -like 'NT AUTHORITY\*' -or $ServiceAccount -eq 'LocalSystem' -or $ServiceAccount -eq 'LocalService' -or $ServiceAccount -eq 'NetworkService'
if (-not $isBuiltInAccount -and [string]::IsNullOrEmpty($ServiceAccountPassword)) {
    Stop-Fail "服务账号 $ServiceAccount 是普通账号，必须提供 -ServiceAccountPassword（nssm 要用它配置服务登录）。"
}
Write-Ok "服务账号: $ServiceAccount"

# -----------------------------------------------------------------------------
# 卸载分支
# -----------------------------------------------------------------------------
if ($Uninstall) {
    Write-Step "卸载服务 $ServiceName"
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Warn2 "服务 $ServiceName 不存在，无需卸载。"
    } else {
        & $NssmPath stop $ServiceName | Out-Null
        Start-Sleep -Seconds 2
        & $NssmPath remove $ServiceName confirm | Out-Null
        if ($LASTEXITCODE -ne 0) { Stop-Fail "nssm remove 失败（退出码 $LASTEXITCODE）。" }
        Write-Ok "服务 $ServiceName 已删除"
    }
    Write-Host ''
    Write-Host "数据目录未被删除：$DataDir" -ForegroundColor Yellow
    Write-Host '  数据库、日志、DataProtection 密钥环都还在那里。确认不再需要后再手工清理。' -ForegroundColor Gray
    exit 0
}

# -----------------------------------------------------------------------------
# 1. 数据目录
#    logs 子目录必须**在服务启动之前**存在：nssm 在拉起进程之前就要打开
#    stdout/stderr 重定向文件，目录不存在会直接导致服务起不来。
#    而 Serilog 自己的日志目录也要在这里，否则"日志文件不见了"成为第一个
#    也最难查的故障（Program.cs 里就是这么写的）。
# -----------------------------------------------------------------------------
Write-Step '1/6 数据目录与运行环境'

if (Test-Path $DataDir) {
    Write-Ok "已存在：$DataDir（不动它的内容）"
} else {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    Write-Ok "已创建：$DataDir"
}
$logDir = Join-Path $DataDir 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
Write-Ok "日志目录：$logDir"

# 数据目录落在程序目录里是个隐患：备份源若包含程序目录，就会把数据库和
# blob 池一起备份进去（自我备份），而且重新发布覆盖程序目录时可能连带删掉数据。
if ($DataDir.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Warn2 "数据目录在程序目录内部。重新发布覆盖程序目录时有丢数据的风险，建议改到别处（如 $env:ProgramData\FileBackupTool）。"
}

# 可写性探测 —— ⚠️ 它是以**当前账号（管理员）**跑的，**不是服务账号**。
#
# 为什么不能替服务账号探：那需要拿它的密码去模拟登录，而内置账号
#（NT AUTHORITY\LocalService 等）根本没有可用密码。
# 所以这个探测只能回答"这个目录**现在**能写"，回答不了"服务账号能写"。
# 而后者恰恰是 ProgramData 部署的经典坑：目录是管理员建的，ACL 只继承
# ProgramData 的默认值，LocalService 一类账号对管理员新建的子目录往往没有写权限。
#
# 真正的判定在第 5 步的启动验证：若服务因写不进去而拒绝启动，探活会失败并打出
# 日志尾部（那时日志里是明确的「数据目录不可写」），失败诊断里给了补救命令。
try {
    $probe = Join-Path $DataDir '.write-probe'
    Set-Content -Path $probe -Value 'probe' -Encoding ASCII
    Remove-Item $probe -Force
    Write-Ok "数据目录可写（以当前账号 $($identity.Name) 探测；**不代表服务账号 $ServiceAccount 能写**）"
} catch {
    Stop-Fail "数据目录不可写：$DataDir（以当前账号 $($identity.Name) 探测）。请确认该账号对它有写权限。"
}

# -----------------------------------------------------------------------------
# 1b. 长路径（设计文档 01 §6.1）
#
# 应用清单里声明了 longPathAware，但那只解决"**进程**允许长路径"这一半；
# 目标机器还必须把注册表 LongPathsEnabled 设为 1。**两者缺一，超长路径依然抛
# PathTooLongException**。
#
# 为什么这里要专门检查：PathTooLongException 是 IOException 的子类，会被引擎的
# 逐文件失败处理**吞掉，变成"静默跳过"**（01 §6.1 原文）。对一个备份工具来说
# 这是最不能接受的失败形态——文件没备份上，而界面显示成功。
# -----------------------------------------------------------------------------
$lpKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem'
$lpValue = (Get-ItemProperty -Path $lpKey -Name 'LongPathsEnabled' -ErrorAction SilentlyContinue).LongPathsEnabled

if ($lpValue -eq 1) {
    Write-Ok '长路径已启用（LongPathsEnabled=1）'
} elseif ($SetLongPaths) {
    if ($null -eq (Get-ItemProperty -Path $lpKey -Name 'LongPathsEnabled' -ErrorAction SilentlyContinue)) {
        New-ItemProperty -Path $lpKey -Name 'LongPathsEnabled' -Value 1 -PropertyType DWord -Force | Out-Null
    } else {
        Set-ItemProperty -Path $lpKey -Name 'LongPathsEnabled' -Value 1 | Out-Null
    }
    Write-Ok '已设置 LongPathsEnabled=1'
    Write-Warn2 '该设置对**之后启动**的进程生效；服务是稍后才启动的，正常无需重启机器。拿不准就重启一次。'
} else {
    # 先把要显示的取值算成字符串再拼：直接往双引号字符串里塞 $(if ... { } else { })
    # 很容易被中文全角括号带偏（写成 }）而不是 })，而那种错只在解析期暴露。
    $lpText = if ($null -eq $lpValue) { '不存在，等同 0' } else { $lpValue }
    Write-Warn2 "本机 LongPathsEnabled 未启用（当前值：$lpText）。"
    Write-Warn2 '后果：超过约 260 字符的路径会抛 PathTooLongException，而它会被逐文件失败处理'
    Write-Warn2 '      吞掉、变成**静默跳过**——文件没备份上，界面却显示成功。'
    Write-Warn2 '现在启用请重跑并加 -SetLongPaths（机器级注册表改动，需管理员）。'
}

# -----------------------------------------------------------------------------
# 2. appsettings.Production.json
#    已存在就**原样保留**——重新发布不会覆盖生产配置。
# -----------------------------------------------------------------------------
Write-Step '2/6 生产配置'

$prodConfig = Join-Path $InstallDir 'appsettings.Production.json'
$template   = Join-Path $InstallDir 'appsettings.Production.json.template'

if ((Test-Path $prodConfig) -and -not $Force) {
    Write-Ok "已存在，未改动：$prodConfig"
    # 已存在的配置可能监听别的端口——探活必须按**配置里**的端口来，
    # 否则会出现"服务明明起来了，脚本却说没起来"。
    # -Encoding UTF8 是必须的：Windows PowerShell 5.1 在没有 BOM 时按 ANSI 读文件，
    # 会把配置里的中文注释读成乱码，再写回去就永久损坏了。
    $cfgText = Get-Content $prodConfig -Raw -Encoding UTF8
    if ($cfgText -match 'http://localhost:(\d+)') {
        $configuredPort = [int]$Matches[1]
        if ($configuredPort -ne $Port) {
            Write-Warn2 "配置里监听 $configuredPort，与 -Port $Port 不一致；按配置里的 $configuredPort 探活。"
            $Port = $configuredPort
        }
    }
} else {
    if (-not (Test-Path $template)) {
        Stop-Fail "缺少 $template，无法生成生产配置。"
    }

    # 反斜杠要转义成 \\ ——JSON 里单个反斜杠是非法转义序列，
    # 忘了这一步会得到一个**语法错误**的配置文件，服务启动直接失败。
    $dataDirJson = $DataDir.Replace('\', '\\')

    $templateText = Get-Content $template -Raw -Encoding UTF8

    # 先校验**模板本身**含占位符，再做替换。
    # 顺序很重要：下面的 Replace 是无条件的，所以"替换完再查有没有残留"其实是
    # 永远查不出来的死代码。真正的风险是模板被改坏（比如误删了占位符）——
    # 那时生成的配置会静静地用上错的数据目录。
    foreach ($token in @('__DATA_DIR__', '__PORT__')) {
        if ($templateText -notmatch [regex]::Escape($token)) {
            Stop-Fail "模板 $template 里没有 $token 占位符——生成的配置不会带上正确的值，拒绝继续。"
        }
    }

    $cfgText = $templateText.
        Replace('__DATA_DIR__', $dataDirJson).
        Replace('__PORT__', $Port.ToString())

    # 再校验结果：替换后不该有占位符残留（防的是将来改了模板形态）。
    if ($cfgText -match '__DATA_DIR__|__PORT__') {
        Stop-Fail '模板占位符没有被完全替换，生成的配置不可用。'
    }

    # **校验通过之后**才落盘，而且先写临时文件再原子替换。
    # 否则一旦中途失败，磁盘上会留下一个半成品配置；而下次运行会命中上面
    # "已存在就原样保留"那个分支，把它当成"生产机上的事实来源"原样保留——
    # 一个错误就被固化下来了。
    $tmpConfig = "$prodConfig.tmp"
    Set-Content -Path $tmpConfig -Value $cfgText -Encoding UTF8
    Move-Item -Path $tmpConfig -Destination $prodConfig -Force
    Write-Ok "已生成：$prodConfig"

    Write-Warn2 '请打开它确认 ReverseProxy:TrustedProxies 是 nginx 的地址（同机就是 127.0.0.1）。'
}

# -----------------------------------------------------------------------------
# 3. 服务注册
# -----------------------------------------------------------------------------
Write-Step '3/6 注册服务'

$serviceExists = $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)

if ($serviceExists) {
    # 先停：正在运行的进程会锁住 exe 与数据目录锁文件，
    # 而且此时替换程序文件正是"两个进程同时写同一批 blob"的经典起因（01 §6）。
    Write-Host '   服务已存在，先停止以便更新…' -ForegroundColor DarkGray

    # **暂停态不能直接 STOP。** Windows 要求先把服务 continue 恢复，才接受 stop。
    # 而"暂停"恰是本程序最常见的状态：启动即崩溃时 nssm 会暂停服务以阻止重启风暴，
    # 所以"第一次部署失败、再来一次"几乎必然撞上它。
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Paused') {
        & $NssmPath continue $ServiceName | Out-Null
        Start-Sleep -Seconds 1
        Write-Host '   （服务处于暂停态，已先恢复）' -ForegroundColor DarkGray
    }

    & $NssmPath stop $ServiceName | Out-Null

    # **必须确认真的停了**，不能 sleep 两秒就宣称"已停止"。
    # 原始版本就是那么写的，于是在暂停态下会打印"已停止"而服务其实还在——
    # 一个假的成功比一个失败更糟。
    $stopDeadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $stopDeadline) {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
    }

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Fail "服务 $ServiceName 没能停下来（当前状态：$($svc.Status)）。请手工处理后再重跑：nssm continue $ServiceName ; nssm stop $ServiceName"
    }
    Write-Ok '已停止'
} else {
    & $NssmPath install $ServiceName $apiExe | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-Fail "nssm install 失败（退出码 $LASTEXITCODE）。" }
    Write-Ok "已注册：$ServiceName"
}

# ---- 可执行文件路径：**每次都要设**，不能只在新建时设 ----
#
# 服务已存在时，上面走的只是 stop + 各种 set，**没有**重设 Application。
# 那一项的后果不是"少配置一条"：把新包放到另一个目录（例如 C:\Apps\FileBackupTool-2.0）
# 再跑本脚本，exe 仍是旧路径、而内容目录（AppDirectory）指向新目录 ——
# 于是"新构建根本没在跑，旧 exe 用着新的 appsettings/wwwroot"这种混合态，
# 而第 5 步的探活是问端口，旧进程照答 200 → 脚本打印「服务运行中」并返回 0。
# 没部署被包装成部署成功，是这套流程里最不能接受的一种失败。
Invoke-Nssm @('set', $ServiceName, 'Application', $apiExe) 'Application(可执行文件路径)'

# 工作目录：设成程序目录。DataDir 用绝对路径，所以即使这里设错数据也不会写飞，
# 但设对了才能让"相对路径"这一项有确定含义。
Invoke-Nssm @('set', $ServiceName, 'AppDirectory', $InstallDir) 'AppDirectory(工作目录)'
Invoke-Nssm @('set', $ServiceName, 'DisplayName', $ServiceName) 'DisplayName'
Invoke-Nssm @('set', $ServiceName, 'Description', 'FileBackupTool：服务端常驻、浏览器控制的文件备份工具。') 'Description'
Invoke-Nssm @('set', $ServiceName, 'Start', 'SERVICE_AUTO_START') 'Start(自动启动)'
if ($isBuiltInAccount) {
    # 内置账号不接受（也不该接受）密码参数；多传一个空串会让 nssm 困惑。
    Invoke-Nssm @('set', $ServiceName, 'ObjectName', $ServiceAccount) 'ObjectName(服务账号)'
} else {
    Invoke-Nssm @('set', $ServiceName, 'ObjectName', $ServiceAccount, $ServiceAccountPassword) 'ObjectName(服务账号)'
}

# nssm 自己抓的 stdout/stderr：应用**来不及**配置 Serilog 就崩的场景
#（例如配置文件的 JSON 语法错误）只有这里能看到原因。
Invoke-Nssm @('set', $ServiceName, 'AppStdout', (Join-Path $logDir 'service-out.log')) 'AppStdout'
Invoke-Nssm @('set', $ServiceName, 'AppStderr', (Join-Path $logDir 'service-err.log')) 'AppStderr'
Invoke-Nssm @('set', $ServiceName, 'AppRotateFiles', '1') 'AppRotateFiles'
Invoke-Nssm @('set', $ServiceName, 'AppRotateOnline', '1') 'AppRotateOnline'
Invoke-Nssm @('set', $ServiceName, 'AppRotateBytes', '10485760') 'AppRotateBytes'

# 优雅停机：给进程时间冲刷 SQLite、释放数据目录锁。
# 注意：停服务会**中断正在进行的备份**（残留的 InProgress 快照会在下次启动时
# 被启动自愈标成 Aborted）。别在备份跑到一半时停服务。
Invoke-Nssm @('set', $ServiceName, 'AppStopMethodConsole', '20000') 'AppStopMethodConsole'

# 意外退出自动重启；但 AppThrottle 保证"起来就崩"的情况不会变成疯狂重启风暴。
Invoke-Nssm @('set', $ServiceName, 'AppExit', 'Default', 'Restart') 'AppExit'
Invoke-Nssm @('set', $ServiceName, 'AppRestartDelay', '5000') 'AppRestartDelay'
Invoke-Nssm @('set', $ServiceName, 'AppThrottle', '10000') 'AppThrottle'

Write-Ok "可执行文件 / 工作目录 / 账号 / 日志重定向 / 自动重启 已配置"

# -----------------------------------------------------------------------------
# 4. 环境变量
# -----------------------------------------------------------------------------
Write-Step '4/6 环境变量'

$dbPath = Join-Path $DataDir 'filebackuptool.db'
$dbExists = Test-Path $dbPath
$passwordSupplied = $false

if (-not $AdminPassword) {
    $AdminPassword = $env:FILEBACKUPTOOL_ADMIN_PASSWORD
}

$envPairs = @('ASPNETCORE_ENVIRONMENT=Production')

if ($AdminPassword) {
    $envPairs += "FILEBACKUPTOOL_ADMIN_PASSWORD=$AdminPassword"
    $passwordSupplied = $true
    Write-Ok '已设置初始管理员密码（仅首次启动会用到）'
} elseif (-not $dbExists) {
    # 数据目录里连数据库都没有 → 这是一次全新部署，缺密码时服务**一定会**拒绝启动
    #（IdentitySeeder 的设计如此：宁可不启动，也不用默认密码起来让人以为门是锁着的）。
    # 与其让用户去看日志猜，不如现在就停下来。
    Stop-Fail @"
这是一次全新部署（数据目录里没有数据库），但没有提供初始管理员密码。
服务会拒绝启动——这是刻意的设计，不会自动生成密码。

请任选一种：
  · 设环境变量后重跑：`$env:FILEBACKUPTOOL_ADMIN_PASSWORD = '<密码>'; .\install-service.ps1 ...
  · 或用参数：        .\install-service.ps1 -AdminPassword '<密码>' ...

密码策略：至少 12 位，且同时包含大写、小写、数字与符号，且不能与账号名（admin）相同。
数据目录：$DataDir
"@
} else {
    # ⚠️ "库存在"**不等于**"账号已建好"，所以这里只说"这次不要求密码"，不说"不需要密码"。
    #
    # 迁移在建号**之前**执行。上一次启动若在建号那一步失败（最常见的就是密码不合规），
    # 结果正是：filebackuptool.db 已经在了，而 AspNetUsers 里一行都没有。
    # 这个坑真的踩过：第一次部署用了个 8 位纯数字密码，服务反复秒退，
    # 然后重跑时这一分支会说"数据库已存在，无需初始密码"——服务照样起不来，
    # 而且看起来像另一个问题。
    #
    # 想真正判断账号有没有建好，得读 SQLite 的 AspNetUsers 表，而生产机上不该假设有
    # sqlite3.exe。所以这里不猜：如实说明、并把补救方式写清楚。密码多设一次是无害的
    #（IdentitySeeder 在账号已存在时直接 return，根本不读这个环境变量）。
    Write-Warn2 "数据目录里已有数据库（$dbPath），所以这次不要求初始密码。"
    Write-Warn2 '但"库存在"不等于"账号已建好"：迁移先于建号，上一次启动若在建号那一步失败，'
    Write-Warn2 '库已经在了而账号仍不存在。若服务随后报"拒绝启动：初始账号无法建立"，'
    Write-Warn2 '请带上 -AdminPassword（或设 FILEBACKUPTOOL_ADMIN_PASSWORD）重跑。'
}

# AppEnvironmentExtra 是**整体替换**，所以一次性传全。
Invoke-Nssm (@('set', $ServiceName, 'AppEnvironmentExtra') + $envPairs) 'AppEnvironmentExtra(环境变量)'
# 打印时把密码打码：这段输出会被截屏贴进工单。
Write-Ok (($envPairs | ForEach-Object {
    if ($_ -like '*PASSWORD*') { ($_ -split '=')[0] + '=***' } else { $_ }
}) -join '  ')
# -----------------------------------------------------------------------------
# 5. 启动并验证
#    这一步是整份脚本里最重要的：**不做验证的部署脚本等于把"我以为它起来了"
#    写进流程里**。服务状态 + HTTP 探活都过了才算成功。
# -----------------------------------------------------------------------------
if ($SkipStart) {
    Write-Step '5/6 启动（已按 -SkipStart 跳过）'
    Write-Warn2 "服务已配置但未启动。手工启动：nssm start $ServiceName"
    exit 0
}

Write-Step '5/6 启动并验证'

& $NssmPath start $ServiceName | Out-Null
Write-Host '   等待服务就绪…' -ForegroundColor DarkGray

$url = "http://localhost:$Port/"
$ok = $false
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1500

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne 'Running') { continue }

    # 根路径返回 index.html（静态文件中间件在认证与限流之前短路），
    # 所以 200 就足以证明"进程活着且在正常服务请求"。
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
        if ($resp.StatusCode -eq 200) { $ok = $true; break }
    } catch {
        # 还没起来，继续等。
    }
}
if (-not $ok) {
    Write-Err '服务未能通过验证（90 秒内没有在根路径返回 200）。'
    Write-Host ''
    # 最常见的一种原因单独点出来：第 1 步那个可写性探测是**以管理员身份**跑的，
    # 所以它通过不代表服务账号能写（见那一处的注释）。
    Write-Host '若下面的日志里出现「数据目录不可写」，给服务账号补一条授权即可：' -ForegroundColor Yellow
    Write-Host ('  icacls "' + $DataDir + '" /grant "' + $ServiceAccount + ':(OI)(CI)M" /T') -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '--- 服务状态 ---' -ForegroundColor Yellow
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue | Format-Table Status, Name -AutoSize | Out-String | Write-Host

    foreach ($logName in @('service-err.log', 'service-out.log')) {
        $p = Join-Path $logDir $logName
        Write-Host "--- $logName 末尾 25 行 ---" -ForegroundColor Yellow
        if (Test-Path $p) { Get-Content $p -Tail 25 | Write-Host } else { Write-Host '(不存在)' }
    }

    # Serilog 的日志：如果进程已经跑到了应用内部，真正的原因在这里。
    $appLogs = @(Get-ChildItem $logDir -Filter 'filebackuptool-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    if ($appLogs.Count -gt 0) {
        Write-Host "--- $($appLogs[0].Name) 末尾 25 行 ---" -ForegroundColor Yellow
        Get-Content $appLogs[0].FullName -Tail 25 | Write-Host
    }

    Stop-Fail '部署未完成：服务没起来。上面的日志里有原因。'
}

Write-Ok "服务运行中，$url 返回 200"
# -----------------------------------------------------------------------------
# 6. 收尾
# -----------------------------------------------------------------------------
Write-Step '6/6 完成'

Write-Host ''
Write-Host "  服务名   : $ServiceName"
Write-Host "  程序目录 : $InstallDir"
Write-Host "  数据目录 : $DataDir"
Write-Host "  监听     : $url （仅本机，对外由 nginx 终止 TLS）"
Write-Host "  应用日志 : $logDir\filebackuptool-*.log"
Write-Host "  nssm 日志: $logDir\service-out.log / service-err.log"
Write-Host ''

if ($passwordSupplied) {
    Write-Host '接下来做两件事：' -ForegroundColor Cyan
    Write-Host @"
  1. 去掉服务里的初始密码。它只在建号那一次被读取，之后不再需要，
     而 nssm 会把它**明文**存在注册表里。执行：
       nssm set $ServiceName AppEnvironmentExtra ASPNETCORE_ENVIRONMENT=Production

  2. 登录后第一件事是配置**还原根白名单**——还原是写文件操作，
     白名单是最重要的安全缓解（设计文档 04 §1.3）。现在的初始值是空列表。

  浏览器访问 https://<你的域名>/ （nginx 配好之前也可以先用 $url）。
"@ -ForegroundColor Gray
} else {
    Write-Host '提醒：登录后请确认还原根白名单已配置（还原是写文件操作，白名单是最重要的安全缓解）。' -ForegroundColor Gray
}
