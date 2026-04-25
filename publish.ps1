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
$AppFileBase  = "NAI Utility Tool"
$PublishDir   = Join-Path $RootDir "publish\$AppFileBase"
$BinDir       = Join-Path $PublishDir "bin"
$LegacyPublishDir = Join-Path $RootDir "publish\NAITool"

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Remove-DirectoryIfExists {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return
    }

    Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Attributes = 'Normal' }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Remove-PublishBloat {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return
    }

    $patterns = @(
        "*.pdb",
        "*.mdb",
        "*.dbg",
        "*.lib",
        "DirectML.Debug.dll",
        "createdump.exe",
        "mscordaccore.dll",
        "mscordaccore_*.dll",
        "mscordbi.dll"
    )

    foreach ($pattern in $patterns) {
        Get-ChildItem -LiteralPath $Path -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
}

# 清理旧的发布目录
Remove-DirectoryIfExists -Path $MainBuildDir
foreach ($file in @("NAITool.exe", "NAITool.dll", "NAITool.deps.json", "NAITool.runtimeconfig.json", "NAITool.pdb", "NAIUtilityTool.exe", "NAIUtilityTool.dll", "NAIUtilityTool.deps.json", "NAIUtilityTool.runtimeconfig.json", "NAIUtilityTool.pdb", "$AppFileBase.exe", "$AppFileBase.dll", "$AppFileBase.deps.json", "$AppFileBase.runtimeconfig.json", "$AppFileBase.pdb")) {
    $path = Join-Path $BuildDir $file
    if (Test-Path $path) {
        Remove-Item -Force $path
    }
}

Remove-DirectoryIfExists -Path $PublishDir
if ($LegacyPublishDir -ne $PublishDir) {
    Remove-DirectoryIfExists -Path $LegacyPublishDir
}
New-Item -ItemType Directory -Force $PublishDir | Out-Null
New-Item -ItemType Directory -Force $BinDir | Out-Null

Write-Host "正在发布主程序..." -ForegroundColor Green
Invoke-Dotnet publish "$SrcDir\NAITool.csproj" -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "$BinDir"

Write-Host "正在发布启动器..." -ForegroundColor Green
Invoke-Dotnet publish "$LauncherDir\NAIToolLauncher.csproj" -c $Configuration -r $Runtime --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$PublishDir"

# ── 从源仓库直接复制数据文件到发布根目录 ──
Write-Host "复制数据文件..." -ForegroundColor Green

$AssetsDir = Join-Path $PublishDir "assets"
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "tagsheet") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $AssetsDir "wildcards") | Out-Null

$PublishImageFiles = Get-ChildItem -Path "$RootDir\assets\img" -Filter "*.png" |
    Where-Object { $_.Name -notlike "MaidAeka*.png" }
if ($PublishImageFiles) {
    $PublishImgDir = Join-Path $AssetsDir "img"
    New-Item -ItemType Directory -Force $PublishImgDir | Out-Null
    $PublishImageFiles | Copy-Item -Destination $PublishImgDir -Force
}
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
Remove-PublishBloat -Path $PublishDir

Write-Host "发布完成！文件位于: $PublishDir" -ForegroundColor Green
