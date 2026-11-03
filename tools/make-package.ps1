<#
.SYNOPSIS
  产出可直接拷到别的 Windows x64 电脑上运行的便携包（自包含运行时）。

.DESCRIPTION
  1. 构建前端（renderer/dist）
  2. 自包含发布宿主（win-x64，不需要目标机器装 .NET 8 桌面运行时）
  3. 把 ffmpeg.exe 拷进 dist\bin（B站 AAC 音轨必须靠它实时转码；找不到就跳过并提示）
  4. 写一份《使用说明.txt》
  5. 打成 artifacts\music-player-<版本>-win-x64.zip

.EXAMPLE
  pwsh -NoProfile -File tools/make-package.ps1

.EXAMPLE
  # 只发布不压缩，方便本地先跑一遍
  pwsh -NoProfile -File tools/make-package.ps1 -NoZip
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # 跳过前端构建（已经 pnpm build:web 过时可省时间）
    [switch]$NoWebBuild,

    # 只产出 dist\ 目录，不压缩
    [switch]$NoZip,

    # 不把 ffmpeg.exe 打进包里
    [switch]$NoFfmpeg,

    # 指定要内置的 ffmpeg.exe（默认自动找；想减体积可指向精简构建）
    [string]$FfmpegPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'host\MusicPlayer.Host.csproj'
$dist = Join-Path $repo 'dist'
$artifacts = Join-Path $repo 'artifacts'

function Write-Step($text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Get-FfmpegPath {
    $fromEnv = $env:MUSICPLAYER_FFMPEG
    if ($fromEnv -and (Test-Path -LiteralPath $fromEnv)) { return (Resolve-Path -LiteralPath $fromEnv).Path }

    foreach ($dir in ($env:PATH -split ';')) {
        if (-not $dir) { continue }
        $candidate = Join-Path $dir.Trim() 'ffmpeg.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path -LiteralPath $winget) {
        foreach ($pkg in (Get-ChildItem -LiteralPath $winget -Directory -Filter 'Gyan.FFmpeg*' -ErrorAction SilentlyContinue)) {
            $found = Get-ChildItem -LiteralPath $pkg.FullName -Recurse -Filter 'ffmpeg.exe' -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }

    return $null
}

function Get-YtDlpPath {
    # 只认独立 exe（官方 release 约 17MB）。pip 安装出来的
    # %APPDATA%\Python\...\Scripts\yt-dlp.exe 只是个 0.1MB 启动垫片，
    # 换台没装 Python 的机器就跑不起来，绝不能打进便携包。
    $minBytes = 2MB

    $fromEnv = $env:MUSICPLAYER_YTDLP
    if ($fromEnv -and (Test-Path -LiteralPath $fromEnv) -and (Get-Item -LiteralPath $fromEnv).Length -ge $minBytes) {
        return (Resolve-Path -LiteralPath $fromEnv).Path
    }

    foreach ($dir in ($env:PATH -split ';')) {
        if (-not $dir) { continue }
        $candidate = Join-Path $dir.Trim() 'yt-dlp.exe'
        if ((Test-Path -LiteralPath $candidate) -and (Get-Item -LiteralPath $candidate).Length -ge $minBytes) {
            return $candidate
        }
    }

    # 本机是 pip 版（找到的只是垫片）时返回 null，交给下面的引导提示
    return $null
}

# ---------- 版本号 ----------
$version = (Select-String -LiteralPath $project -Pattern '<Version>\s*([^<]+?)\s*</Version>').Matches[0].Groups[1].Value
if (-not $version) { $version = '0.0.0' }
Write-Host "音乐播放器 便携包  $version  ($Runtime, $Configuration)" -ForegroundColor Green

# ---------- 1. 前端 ----------
if ($NoWebBuild) {
    Write-Step '跳过前端构建（-NoWebBuild）'
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'renderer\dist\index.html'))) {
        throw 'renderer/dist/index.html 不存在，不能跳过前端构建。'
    }
}
else {
    Write-Step '构建前端（renderer → dist）'
    & pnpm --filter renderer build
    if ($LASTEXITCODE -ne 0) { throw "前端构建失败（exit $LASTEXITCODE）" }
}

