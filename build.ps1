param(
    [string]$Config     = "Release",
    [string]$GameDir    = "F:\Steam\steamapps\common\Slay the Spire 2",
    [string]$LoaderDir  = "D:\A-Developing\main\sts2\tools\ModVersionLoader",
    [string]$SdkRoot    = "D:\A-Developing\tools\sts2_sdk_by_version",
    [switch]$NoLocalDeploy,
    [switch]$StageWorkshop
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$ModId       = "StandardSeedHost"

# ----------------------------------------------------------------------------
# 版本分发机制（与 AutoModSubscriber v0.1.5 同一套）
#
# 工坊条目只发一份内容，所有 Steam 分支拿到的字节完全相同：
#
#   <ModId>/
#   ├── mod_manifest.json              一份版本号 + min_game_version = 最低支持版本
#   ├── <ModId>.dll                    ModVersionLoader 启动器（游戏只加载这一个）
#   └── bin/
#       ├── g0.107.1/                  各游戏版本各自的实现
#       │   └── <ModId>.Impl.dll
#       └── g0.111.0/
#           └── <ModId>.Impl.dll
#
# 游戏只加载 <mod.path>/<manifest.id>.dll（ModManager.TryLoadMod），bin/ 下的实现
# 不会被游戏误加载，由启动器读 release_info.json 挑选后加载。
#
# 关键：min_game_version 必须写「最低支持版本」而不是最新版本，否则老分支会被
# 游戏自身的版本检查挡下（实测：写 0.111.0 时正式版 v0.107.1 直接拒绝加载）。
#
# 注意 SDK 与版本目录是两件事：
#   - 版本目录名 = 游戏 release_info.json 里的版本（启动器按它匹配），故为 g0.111.0；
#   - 编译用 SDK  = 该分支实际安装的二进制快照。v0.111.0 被 Steam 以同版本号重发过，
#     当前安装的是 build24724944（9,757,184 B，sts2 装配件 0.1.0.0），
#     与旧的 v0.111.0 快照（10,655,744 B，装配件 1.0.0.0）不是同一份，故用新快照编译。
# ----------------------------------------------------------------------------

$Targets = @(
    @{ Compat = "v107"; Sdk = "v0.107.1";              VersionDir = "g0.107.1" },
    @{ Compat = "v111"; Sdk = "v0.111.0+build24724944"; VersionDir = "g0.111.0" }
)

# 启动器只依赖 [ModInitializer] 与 GodotSharp，不碰游戏内部 API，编译用最新安装快照。
$LoaderSdk = "v0.111.0+build24724944"

# dotnet 可能不在当前会话 PATH 里（宿主子进程环境变量可能为空），补一个兜底。
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $fallback = "C:\Program Files\dotnet"
    if (Test-Path "$fallback\dotnet.exe") {
        $env:PATH = "$fallback;$env:PATH"
        Write-Host "dotnet 不在 PATH，已临时加入 $fallback" -ForegroundColor DarkGray
    } else {
        throw "找不到 dotnet，请先安装 .NET 9 SDK。"
    }
}

Write-Host "=== $ModId Build (version-bundled, multi-implementation) ===" -ForegroundColor Cyan
Write-Host "Project: $ProjectRoot"
Write-Host "Config:  $Config"

# --- 1. 读取 mod 版本（csproj 为唯一真源） -----------------------------------
$csprojPath = Join-Path $ProjectRoot "$ModId.csproj"
[xml]$csproj = Get-Content $csprojPath
$modVersion = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $modVersion) { throw "Could not read <Version> from $csprojPath" }
Write-Host "Mod version: $modVersion" -ForegroundColor Cyan

# min_game_version = 所有实现里最低的游戏版本，保证老分支也能加载
$minGameVersion = ($Targets | ForEach-Object { $_.VersionDir -replace '^g', '' } |
                   Sort-Object { [version]$_ } | Select-Object -First 1)
Write-Host "min_game_version: $minGameVersion  (最低支持版本)" -ForegroundColor Cyan

# --- 2. 组装目录 -------------------------------------------------------------
$stageRoot = Join-Path $ProjectRoot "build\mods\$ModId"
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

# --- 3. 逐个编译实现 ---------------------------------------------------------
Write-Host "[1/4] Building implementations..." -ForegroundColor Yellow
$built = @()
foreach ($t in $Targets) {
    $sdkDir = Join-Path $SdkRoot $t.Sdk
    if (-not (Test-Path "$sdkDir\sts2.dll")) {
        throw "SDK not found for $($t.Compat): $sdkDir\sts2.dll（该版本游戏 SDK 尚未存档）"
    }

    Write-Host "  [$($t.Compat)] -> $($t.VersionDir)  (SDK $($t.Sdk))" -ForegroundColor DarkGray
    Push-Location $ProjectRoot
    try {
        dotnet build -c $Config --nologo -v q `
            /p:GameCompat=$($t.Compat) `
            /p:Sts2DataDir=$sdkDir
        if ($LASTEXITCODE -ne 0) { throw "build failed for $($t.Compat)" }
    } finally { Pop-Location }

    $implDll = Join-Path $ProjectRoot ".godot\mono\temp\bin\$Config\$ModId.Impl.dll"
    if (-not (Test-Path $implDll)) { throw "implementation DLL not found: $implDll" }

    $destDir = Join-Path $stageRoot "bin\$($t.VersionDir)"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item $implDll (Join-Path $destDir "$ModId.Impl.dll") -Force
    $size = (Get-Item (Join-Path $destDir "$ModId.Impl.dll")).Length
    Write-Host "       OK  bin\$($t.VersionDir)\$ModId.Impl.dll ($size bytes)" -ForegroundColor Green
    $built += $t.VersionDir
}

