param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$RootDir      = $PSScriptRoot
$SrcDir       = Join-Path $RootDir "src\NAITool"
$LauncherDir  = Join-Path $RootDir "src\NAIToolLauncher"
$BuildDir     = Join-Path $RootDir "build\$Configuration"
$MainBuildDir = Join-Path $BuildDir "bin"
$PublishDir   = Join-Path $RootDir "publish\NAITool"
$BinDir       = Join-Path $PublishDir "bin"

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

# 清理旧的发布目录
if (Test-Path $MainBuildDir) {
    Remove-Item -Recurse -Force $MainBuildDir
}
foreach ($file in @("NAITool.exe", "NAITool.dll", "NAITool.deps.json", "NAITool.runtimeconfig.json", "NAITool.pdb")) {
    $path = Join-Path $BuildDir $file
    if (Test-Path $path) {
        Remove-Item -Force $path
    }
}

if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}
New-Item -ItemType Directory -Force $PublishDir | Out-Null
New-Item -ItemType Directory -Force $BinDir | Out-Null

Write-Host "正在发布主程序..." -ForegroundColor Green
Invoke-Dotnet publish "$SrcDir\NAITool.csproj" -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o "$BinDir"

Write-Host "正在发布启动器..." -ForegroundColor Green
Invoke-Dotnet publish "$LauncherDir\NAIToolLauncher.csproj" -c $Configuration -r $Runtime --self-contained false -p:PublishSingleFile=true -o "$PublishDir"

# ── 从源仓库直接复制数据文件到发布根目录 ──
Write-Host "复制数据文件..." -ForegroundColor Green

$AssetsDir = Join-Path $PublishDir "assets"
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "img") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "tagsheet") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "wildcards") | Out-Null

Copy-Item -Force "$RootDir\assets\img\*.png"          (Join-Path $AssetsDir "img")
Copy-Item -Force "$RootDir\assets\tagsheet\*.csv"     (Join-Path $AssetsDir "tagsheet")
Copy-Item -Recurse -Force "$RootDir\assets\wildcards\*" (Join-Path $AssetsDir "wildcards")

$ModelsUpscalerDir = Join-Path $PublishDir "models\upscaler"
New-Item -ItemType Directory -Force $ModelsUpscalerDir | Out-Null
Copy-Item -Force "$RootDir\models\upscaler\*"          $ModelsUpscalerDir

# ── 创建 user/ 目录结构并填入默认数据 ──
$UserDir = Join-Path $PublishDir "user"
New-Item -ItemType Directory -Force (Join-Path $UserDir "config")             | Out-Null
New-Item -ItemType Directory -Force (Join-Path $UserDir "fxpresets")          | Out-Null
New-Item -ItemType Directory -Force (Join-Path $UserDir "userprompts")        | Out-Null
New-Item -ItemType Directory -Force (Join-Path $UserDir "wildcards")          | Out-Null
New-Item -ItemType Directory -Force (Join-Path $UserDir "automation\presets") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $UserDir "vibe")              | Out-Null

Copy-Item -Force "$RootDir\assets\fxpresets\*.json" (Join-Path $UserDir "fxpresets")

$WildcardsSrc = Join-Path $RootDir "assets\wildcards"
if (Test-Path $WildcardsSrc) {
    Copy-Item -Recurse -Force "$WildcardsSrc\*" (Join-Path $UserDir "wildcards")
}

# ── 其他根级目录 ──
New-Item -ItemType Directory -Force (Join-Path $PublishDir "output") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $PublishDir "logs")   | Out-Null

# ── 清理不需要的文件 ──
foreach ($pdb in @((Join-Path $BinDir "NAITool.pdb"), (Join-Path $PublishDir "NAITool.pdb"))) {
    if (Test-Path $pdb) { Remove-Item -Force $pdb }
}

Write-Host "发布完成！文件位于: $PublishDir" -ForegroundColor Green