# ---------- 2. 自包含发布 ----------
Write-Step "发布宿主到 dist\（自包含 $Runtime，目标机器无需安装 .NET）"
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }

& dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:SelfContained=true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

# ---------- 3. 随包命令行工具 ----------
$binDir = Join-Path $dist 'bin'
$bundled = @()

if (-not $NoFfmpeg) {
    Write-Step '放入 ffmpeg（B站 AAC 音轨实时转码需要）'
    $ffmpeg = if ($FfmpegPath) { $FfmpegPath } else { Get-FfmpegPath }
    if ($ffmpeg -and (Test-Path -LiteralPath $ffmpeg)) {
        New-Item -ItemType Directory -Path $binDir -Force | Out-Null
        Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $binDir 'ffmpeg.exe') -Force
        $sizeMb = [math]::Round((Get-Item -LiteralPath (Join-Path $binDir 'ffmpeg.exe')).Length / 1MB, 1)
        Write-Host "    已内置 ffmpeg.exe（$sizeMb MB）← $ffmpeg" -ForegroundColor DarkGray
        if ($sizeMb -gt 150) {
            Write-Host '    提示：这是 ffmpeg full build，占了包体大头。想瘦身可换精简构建（只需 AAC 解码 + WAV 输出），' -ForegroundColor DarkGray
            Write-Host '          先下载 ffmpeg-release-essentials 一类的 exe，再用 -FfmpegPath <路径> 指定。' -ForegroundColor DarkGray
        }
        $bundled += 'ffmpeg'
    }
    else {
        Write-Warning ' 没找到 ffmpeg.exe，本包将无法播放 B站（AAC）音轨；装好 ffmpeg 后重跑本脚本即可。'
    }
}

Write-Step '检查 yt-dlp（YouTube 搜索 / 播放需要，可选）'
$ytdlp = Get-YtDlpPath
if ($ytdlp) {
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    Copy-Item -LiteralPath $ytdlp -Destination (Join-Path $binDir 'yt-dlp.exe') -Force
    Write-Host "    已内置 yt-dlp.exe ← $ytdlp" -ForegroundColor DarkGray
    $bundled += 'yt-dlp'
}
else {
    Write-Host '    本包不含 yt-dlp（pip 版只是 0.1MB 启动垫片，换台没装 Python 的机器跑不起来，不能随包）。' -ForegroundColor DarkGray
    Write-Host '    要支持 YouTube：把独立 exe 下载到 dist\bin\ 再重跑脚本，或让使用者自己放：' -ForegroundColor DarkGray
    Write-Host '    https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -ForegroundColor DarkGray
}

# ---------- 4. 使用说明 ----------
Write-Step '写入《使用说明.txt》'
$guide = Join-Path $dist '使用说明.txt'

if ($bundled -contains 'ffmpeg') {
    $ffmpegNote = '  - ffmpeg：本包已内置（bin\ffmpeg.exe），开箱即用。'
}
else {
    $ffmpegNote = @'
  - ffmpeg：本包未内置。B站音轨是 AAC，CEF 不能直接解码，必须靠 ffmpeg 实时转码；
    装好后放到本目录的 bin\ffmpeg.exe，或设环境变量 MUSICPLAYER_FFMPEG。
'@
}

if ($bundled -contains 'yt-dlp') {
    $ytdlpNote = '  - yt-dlp：本包已内置（bin\yt-dlp.exe），YouTube 搜索 / 播放开箱即用。'
}
else {
    $ytdlpNote = @'
  - yt-dlp：本包未内置，影响 YouTube 搜索和播放（B站不受影响）。任选一种装法：
      1) 下载 yt-dlp.exe 放到本目录的 bin\ 下
         https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe
      2) 已装 Python 时执行：python -m pip install --user -U "yt-dlp[default]"
'@
}

$guideText = @"
音乐播放器 $version（Windows 10/11 x64 便携版）
==================================================

一、怎么用
  1. 把整个文件夹解压到任意可写位置（例如 D:\MusicPlayer，不要放 C:\Program Files，
     那里的程序目录没有写权限）。
  2. 双击 MusicPlayer.Host.exe 启动。
  3. 第一次启动会让你选数据目录（默认 D:\MusicPlayerData），缓存 / 播放列表 / 设置 / 日志都放这里。
     选择记在 exe 同目录的 userdata.txt；以后想换可以在播放器「设置 → 数据目录」里改（改完重启生效），
     也可以删掉 userdata.txt 回到默认位置。没有 D 盘或目录不可写时自动改用
     %LOCALAPPDATA%\MusicPlayer\ver2，页面上会显示实际用的位置。