# --- 4. 构建启动器 -----------------------------------------------------------
Write-Host "[2/4] Building version loader..." -ForegroundColor Yellow
$loaderSdkDir = Join-Path $SdkRoot $LoaderSdk
Push-Location $LoaderDir
try {
    dotnet build -c $Config --nologo -v q `
        /p:LoaderAssemblyName=$ModId `
        /p:Sts2DataDir=$loaderSdkDir
    if ($LASTEXITCODE -ne 0) { throw "loader build failed" }
} finally { Pop-Location }

$loaderDll = Join-Path $LoaderDir ".godot\mono\temp\bin\$Config\$ModId.dll"
if (-not (Test-Path $loaderDll)) { throw "loader DLL not found: $loaderDll" }
Copy-Item $loaderDll (Join-Path $stageRoot "$ModId.dll") -Force
Write-Host "  OK  $ModId.dll ($((Get-Item $loaderDll).Length) bytes)" -ForegroundColor Green

# --- 5. 写 manifest（版本 + 最低支持版本） -----------------------------------
$manifestSrc = Join-Path $ProjectRoot "mod_manifest.json"
$manifest = Get-Content $manifestSrc -Raw | ConvertFrom-Json
$manifest.version = $modVersion
$manifest.min_game_version = $minGameVersion
$manifestText = $manifest | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText(
    (Join-Path $stageRoot "mod_manifest.json"),
    $manifestText,
    (New-Object System.Text.UTF8Encoding($false)))

# --- 6. 打包自检 -------------------------------------------------------------
$rootDlls = @(Get-ChildItem $stageRoot -Filter *.dll -File)
if ($rootDlls.Count -ne 1 -or $rootDlls[0].Name -ne "$ModId.dll") {
    throw "package self-check failed: root must contain exactly $ModId.dll, found: $($rootDlls.Name -join ', ')"
}
foreach ($v in $built) {
    $implPath = Join-Path $stageRoot "bin\$v\$ModId.Impl.dll"
    if (-not (Test-Path $implPath)) { throw "package self-check failed: missing $implPath" }
}
Write-Host "[3/4] Package self-check OK  (root: 1 loader, bin/: $($built.Count) implementation(s))" -ForegroundColor Green

# --- 7. 本地部署 -------------------------------------------------------------
if (-not $NoLocalDeploy) {
    $localMods = Join-Path $GameDir "mods"
    if (-not (Test-Path $localMods)) { throw "mods folder not found: $localMods" }
    $target = Join-Path $localMods $ModId

    $running = Get-Process -Name "SlayTheSpire2" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host ""
        Write-Host "  游戏正在运行（PID $($running.Id -join ', ')），mod 文件被占用，无法部署。" -ForegroundColor Red
        Write-Host "  请先完全退出游戏，再重新运行本脚本。" -ForegroundColor Red
        Write-Host "  （包体已组装好，位于 $stageRoot）" -ForegroundColor DarkGray
        exit 1
    }

    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item "$stageRoot\*" $target -Recurse -Force
    Write-Host "  OK  deployed to $target" -ForegroundColor Green
}

# --- 8. 同步 torelease 快照 + 工坊 workspace ---------------------------------
$toRelease = Join-Path $ProjectRoot "torelease\$ModId"
if (Test-Path $toRelease) { Remove-Item $toRelease -Recurse -Force }
New-Item -ItemType Directory -Force -Path $toRelease | Out-Null
Copy-Item "$stageRoot\*" $toRelease -Recurse -Force
Write-Host "  OK  snapshot -> $toRelease" -ForegroundColor Green

if ($StageWorkshop) {
    Write-Host "[4/4] Staging workshop workspace content..." -ForegroundColor Yellow
    $wsContent = Join-Path (Split-Path $ProjectRoot -Parent) "_workshop_workspaces\$ModId\content"
    if (-not (Test-Path (Split-Path $wsContent -Parent))) {
        throw "workshop workspace not found: $(Split-Path $wsContent -Parent)"
    }
    if (Test-Path $wsContent) { Remove-Item $wsContent -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $wsContent | Out-Null
    Copy-Item "$stageRoot\*" $wsContent -Recurse -Force
    Write-Host "  OK  $wsContent" -ForegroundColor Green
    Write-Host "      上传请另行执行 ModUploader（本脚本不自动上传）" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Package: $stageRoot" -ForegroundColor Green
Get-ChildItem $stageRoot -Recurse -File | ForEach-Object {
    Write-Host ("  {0,-46} {1,8} bytes" -f $_.FullName.Replace("$stageRoot\", ""), $_.Length)
}
Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
