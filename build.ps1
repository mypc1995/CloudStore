<#
.SYNOPSIS
    编译并发布 R2 Manager。

.EXAMPLE
    .\build.ps1
    编译 Release 版并输出到 dist\（免安装自包含单文件 exe）

.EXAMPLE
    .\build.ps1 -SelfContained:$false
    输出依赖运行时版（约 2 MB，目标机需装 .NET 10 Desktop Runtime）

.EXAMPLE
    .\build.ps1 -Run
    编译后顺便启动一次
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [switch] $SelfContained = $true,

    # 打包安装程序（需要本机装有 Inno Setup 6）
    [switch] $Installer,

    [switch] $Run
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\R2Manager\R2Manager.csproj'
$output = Join-Path $PSScriptRoot ($(if ($SelfContained) { 'dist' } else { 'dist-framework-dependent' }))

if (-not (Test-Path $project)) {
    throw "找不到项目文件：$project"
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    throw '未找到 dotnet，请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download'
}

Write-Host "==> 还原依赖 (dotnet restore)" -ForegroundColor Cyan
& $dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "restore 失败（退出码 $LASTEXITCODE）" }

Write-Host "==> 发布 ($Configuration, selfContained=$SelfContained)" -ForegroundColor Cyan
& $dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained "$SelfContained" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:PublishTrimmed=false `
    --output $output

if ($LASTEXITCODE -ne 0) { throw "publish 失败（退出码 $LASTEXITCODE）" }

$exe = Join-Path $output 'R2Manager.exe'
if (-not (Test-Path $exe)) {
    # 程序集名改成了中文，产物文件名也跟着变，这里按实际产物取。
    # 注意 dist\ 里还躺着编译好的安装程序，不能把它当成主程序——
    # 否则算出来的 SHA256 是安装包的，用户核对时对不上。
    $exe = Get-ChildItem -Path $output -Filter '*.exe' |
        Where-Object { $_.Name -notlike '*安装程序*' } |
        Select-Object -First 1
    if (-not $exe) { throw "发布完成但在 $output 里没找到 exe" }
    $exe = $exe.FullName
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 完成：$exe（$size MB）" -ForegroundColor Green

# 随包附上使用说明与哈希校验值：
#   * 使用说明是给最终用户的上手指引，含首次启动等待等常见问题
#   * SHA256 让反馈者能确认下载的文件没损坏（也便于排查杀软误报）
$readme = Join-Path $PSScriptRoot 'docs\使用说明.txt'
if (Test-Path $readme) {
    Copy-Item $readme (Join-Path $output '使用说明.txt') -Force
}

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$exeName = Split-Path $exe -Leaf
@(
    "$hash  $exeName",
    '',
    "文件大小：$size MB",
    "生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    '',
    '校验方法（PowerShell）：',
    "  Get-FileHash -Algorithm SHA256 -Path '$exeName'",
    '',
    '校验方法（CMD）：',
    "  certutil -hashfile $exeName SHA256"
) | Set-Content -Path (Join-Path $output 'SHA256.txt') -Encoding UTF8

Write-Host '==> 已生成 使用说明.txt 与 SHA256.txt' -ForegroundColor Green

if ($Installer) {
    # 安装包用 Inno Setup 编译。找不到 ISCC 就明说，别静默跳过——
    # 免得以为出了安装包、其实没有。
    $iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

    if (-not (Test-Path $iscc)) {
        $iscc = Get-ChildItem 'C:\Program Files*\Inno Setup 6\ISCC.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if (-not $iscc) {
        throw '未找到 Inno Setup 6（ISCC.exe）。请先安装：https://jrsoftware.org/isdl.php'
    }

    Write-Host '==> 编译安装程序 (Inno Setup)' -ForegroundColor Cyan

    # 版本号从 Directory.Build.props 读，那是唯一来源；不在安装脚本里再手写一份。
    $propsFile = Join-Path $PSScriptRoot 'Directory.Build.props'
    $appVersion = '1.0.0'

    if (Test-Path $propsFile) {
        $match = Select-String -Path $propsFile -Pattern '<Version>([^<]+)</Version>' |
            Select-Object -First 1

        if ($match -and $match.Matches.Count -gt 0) {
            $appVersion = $match.Matches[0].Groups[1].Value.Trim()
        }
    }

    Write-Host "    版本号：$appVersion（来自 Directory.Build.props）"

    & $iscc "/DMyAppVersion=$appVersion" (Join-Path $PSScriptRoot 'installer\云存储管家.iss')
    if ($LASTEXITCODE -ne 0) { throw "安装包编译失败（退出码 $LASTEXITCODE）" }

    $setup = Join-Path $PSScriptRoot 'dist\云存储管家-安装程序.exe'
    $setupSize = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "==> 完成：$setup（$setupSize MB）" -ForegroundColor Green
}

if ($Run) {
    Write-Host '==> 启动 云存储管家' -ForegroundColor Cyan
    Start-Process -FilePath $exe
}