本包已自带 .NET 运行时，目标机器不需要安装 .NET 8，也不需要 Visual Studio。

二、系统要求
  - Windows 10 / 11 x64
  - 「Microsoft Visual C++ 2015–2022 可再发行组件 (x64)」——CefSharp 依赖它。
    绝大多数机器已自带；若双击后闪退并提示缺少 vcruntime140.dll，装一次即可：
    https://aka.ms/vs/17/release/vc_redist.x64.exe

三、在线播放需要的外部工具
$ffmpegNote
$ytdlpNote
  - 装好后把 yt-dlp.exe 放在 exe 同目录或 bin\ 子目录都能被自动找到，不需要改 PATH。
  - 便携模式（可选）：在本目录放一个空的 portable.txt，数据和缓存就落在本目录的 data\，
    整个文件夹可以拷进 U 盘带走。

四、常见问题
  - 双击没反应 / 闪退：先在命令行里运行 MusicPlayer.Host.exe 看报错；
    多半是数据目录不可写，用 --user-data <目录> 换一个位置。
  - 想重新选数据目录：执行 MusicPlayer.Host.exe --pick-user-data，或删掉 userdata.txt 再启动，
    也可以在播放器「设置 → 数据目录」里点「更改…」。
  - 界面空白：本目录必须保留 web\ 子目录（前端产物），不要把 exe 单独拷走。
  - 播放 B站/YouTube 报错：见「三」，缺 ffmpeg 或 yt-dlp。
  - 只播本地文件 / https 直链：不需要任何外部工具。

五、卸载
  删除本文件夹，再删除数据目录（默认 D:\MusicPlayerData，或 portable.txt 模式下的 data\）。
"@
Set-Content -LiteralPath $guide -Value $guideText -Encoding utf8

# ---------- 5. 自检 ----------
Write-Step '产物自检'
$required = @(
    'MusicPlayer.Host.exe',
    'CefSharp.Core.dll',
    'libcef.dll',
    'CefSharp.BrowserSubprocess.exe',
    'web\index.html'
)
$missing = @()
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $dist $item))) { $missing += $item }
}
if ($missing.Count -gt 0) {
    throw "产物缺少关键文件：$($missing -join ', ')"
}

$distBytes = (Get-ChildItem -LiteralPath $dist -Recurse -File | Measure-Object -Property Length -Sum).Sum
$distMb = [math]::Round($distBytes / 1MB, 1)
Write-Host "    目录结构 OK，dist\ 共 $distMb MB" -ForegroundColor DarkGray
Get-ChildItem -LiteralPath $dist | Sort-Object Name |
    Format-Table @{ n = '名称'; e = { $_.Name } }, @{ n = '类型'; e = { if ($_.PSIsContainer) { '目录' } else { '文件' } } } -AutoSize | Out-String | Write-Host

# ---------- 6. 压缩 ----------
if ($NoZip) {
    Write-Step '跳过压缩（-NoZip）'
}
else {
    Write-Step '打包 ZIP'
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $zipPath = Join-Path $artifacts "music-player-$version-$Runtime.zip"
    if (Test-Path -LiteralPath $zipPath) {
        try {
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction Stop
        }
        catch {
            # 上一次的 zip 可能正被资源管理器预览 / 打开着，换个带时间戳的名字，别让整个打包白跑
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $zipPath = Join-Path $artifacts "music-player-$version-$Runtime-$stamp.zip"
            Write-Warning "  旧 zip 被占用，本次写到 $zipPath"
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # includeBaseDirectory = $false：资源管理器「全部解压缩」会自动按 zip 名建一层文件夹
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $dist,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "    $zipPath （$zipMb MB）" -ForegroundColor Green
}

Write-Step '完成'
Write-Host "  可分发内容：$dist" -ForegroundColor Green
if (-not $NoZip) {
    Write-Host "  拷给别人：  $zipPath" -ForegroundColor Green
}
